using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Server.Tts
{
    /// <summary>
    /// ZipVoice TTS —— 纯 PCM 输出版
    ///
    /// 下行协议与 TtsMatchaIcefall 完全一致：
    /// 16000Hz / 16bit / 单声道 / 小端序裸 PCM，固定 640 字节（20ms）一帧，按音频节拍发送。
    /// ZipVoice 的 vocoder 通常是 24kHz，因此会启用 PcmResampler 转成 16kHz。
    /// </summary>
    public class TtsZipVoice
    {
        OfflineTts ot;
        OfflineTtsConfig config;
        OfflineTtsGenerationConfig genConfig;
        bool initDone = false;
        int SampleRate = 0;
        string modelPath;
        public IWebSocketConnection client = null;
        float volume = 1f;

        private const int TARGET_SAMPLE_RATE = 16000;
        private const int FRAME_SAMPLES = 320;
        private const int FRAME_BYTES = FRAME_SAMPLES * 2;
        private const int MAX_QUEUE_FRAMES = 200;   // 4 秒上限

        private readonly ConcurrentQueue<byte[]> sendQueue = new();
        private CancellationTokenSource cts = new();
        private Task generateTask = Task.CompletedTask;
        private readonly SemaphoreSlim generateGate = new(1, 1);

        private readonly List<byte> pcmFrameBuffer = new List<byte>();
        private readonly object frameBufferLock = new object();

        public Action OnPlaybackFinished;

        private volatile bool isGenerating = false;
        private volatile bool sendRunning = true;
        private Thread sendThread;
        private bool finishedNotified = true;

        private PcmResampler resampler = null;

        public TtsZipVoice()
        {
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia";
            config = new OfflineTtsConfig();
            config.Model.ZipVoice.Encoder = Path.Combine(modelPath, "encoder.int8.onnx");
            config.Model.ZipVoice.Decoder = Path.Combine(modelPath, "decoder.int8.onnx");
            config.Model.ZipVoice.Lexicon = Path.Combine(modelPath, "lexicon.txt");
            config.Model.ZipVoice.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.Model.ZipVoice.DataDir = Path.Combine(modelPath, "espeak-ng-data");
            config.Model.ZipVoice.Vocoder = Path.Combine(modelPath, "vocos_24khz.onnx");

            float[] samples = WavUtility.ReadMono16kWavToFloat(Path.Combine(modelPath, "test_wavs/wanwan.wav"));
            genConfig = new OfflineTtsGenerationConfig();
            genConfig.ReferenceAudio = samples;
            genConfig.ReferenceSampleRate = 16000;
            genConfig.ReferenceText = "今天我们准备了澳白、拿铁、美式和热牛奶，您想要哪一款，请跟我说。";
            genConfig.NumSteps = 4;
            genConfig.Extra["min_char_in_sentence"] = "10";

            config.Model.NumThreads = 5;
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            Console.WriteLine("[TTS] 模型采样率: " + SampleRate);

            if (SampleRate != TARGET_SAMPLE_RATE)
            {
                resampler = new PcmResampler(SampleRate, TARGET_SAMPLE_RATE);
                Console.WriteLine($"[TTS] 启用重采样 {SampleRate} -> {TARGET_SAMPLE_RATE} Hz");
            }

            initDone = true;

            sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TtsZipPcmSend" };
            sendThread.Start();
        }

        public void UpdateClient(IWebSocketConnection connection)
        {
            client = connection;
            if (connection == null)
            {
                Interrupt();
            }
        }

        public void Generate(string text, float speed, int speakerId)
        {
            if (!initDone)
            {
                Console.WriteLine("[TTS] 未完成初始化");
                return;
            }

            generateGate.Wait();
            try
            {
                CancelCurrent();
                ClearBuffers();
                resampler?.Reset();

                var localCts = new CancellationTokenSource();
                cts = localCts;
                isGenerating = true;
                finishedNotified = false;

                generateTask = Task.Run(() =>
                {
                    try
                    {
                        OfflineTtsCallbackProgressWithArg callback = (samples, n, progress, arg) =>
                            OnAudioData(samples, n, localCts.Token);
                        ot.GenerateWithConfig(text, genConfig, callback);

                        if (!localCts.IsCancellationRequested)
                        {
                            FlushTailFrame();
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("[TTS] 生成异常: " + e.Message);
                    }
                    finally
                    {
                        isGenerating = false;
                    }
                }, localCts.Token);
            }
            finally
            {
                generateGate.Release();
            }
        }

        public void Interrupt()
        {
            if (!generateGate.Wait(1000))
            {
                Console.WriteLine("[TTS] 打断时获取锁超时，强制取消");
                CancelCurrent();
                ClearBuffers();
                isGenerating = false;
                return;
            }

            try
            {
                CancelCurrent();
                ClearBuffers();
                isGenerating = false;
                finishedNotified = true;
                Console.WriteLine("[TTS] 已打断生成");
            }
            finally
            {
                generateGate.Release();
            }
        }

        private void CancelCurrent()
        {
            var local = cts;
            if (local != null && !local.IsCancellationRequested)
            {
                try { local.Cancel(); } catch { }
            }
            try { generateTask.Wait(500); } catch { }
        }

        private void ClearBuffers()
        {
            while (sendQueue.TryDequeue(out _)) { }
            lock (frameBufferLock)
            {
                pcmFrameBuffer.Clear();
            }
        }

        private int OnAudioData(nint samples, int n, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                Console.WriteLine("[TTS] 停止生成（回调中断）");
                return 0;
            }

            if (n <= 0)
            {
                return 0;
            }

            int originalN = n;
            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);

            if (resampler != null)
            {
                floatData = resampler.Process(floatData, n);
                n = floatData.Length;
                if (n == 0)
                {
                    return originalN;
                }
            }

            byte[] pcmBytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                float v = floatData[i] * volume;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                short s = (short)(v * 32767f);
                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            EnqueueFrames(pcmBytes);
            return originalN;
        }

        private void EnqueueFrames(byte[] pcmBytes)
        {
            lock (frameBufferLock)
            {
                pcmFrameBuffer.AddRange(pcmBytes);
                while (pcmFrameBuffer.Count >= FRAME_BYTES)
                {
                    byte[] frame = new byte[FRAME_BYTES];
                    pcmFrameBuffer.CopyTo(0, frame, 0, FRAME_BYTES);
                    pcmFrameBuffer.RemoveRange(0, FRAME_BYTES);
                    EnqueueFrame(frame);
                }
            }
        }

        private void FlushTailFrame()
        {
            lock (frameBufferLock)
            {
                if (pcmFrameBuffer.Count == 0) return;
                byte[] frame = new byte[FRAME_BYTES];
                int count = Math.Min(pcmFrameBuffer.Count, FRAME_BYTES);
                pcmFrameBuffer.CopyTo(0, frame, 0, count);
                pcmFrameBuffer.Clear();
                EnqueueFrame(frame);
            }
        }

        private void EnqueueFrame(byte[] frame)
        {
            while (sendQueue.Count >= MAX_QUEUE_FRAMES)
            {
                if (!sendQueue.TryDequeue(out _)) break;
            }
            sendQueue.Enqueue(frame);
        }

        private void SendLoop()
        {
            const int frameIntervalMs = FRAME_SAMPLES * 1000 / TARGET_SAMPLE_RATE; // 20
            long nextTick = Environment.TickCount64;

            while (sendRunning)
            {
                if (sendQueue.TryDequeue(out byte[] frame))
                {
                    var conn = client;
                    if (conn != null && conn.IsAvailable)
                    {
                        try { conn.Send(frame); }
                        catch (Exception e) { Console.WriteLine("[TTS] 发送异常: " + e.Message); }
                    }

                    nextTick += frameIntervalMs;
                    long delay = nextTick - Environment.TickCount64;
                    if (delay > 0)
                    {
                        Thread.Sleep((int)delay);
                    }
                    else if (delay < -200)
                    {
                        nextTick = Environment.TickCount64;
                    }
                }
                else
                {
                    if (!isGenerating && !finishedNotified)
                    {
                        finishedNotified = true;
                        OnPlaybackFinished?.Invoke();
                    }
                    nextTick = Environment.TickCount64;
                    Thread.Sleep(5);
                }
            }
        }

        public void Stop()
        {
            sendRunning = false;
            sendThread?.Join(500);
            Interrupt();
            ot?.Dispose();
            ot = null;
            generateGate.Dispose();
        }
    }
}
