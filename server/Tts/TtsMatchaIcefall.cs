using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Server.Tts
{
    /// <summary>
    /// Matcha-Icefall TTS —— 纯 PCM 输出版
    ///
    /// 下行协议：16000Hz / 16bit / 单声道 / 小端序裸 PCM，无编解码、无长度前缀、无比特率概念。
    /// 模型原生就是 16kHz（vocos-16khz-univ.onnx），正常情况下零转换直通；
    /// 若换了非 16kHz 的 vocoder，则用自带的 PcmResampler 兜底（抗混叠 sinc 插值，不引入高频噪声）。
    ///
    /// 发送策略：固定 640 字节（20ms）一帧，由独立线程按音频时钟节拍发送。
    /// 帧长与 ESP32 抖动缓冲的帧长严格一致，客户端无需拼帧即可对齐。
    /// </summary>
    public class TtsMatchaIcefall
    {
        OfflineTts ot;
        OfflineTtsConfig config;
        bool initDone = false;
        int SampleRate = 0;
        string modelPath;
        public IWebSocketConnection client = null;
        float volume = 1f;

        // ===== 音频格式常量（全链路唯一一套）=====
        private const int TARGET_SAMPLE_RATE = 16000;
        private const int FRAME_SAMPLES = 320;                    // 20ms @ 16kHz
        private const int FRAME_BYTES = FRAME_SAMPLES * 2;        // 640 字节

        // 发送队列上限：200 帧 = 4 秒。超出说明网络堵了，
        // 继续堆积只会让延迟越拉越大且吃内存，直接丢最旧的。
        private const int MAX_QUEUE_FRAMES = 200;

        private readonly ConcurrentQueue<byte[]> sendQueue = new();
        private CancellationTokenSource cts = new();
        private Task generateTask = Task.CompletedTask;
        private readonly SemaphoreSlim generateGate = new(1, 1);

        // 不足一帧的 PCM 残留
        private readonly List<byte> pcmFrameBuffer = new List<byte>();
        private readonly object frameBufferLock = new object();

        // 播放完毕回调，队列耗尽且生成结束时触发
        public Action OnPlaybackFinished;

        private volatile bool isGenerating = false;
        private volatile bool sendRunning = true;
        private Thread sendThread;

        // 模型采样率 != 16000 时才启用（正常为 null，零开销直通）
        private PcmResampler resampler = null;

        // 本轮是否已经通知过播放完毕，避免空闲时反复触发回调
        private bool finishedNotified = true;

        public TtsMatchaIcefall()
        {
            modelPath = Environment.CurrentDirectory + "/matcha-icefall-zh-baker";
            config = new OfflineTtsConfig();
            config.Model.Matcha.AcousticModel = Path.Combine(modelPath, "model-steps-3.onnx");
            config.Model.Matcha.Vocoder = Path.Combine(modelPath, "vocos-16khz-univ.onnx");
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
            Console.WriteLine("[TTS] 模型采样率: " + SampleRate);

            if (SampleRate != TARGET_SAMPLE_RATE)
            {
                resampler = new PcmResampler(SampleRate, TARGET_SAMPLE_RATE);
                Console.WriteLine($"[TTS] 启用重采样 {SampleRate} -> {TARGET_SAMPLE_RATE} Hz");
            }
            else
            {
                Console.WriteLine($"[TTS] 采样率原生匹配 {TARGET_SAMPLE_RATE} Hz，零转换直通");
            }

            initDone = true;

            sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TtsPcmSend" };
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

                var localCts = new CancellationTokenSource();
                cts = localCts;
                isGenerating = true;
                finishedNotified = false;

                generateTask = Task.Run(() =>
                {
                    try
                    {
                        OfflineTtsCallback callback = (samples, n) =>
                            OnAudioData(samples, n, localCts.Token);
                        ot.GenerateWithCallback(text, speed, speakerId, callback);

                        // 生成结束：把不足一帧的尾巴零填充后补发，避免最后几十毫秒被吞掉
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
            // 不用 lock 包住 Wait，避免生成线程回调阻塞时死锁
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
            // 只等一小会儿；回调里检测到 token 即返回 0，SherpaOnnx 会自行收尾
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

        /// <summary>
        /// TTS 回调：float 采样 → 16bit PCM → 切成 20ms 定长帧入队
        /// </summary>
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

            // 采样率不一致时重采样（正常 16kHz 模型走不到这里）
            if (resampler != null)
            {
                floatData = resampler.Process(floatData, n);
                n = floatData.Length;
                if (n == 0)
                {
                    return originalN;   // 非 0 = 继续生成
                }
            }

            byte[] pcmBytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                float v = floatData[i] * volume;
                // 先钳位到 [-1,1] 再放大，避免模型偶发溢出值绕回造成爆音
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                short s = (short)(v * 32767f);
                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            EnqueueFrames(pcmBytes);
            return originalN;   // 返回非 0 表示继续生成
        }

        /// <summary>
        /// 按 640 字节切帧入队；不足一帧的留到下次回调
        /// </summary>
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

        /// <summary>
        /// 生成结束时把残留的不足一帧数据零填充补齐后发出
        /// </summary>
        private void FlushTailFrame()
        {
            lock (frameBufferLock)
            {
                if (pcmFrameBuffer.Count == 0)
                {
                    return;
                }
                byte[] frame = new byte[FRAME_BYTES];
                int count = Math.Min(pcmFrameBuffer.Count, FRAME_BYTES);
                pcmFrameBuffer.CopyTo(0, frame, 0, count);
                pcmFrameBuffer.Clear();
                EnqueueFrame(frame);
            }
        }

        private void EnqueueFrame(byte[] frame)
        {
            // 有界队列：满了丢最旧，保证内存和端到端延迟都不失控
            while (sendQueue.Count >= MAX_QUEUE_FRAMES)
            {
                if (!sendQueue.TryDequeue(out _)) break;
            }
            sendQueue.Enqueue(frame);
        }

        /// <summary>
        /// 发送线程：按音频时钟节拍推送 20ms 帧。
        /// 用绝对时间基准累加，避免 Thread.Sleep 误差累积导致的欠载断续。
        /// </summary>
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
                        try
                        {
                            conn.Send(frame);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("[TTS] 发送异常: " + e.Message);
                        }
                    }

                    // 基于绝对时间对齐节拍
                    nextTick += frameIntervalMs;
                    long delay = nextTick - Environment.TickCount64;
                    if (delay > 0)
                    {
                        Thread.Sleep((int)delay);
                    }
                    else if (delay < -200)
                    {
                        // 落后超过 200ms（网络卡顿等），重置基准而不是疯狂追帧
                        nextTick = Environment.TickCount64;
                    }
                }
                else
                {
                    // 队列空：生成也结束了 -> 播放完毕（只通知一次）
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
