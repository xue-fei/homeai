using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Concentus;

namespace Server.Tts
{
    public class TtsMatchaIcefall
    {
        OfflineTts ot;
        OfflineTtsConfig config;
        bool initDone = false;
        int SampleRate = 0;
        string modelPath;
        public IWebSocketConnection client = null;
        float volume = 1f;

        private ConcurrentQueue<byte> sendQueue = new();
        private CancellationTokenSource cts = new();
        private Task generateTask = Task.CompletedTask;
        private readonly object generateLock = new();
        private int sendChunkSize = 2048;

        // 播放完毕回调，队列耗尽且生成结束时触发
        public Action OnPlaybackFinished;

        // 标记当前是否有生成任务正在进行
        private volatile bool isGenerating = false;

        // Opus encoding members
        private OpusCodec opusEncoder = null;
        private List<byte> pcmFrameBuffer = new List<byte>();
        private int opusFrameSize; // 每帧采样数
        private IResampler resampler = null;
        // 目标采样率：16000 Hz（ESP32 I2S 精确时钟，全链路统一）
        private const int TARGET_SAMPLE_RATE = 16000;
        // Opus 发送队列（用于速率对齐）
        private ConcurrentQueue<byte[]> opusSendQueue = new();
        private Thread opusSendThread = null;
        private volatile bool opusSendRunning = false;

        public TtsMatchaIcefall()
        {
            modelPath = Environment.CurrentDirectory + "/matcha-icefall-zh-baker";
            config = new OfflineTtsConfig();
            config.Model.Matcha.AcousticModel = Path.Combine(modelPath, "model-steps-3.onnx");
            config.Model.Matcha.Vocoder = Path.Combine(modelPath, "vocos-22khz-univ.onnx");
            config.Model.Matcha.Lexicon = Path.Combine(modelPath, "lexicon.txt");
            config.Model.Matcha.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.Model.Matcha.DictDir = Path.Combine(modelPath, "dict");
            config.Model.Matcha.LengthScale = 1f;
            config.Model.NumThreads = 5;
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";
            config.RuleFsts = modelPath + "/phone.fst" + ","
                        + modelPath + "/date.fst" + ","
                    + modelPath + "/number.fst";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            Console.WriteLine("SampleRate:" + SampleRate);

            if (!Directory.Exists(Environment.CurrentDirectory + "/audio"))
            {
                Directory.CreateDirectory(Environment.CurrentDirectory + "/audio");
            }
            initDone = true;

            Thread sendThread = new Thread(SendLoop) { IsBackground = true };
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
                Console.WriteLine("文字转语音未完成初始化");
                return;
            }

            lock (generateLock)
            {
                // 取消上一个任务
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                try { generateTask.Wait(500); } catch { }

                // 清空队列和帧缓冲区
                while (sendQueue.TryDequeue(out _)) { }
                while (opusSendQueue.TryDequeue(out _)) { }
                pcmFrameBuffer.Clear();

                var localCts = new CancellationTokenSource();
                cts = localCts;
                isGenerating = true;

                generateTask = Task.Run(() =>
                {
                    try
                    {
                        OfflineTtsCallback callback = (samples, n) =>
                            OnAudioData(samples, n, localCts.Token);
                        ot.GenerateWithCallback(text, speed, speakerId, callback);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("生成异常: " + e.Message);
                    }
                    finally
                    {
                        isGenerating = false;
                        // 生成结束后，如果队列也空了就立即通知
                        if (sendQueue.IsEmpty)
                        {
                            OnPlaybackFinished?.Invoke();
                        }
                    }
                }, localCts.Token);
            }
        }

        public void Interrupt()
        {
            lock (generateLock)
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    Console.WriteLine("[TTS] 已打断生成");
                }

                try { generateTask.Wait(500); } catch { }

