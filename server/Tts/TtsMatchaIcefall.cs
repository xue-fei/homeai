using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Server.Tts
{
    /// <summary>
    /// Matcha-Icefall TTS —— 纯 PCM 流水线版
    ///
    /// 下行协议：16000Hz / 16bit / 单声道 / 小端序裸 PCM，无编解码、无长度前缀。
    ///
    /// 【为什么要流水线】
    /// 旧版是「生成一句 -> 等发完 -> 再生成下一句」，句子之间必然出现一个
    /// 等于推理耗时的空档期，ESP32 抖动缓冲被抽干后要重新预缓冲，听感就是
    /// 每句话都顿一下（"跳跃/卡顿"）。
    ///
    /// 新版把文本队列和 PCM 发送队列彻底解耦：
    ///   Enqueue(text)  -> textQueue -> [生成线程 串行推理] -> sendQueue -> [发送线程 按音频时钟]
    /// 生成线程一句接一句连续推理，PCM 不断往同一个 sendQueue 追加，
    /// 句子之间不清队列、不补零、不重置残留 —— 样本流严格连续，永不断流。
    ///
    /// 打断用 generation 代号实现：Interrupt() 自增代号，正在跑的推理回调
    /// 检测到代号变化立即返回 0，陈旧的排队请求也会被丢弃。
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

        // 每个 WebSocket 包聚合 3 帧 = 60ms。
        // 20ms 小包在 TCP 上极易被 Nagle 聚合成不规律突发，聚合后包数降到 1/3，
        // 抖动显著下降；ESP32 侧 onPcmReceived 本来就能处理任意长度。
        private const int FRAMES_PER_PACKET = 3;

        // 发送队列上限 400 帧 = 8 秒。生成远快于实时，正常会保持较满。
        private const int MAX_QUEUE_FRAMES = 400;

        // 开播水位：攒够 400ms PCM 才开始发送。
        // 首句推理有冷启动开销，先攒一点能吸收后续的推理抖动，
        // 配合 ESP32 的 120ms 预缓冲，端到端起播延迟约 0.5s，换来全程不断流。
        private const int START_WATERMARK_FRAMES = 20;

        private readonly ConcurrentQueue<byte[]> sendQueue = new();

        // 待合成文本队列（生成线程串行消费）
        private readonly ConcurrentQueue<PendingText> textQueue = new();
        private readonly SemaphoreSlim textSignal = new(0);

        // 打断代号：每次 Interrupt 自增，所有在途工作据此判定是否作废
        private volatile int generation = 0;

        // 不足一帧的 PCM 残留
        private readonly List<byte> pcmFrameBuffer = new List<byte>();
        private readonly object frameBufferLock = new object();

        // 整段播放完毕回调（文本队列空 + 无推理 + PCM 队列空）
        public Action OnPlaybackFinished;

        private volatile bool sendRunning = true;
        private volatile bool synthRunning = true;
        private volatile bool isGenerating = false;      // 生成线程是否正在推理
        private volatile bool started = false;           // 是否已过开播水位
        private volatile bool finishedNotified = true;

        private Thread sendThread;
        private Thread synthThread;

        // 模型采样率 != 16000 时才启用
        private PcmResampler resampler = null;

        private struct PendingText
        {
            public string Text;
            public float Speed;
            public int SpeakerId;
            public int Gen;
        }

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

            synthThread = new Thread(SynthLoop) { IsBackground = true, Name = "TtsSynth" };
            synthThread.Start();
            sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TtsPcmSend" };
            sendThread.Priority = ThreadPriority.AboveNormal;   // 节拍线程优先，减少调度抖动
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

        /// <summary>
        /// 追加一句待合成文本。不打断、不清队列 —— 这是实现连续播放的关键：
        /// LLM 逐句吐出的文本全部排队，生成线程无缝衔接。
        /// </summary>
        public void Enqueue(string text, float speed, int speakerId)
        {
            if (!initDone || string.IsNullOrWhiteSpace(text)) return;

            finishedNotified = false;
            textQueue.Enqueue(new PendingText
            {
                Text = text,
                Speed = speed,
                SpeakerId = speakerId,
                Gen = generation
            });
            textSignal.Release();
        }

        /// <summary>
        /// 兼容旧调用名。语义已改为「追加」而非「替换」。
        /// </summary>
        public void Generate(string text, float speed, int speakerId) => Enqueue(text, speed, speakerId);

        /// <summary>
        /// 打断：作废所有在途文本与 PCM，并让正在跑的推理尽快退出。
        /// </summary>
        public void Interrupt()
        {
            Interlocked.Increment(ref generation);

            while (textQueue.TryDequeue(out _)) { }
            while (sendQueue.TryDequeue(out _)) { }
            lock (frameBufferLock)
            {
                pcmFrameBuffer.Clear();
            }
            started = false;
            finishedNotified = true;
            Console.WriteLine("[TTS] 已打断");
        }

        /// <summary>
        /// 合成线程：串行消费文本队列，PCM 持续追加到 sendQueue，中间不清空。
        /// </summary>
        private void SynthLoop()
        {
            while (synthRunning)
            {
                textSignal.Wait();
                if (!synthRunning) break;

                if (!textQueue.TryDequeue(out PendingText item)) continue;

                // 排队期间被打断过 -> 整条作废
                if (item.Gen != generation) continue;

                int myGen = item.Gen;
                isGenerating = true;
                try
                {
                    OfflineTtsCallback callback = (samples, n) => OnAudioData(samples, n, myGen);
                    ot.GenerateWithCallback(item.Text, item.Speed, item.SpeakerId, callback);
                }
                catch (Exception e)
                {
                    Console.WriteLine("[TTS] 生成异常: " + e.Message);
                }
                finally
                {
                    isGenerating = false;
                }

                // 只有当后面确实没有待合成文本时才补齐尾帧；
                // 否则把残留留给下一句拼接，句间不插零 -> 无咔哒声。
                if (myGen == generation && textQueue.IsEmpty)
                {
                    FlushTailFrame(myGen);
                }
            }
        }

        /// <summary>
        /// TTS 回调：float 采样 → 16bit PCM → 切成 20ms 定长帧入队
        /// </summary>
        private int OnAudioData(nint samples, int n, int myGen)
        {
            if (myGen != generation)
            {
                return 0;      // 返回 0 通知 SherpaOnnx 停止本次推理
            }
            if (n <= 0) return 0;

            int originalN = n;
            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);

            if (resampler != null)
            {
                floatData = resampler.Process(floatData, n);
                n = floatData.Length;
                if (n == 0) return originalN;
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

            EnqueueFrames(pcmBytes, myGen);
            return originalN;
        }

        private void EnqueueFrames(byte[] pcmBytes, int myGen)
        {
            lock (frameBufferLock)
            {
                pcmFrameBuffer.AddRange(pcmBytes);

                while (pcmFrameBuffer.Count >= FRAME_BYTES)
                {
                    byte[] frame = new byte[FRAME_BYTES];
                    pcmFrameBuffer.CopyTo(0, frame, 0, FRAME_BYTES);
                    pcmFrameBuffer.RemoveRange(0, FRAME_BYTES);
                    EnqueueFrame(frame, myGen);
                }
            }
        }

        private void FlushTailFrame(int myGen)
        {
            lock (frameBufferLock)
            {
                if (pcmFrameBuffer.Count == 0) return;
                byte[] frame = new byte[FRAME_BYTES];
                int count = Math.Min(pcmFrameBuffer.Count, FRAME_BYTES);
                pcmFrameBuffer.CopyTo(0, frame, 0, count);
                pcmFrameBuffer.Clear();
                EnqueueFrame(frame, myGen);
            }
        }

        private void EnqueueFrame(byte[] frame, int myGen)
        {
            if (myGen != generation) return;

            // 【关键】队列满时不能丢帧！
            // Matcha 推理远快于实时（steps-3 大约 20~40x 实时），一段几十秒的长回复
            // 会在两三秒内全部生成完。旧实现在队列满时"丢最旧"，等于把已经合成好的
            // 语音成段扔掉，播出来就是明显的跳字、断句 —— 这正是"跳跃"的直接来源。
            //
            // 正确做法是反压生成线程：这里是合成线程上下文，阻塞完全安全，
            // 等发送线程按实时节拍消费出空位再继续。打断时 generation 变化会立刻跳出。
            while (sendQueue.Count >= MAX_QUEUE_FRAMES)
            {
                if (myGen != generation || !sendRunning) return;
                Thread.Sleep(5);
            }
            sendQueue.Enqueue(frame);
        }

        /// <summary>
        /// 发送线程：按音频时钟节拍推送，每包 FRAMES_PER_PACKET 帧。
        ///
        /// 用 Stopwatch 计算「应该已经发出多少帧」，而不是 Sleep 累加，
        /// 这样即使某次 Sleep 被 OS 拖长也会在下一轮自动补齐，不会产生累积漂移。
        /// 队列未达开播水位时不发送，也不推进时钟基准。
        /// </summary>
        private void SendLoop()
        {
            var sw = Stopwatch.StartNew();
            double framesSent = 0;                       // 已发出的帧数（作为时钟锚点）
            double framesPerMs = TARGET_SAMPLE_RATE / 1000.0 / FRAME_SAMPLES;  // 0.05 帧/ms
            byte[] packet = new byte[FRAME_BYTES * FRAMES_PER_PACKET];

            while (sendRunning)
            {
                // ---- 起播水位控制 ----
                if (!started)
                {
                    if (sendQueue.Count >= START_WATERMARK_FRAMES ||
                        (!isGenerating && textQueue.IsEmpty && sendQueue.Count > 0))
                    {
                        started = true;
                        sw.Restart();
                        framesSent = 0;
                    }
                    else
                    {
                        // 队列空且啥都不在跑 -> 一轮播放结束
                        if (sendQueue.IsEmpty && !isGenerating && textQueue.IsEmpty && !finishedNotified)
                        {
                            finishedNotified = true;
                            OnPlaybackFinished?.Invoke();
                        }
                        Thread.Sleep(5);
                        continue;
                    }
                }

                // ---- 该发多少帧 ----
                double due = sw.Elapsed.TotalMilliseconds * framesPerMs;
                if (due - framesSent < FRAMES_PER_PACKET)
                {
                    Thread.Sleep(2);
                    continue;
                }

                if (sendQueue.Count < FRAMES_PER_PACKET)
                {
                    // 欠载。若还有内容在路上就等一等（别退出 started，避免重复预缓冲）；
                    // 若确实全部结束，则把零散尾帧发完并收尾。
                    if (isGenerating || !textQueue.IsEmpty)
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    int tail = 0;
                    while (tail < FRAMES_PER_PACKET && sendQueue.TryDequeue(out byte[] tf))
                    {
                        Buffer.BlockCopy(tf, 0, packet, tail * FRAME_BYTES, FRAME_BYTES);
                        tail++;
                    }
                    if (tail > 0)
                    {
                        SendPacket(packet, tail * FRAME_BYTES);
                        framesSent += tail;
                    }

                    started = false;
                    if (!finishedNotified)
                    {
                        finishedNotified = true;
                        OnPlaybackFinished?.Invoke();
                    }
                    continue;
                }

                int filled = 0;
                while (filled < FRAMES_PER_PACKET && sendQueue.TryDequeue(out byte[] f))
                {
                    Buffer.BlockCopy(f, 0, packet, filled * FRAME_BYTES, FRAME_BYTES);
                    filled++;
                }
                SendPacket(packet, filled * FRAME_BYTES);
                framesSent += filled;

                // 落后太多（进程被挂起等）就重置时钟，避免疯狂追帧把 ESP32 缓冲冲爆
                if (due - framesSent > 25)
                {
                    sw.Restart();
                    framesSent = 0;
                }
            }
        }

        private void SendPacket(byte[] buffer, int length)
        {
            var conn = client;
            if (conn == null || !conn.IsAvailable) return;

            try
            {
                if (length == buffer.Length)
                {
                    conn.Send(buffer);
                }
                else
                {
                    byte[] exact = new byte[length];
                    Buffer.BlockCopy(buffer, 0, exact, 0, length);
                    conn.Send(exact);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[TTS] 发送异常: " + e.Message);
            }
        }

        public void Stop()
        {
            sendRunning = false;
            synthRunning = false;
            textSignal.Release();
            Interrupt();
            sendThread?.Join(500);
            synthThread?.Join(1000);
            ot?.Dispose();
            ot = null;
            textSignal.Dispose();
        }
    }
}
