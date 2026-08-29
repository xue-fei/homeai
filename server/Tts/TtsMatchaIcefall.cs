using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Server.Tts
{
    /// <summary>
    /// Matcha-Icefall TTS —— 纯 PCM 流水线版
    ///
    /// 下行协议：16000Hz / 16bit / 单声道 / 小端序裸 PCM，无编解码、无长度前缀。
    ///
    /// 【流水线】
    ///   Enqueue(text) -> textQueue -> [SynthLoop 串行推理] -> PcmStreamer -> ESP32
    /// 生成线程一句接一句连续推理，PCM 不断往同一个出口追加，
    /// 句子之间不清队列、不补零、不重置残留 —— 样本流严格连续，永不断流。
    ///
    /// 【出口共享】
    /// PCM 发送不再由本类自己开线程，而是统一走 PcmStreamer。
    /// 因为背景音乐也要往同一个 WebSocket 推 PCM，两个发送线程会把字节流交织成噪音。
    /// 本类通过 Acquire 抢占出口，语音天然优先于音乐。
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
        float volume = 1f;

        private const int TARGET_SAMPLE_RATE = PcmStreamer.TargetSampleRate;
        private const int FRAME_BYTES = PcmStreamer.FrameBytes;

        private readonly PcmStreamer streamer;

        /// <summary>语音开始前的回调，用于让背景音乐让位</summary>
        public Action OnSpeechStarting;

        // 待合成文本队列（生成线程串行消费）
        private readonly ConcurrentQueue<PendingText> textQueue = new();
        private readonly SemaphoreSlim textSignal = new(0);

        // 打断代号：每次 Interrupt 自增，所有在途工作据此判定是否作废
        private volatile int generation = 0;

        // 出口代号（从 PcmStreamer.Acquire 拿到）
        private volatile int outGen = -1;

        // 不足一帧的 PCM 残留
        private readonly List<byte> pcmFrameBuffer = new List<byte>();
        private readonly object frameBufferLock = new object();

        /// <summary>整段播放完毕回调</summary>
        public Action OnPlaybackFinished;

        private volatile bool synthRunning = true;
        private volatile bool isGenerating = false;
        private Thread synthThread;

        private PcmResampler resampler = null;

        private struct PendingText
        {
            public string Text;
            public float Speed;
            public int SpeakerId;
            public int Gen;
        }

        public TtsMatchaIcefall(PcmStreamer streamer)
        {
            this.streamer = streamer;

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
        }

        /// <summary>
        /// 追加一句待合成文本。不打断、不清队列 —— 这是实现连续播放的关键：
        /// LLM 逐句吐出的文本全部排队，生成线程无缝衔接。
        /// </summary>
        public void Enqueue(string text, float speed, int speakerId)
        {
            if (!initDone || string.IsNullOrWhiteSpace(text)) return;

            textQueue.Enqueue(new PendingText
            {
                Text = text,
                Speed = speed,
                SpeakerId = speakerId,
                Gen = generation
            });
            textSignal.Release();
        }

        /// <summary>兼容旧调用名。语义为「追加」而非「替换」。</summary>
        public void Generate(string text, float speed, int speakerId) => Enqueue(text, speed, speakerId);

        /// <summary>打断：作废所有在途文本与 PCM，让正在跑的推理尽快退出。</summary>
        public void Interrupt()
        {
            Interlocked.Increment(ref generation);

            while (textQueue.TryDequeue(out _)) { }
            lock (frameBufferLock)
            {
                pcmFrameBuffer.Clear();
            }

            int g = outGen;
            if (g >= 0)
            {
                streamer.Invalidate(g);
                outGen = -1;
            }
            Console.WriteLine("[TTS] 已打断");
        }

        /// <summary>合成线程：串行消费文本队列，PCM 持续追加到出口，中间不清空。</summary>
        private void SynthLoop()
        {
            while (synthRunning)
            {
                textSignal.Wait();
                if (!synthRunning) break;

                if (!textQueue.TryDequeue(out PendingText item)) continue;
                if (item.Gen != generation) continue;      // 排队期间被打断 -> 作废

                int myGen = item.Gen;

                // 第一句：抢占出口（语音优先于音乐）
                if (outGen < 0)
                {
                    try { OnSpeechStarting?.Invoke(); }
                    catch (Exception e) { Console.WriteLine("[TTS] 让位回调异常: " + e.Message); }

                    outGen = streamer.Acquire(
                        producing: () => isGenerating || !textQueue.IsEmpty,
                        drained: () =>
                        {
                            outGen = -1;
                            try { OnPlaybackFinished?.Invoke(); }
                            catch (Exception e) { Console.WriteLine("[TTS] 播放完毕回调异常: " + e.Message); }
                        });
                }

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

                // 只有后面确实没有待合成文本时才补齐尾帧；
                // 否则把残留留给下一句拼接，句间不插零 -> 无咔哒声。
                if (myGen == generation && textQueue.IsEmpty)
                {
                    FlushTailFrame(myGen);
                }
            }
        }

        /// <summary>TTS 回调：float 采样 → 16bit PCM → 切成 20ms 定长帧推入出口</summary>
        private int OnAudioData(nint samples, int n, int myGen)
        {
            if (myGen != generation) return 0;    // 返回 0 通知 SherpaOnnx 停止推理
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
                    PushFrame(frame, myGen);
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
                PushFrame(frame, myGen);
            }
        }

        /// <summary>
        /// 推一帧到出口。
        ///
        /// 【为什么绝不能丢帧】
        /// Matcha steps-3 推理约 20~40 倍实时，长回复几秒就全生成完。
        /// 若在队列满时丢帧，等于把已合成好的语音成段扔掉 -> 听感就是跳字断句。
        /// PcmStreamer.Push 在队列满时阻塞（反压合成线程），这是设计意图。
        /// </summary>
        private void PushFrame(byte[] frame, int myGen)
        {
            if (myGen != generation) return;
            int g = outGen;
            if (g < 0) return;
            streamer.Push(frame, g);
        }

        public void Stop()
        {
            synthRunning = false;
            textSignal.Release();
            Interrupt();
            synthThread?.Join(1000);
            ot?.Dispose();
            ot = null;
            textSignal.Dispose();
        }
    }
}