                while (sendQueue.TryDequeue(out _)) { }
                while (opusSendQueue.TryDequeue(out _)) { }
                pcmFrameBuffer.Clear();
                isGenerating = false;
            }
        }

        private int OnAudioData(nint samples, int n, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                Console.WriteLine("[TTS] 停止生成（回调中断）");
                return 0;
            }

            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);

            // 重采样到目标采样率（22050 -> 16000）
            if (resampler != null)
            {
                int inLen = n;
                float[] resampledOut = new float[n * 2 + 64];
                int outLen = resampledOut.Length;
                var inSpan = floatData.AsSpan();
                var outSpan = resampledOut.AsSpan();
                resampler.ProcessInterleaved(inSpan, ref inLen, outSpan, ref outLen);
                // 用重采样后的数据（截取有效长度）
                float[] resampledData = new float[outLen];
                Array.Copy(resampledOut, resampledData, outLen);
                floatData = resampledData;
                n = outLen;
            }

            // 将 float 转为 16-bit PCM 字节
            byte[] pcmBytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp(floatData[i] * 32767f * volume, short.MinValue, short.MaxValue);
                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)(s >> 8 & 0xFF);
            }

            // 如果有 Opus 编码器，将 PCM 编码为 Opus 后入队（由发送线程按音频速率发送）
            if (opusEncoder != null)
            {
                // 再次检查取消状态，避免在取消后仍入队
                if (token.IsCancellationRequested)
                {
                    Console.WriteLine("[TTS] 停止生成（回调中断）");
                    return 0;
                }
                var opusPackets = new List<byte[]>();
                EncodeAndQueueOpus(pcmBytes, opusPackets);
                foreach (var packet in opusPackets)
                {
                    if (packet != null)
                    {
                        opusSendQueue.Enqueue(packet);
                    }
                }
            }
            else
            {
                Console.WriteLine("opusEncoder == null");
            }

            return n;
        }

        /// <summary>
        /// 启用 Opus 编码（TTS 输出将编码为 Opus 格式发送）
        /// TTS 模型原生采样率为 22050 Hz（由 ot.SampleRate 动态获取），需重采样到 16000 Hz 以匹配 ESP32/ASR 链路
        /// </summary>
        public void EnableOpusEncoding()
        {
            if (opusEncoder == null)
            {
                int targetRate = TARGET_SAMPLE_RATE; // 16000
                int modelRate = SampleRate;
                if (modelRate != targetRate)
                {
                    resampler = ResamplerFactory.CreateResampler(1, modelRate, targetRate, 5, Console.Out);
                    Console.WriteLine($"[TTS] 启用重采样 {modelRate} -> {targetRate} Hz");
                }
                opusEncoder = new OpusCodec(targetRate, 1, 24000);
                opusFrameSize = targetRate / 50; // 320 samples @ 16kHz
                Console.WriteLine($"[TTS] 已启用 Opus 编码，采样率: {targetRate} Hz (模型原生: {modelRate} Hz)");

                // 启动 Opus 发送线程（按音频速率发送）
                opusSendRunning = true;
                opusSendThread = new Thread(OpusSendLoop) { IsBackground = true };
                opusSendThread.Start();
            }
        }

        /// <summary>
        /// 将 PCM 字节缓冲并编码为 Opus 包列表（每个包独立，可直接作为 WebSocket 帧发送）
        /// </summary>
        private void EncodeAndQueueOpus(byte[] pcmBytes, List<byte[]> outputPackets)
        {
            pcmFrameBuffer.AddRange(pcmBytes);
            outputPackets.Clear();

            int frameBytes = opusFrameSize * 2;
            while (pcmFrameBuffer.Count >= frameBytes)
            {
                byte[] frame = pcmFrameBuffer.GetRange(0, frameBytes).ToArray();
                pcmFrameBuffer.RemoveRange(0, frameBytes);

                byte[] opusPacket = opusEncoder.Encode(frame);
                if (opusPacket != null)
                {
                    outputPackets.Add(opusPacket);
                }
            }
        }

        private void SendLoop()
        {
            var packetBuffer = new List<byte>();

            while (true)
            {
                packetBuffer.Clear();

                if (opusEncoder != null)
                {
                    // Opus 模式：每个 Opus 包已从 OnAudioData 直接发送，这里只处理残留在队列中的数据
                    while (packetBuffer.Count < sendChunkSize && sendQueue.TryDequeue(out byte b))
                    {
                        packetBuffer.Add(b);
                    }
                }
                else
                {
                    // PCM 兼容模式：按固定大小分块发送
                    while (packetBuffer.Count < sendChunkSize && sendQueue.TryDequeue(out byte b))
                    {
                        packetBuffer.Add(b);
                    }
                }

                if (packetBuffer.Count > 0)
                {
                    if (client != null && client.IsAvailable)
                    {
                        try
                        {
                            client.Send(packetBuffer.ToArray());
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("发送异常: " + e.Message);
                        }
                    }
                }
                else
                {
                    if (!isGenerating && sendQueue.IsEmpty)
                    {
                        OnPlaybackFinished?.Invoke();
                    }
                    Thread.Sleep(10);
                }
            }
        }

        /// <summary>
        /// Opus 发送线程：按音频速率（每 20ms 一帧）发送 Opus 包
        /// </summary>
        private void OpusSendLoop()
        {
            // 每帧时长 = opusFrameSize / TARGET_SAMPLE_RATE 秒 = 320/16000 = 20ms
            int frameIntervalMs = opusFrameSize * 1000 / TARGET_SAMPLE_RATE;

            while (opusSendRunning)
            {
                if (opusSendQueue.TryDequeue(out byte[] packet))
                {
                    if (client != null && client.IsAvailable)
                    {
                        try { client.Send(packet); }
                        catch (Exception e) { Console.WriteLine("Opus 包发送异常: " + e.Message); }
                    }
                    // 按音频速率等待
                    Thread.Sleep(frameIntervalMs);
                }
                else
                {
                    // 队列为空，短暂等待
                    Thread.Sleep(1);
                }
            }
        }

        public void Stop()
        {
            opusSendRunning = false;
            opusSendThread?.Join(500);
            Interrupt();
            ot?.Dispose();
        }
    }
}