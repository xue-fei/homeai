using Fleck;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Server
{
    /// <summary>
    /// 下行 PCM 统一发送器（全链路唯一的音频出口）
    ///
    /// 【为什么必须统一】
    /// TTS 语音和背景音乐都要往同一个 WebSocket 推 PCM。如果各自开一个发送线程，
    /// 两路数据会在字节层面交织，ESP32 收到的就是两段音频被随机切碎后拼在一起 ——
    /// 听起来是彻底的噪音。所以出口只能有一个，由本类独占。
    ///
    /// 【抢占模型】
    /// 用 generation 代号做独占令牌：
    ///   Acquire() -> 代号自增 + 清空队列，返回新代号，调用方成为唯一生产者
    ///   Push(frame, gen) -> 代号过期直接失败，生产者据此知道自己被抢占了
    /// 谁 Acquire 谁说话，语音抢占音乐，音乐靠 OnIdle 事件自己恢复。
    ///
    /// 【节拍】
    /// 用 Stopwatch 算「此刻应该已经发出多少帧」，而不是 Sleep 累加。
    /// 某次 Sleep 被 OS 拖长会在下一轮自动补齐，不产生累积漂移。
    /// </summary>
    public class PcmStreamer
    {
        public const int TargetSampleRate = 16000;
        public const int FrameSamples = 320;                      // 20ms
        public const int FrameBytes = FrameSamples * 2;            // 640

        // 每包聚合 6 帧 = 120ms。弱信号 WiFi 下，包越大越稀疏，CSMA/CA 退避与
        // TCP 重传的开销摊薄，抖动和发热都更低。120ms 是"抗抖动"与"端到端延迟"
        // 的折中：再大首字延迟会变明显，再小则包过密。
        private const int FramesPerPacket = 6;

        // 队列上限 8 秒。满了阻塞生产者（反压），绝不丢帧 —— 丢帧就是跳音。
        private const int MaxQueueFrames = 400;

        // 发送超前上限：服务端最多比"实时播放时钟"领先多少帧就暂停发送。
        // 这是关键限速器：TTS 推理是 20~40 倍实时，一推理完就有一大坨帧在队列里，
        // 如果不按实时节拍限速，会把这一大坨一口气全灌给 ESP32，其 64 帧(1.28s)
        // 抖动缓冲瞬间灌满 -> 之后每来一帧丢一帧（dropFrames 暴涨）= 丢掉的都是
        // 已合成好的语音 -> 听感跳字漏词。
        //
        // 定成 32 帧(640ms)：既给 ESP32 冷启动预缓冲(10帧) + DMA(12帧) 留足余量、
        // 保证不欠载，又离 ESP32 缓冲上限 64 帧有安全距离，稳态下绝不触发丢帧。
        // 注意稳态下这个上限其实用不到 —— 发送速率被实时时钟钳制在 50 帧/秒，
        // 超前量只会小幅波动，不会真积累到 32 帧。它只兜住"线程被卡住后突然恢复
        // 疯狂追帧"这种异常场景。
        private const int SendAheadLimitFrames = 32;

        // 起播水位：攒够多少帧才开声。
        // 关键认知：防爆音的预缓冲职责在 ESP32 侧（它冷启动攒 200ms 才开声），
        // 服务端不需要再攒帧。服务端攒帧只会让首字延迟变大，且在"短句 + 突发供给"
        // 场景下（TTS 一次只吐几帧然后又空几秒）永远凑不满水位 -> 一个字节都不发
        // -> ESP32 缓冲被抽干 -> 欠载暴涨。所以这里定成 1：有数据立刻发。
        private const int StartWatermarkFrames = 1;

        // 耐心机制：生产者停止供数据后，等待多久才真正收尾。
        // LLM 是逐句生成的，句与句之间有几秒钟的思考时间。如果 PcmStreamer 一看到
        // isProducing=false 就急着收尾，ESP32 每次都要重新预缓冲，听感就是一顿一顿的。
        // 2 秒耐心值 > LLM 典型的句间停顿（< 1s），但 < ESP32 缓冲耗尽时间（1.28s +
        // 起播水位），所以不会无限等下去。
        private const int PatienceMs = 2000;

        private readonly ConcurrentQueue<byte[]> queue = new();
        private volatile IWebSocketConnection client = null;

        private volatile int generation = 0;
        private volatile bool started = false;
        private volatile bool running = true;
        private readonly Thread thread;
        private readonly object acquireLock = new object();

        // 当前 owner 提供的钩子
        private volatile Func<bool> isProducing = null;
        private volatile Action onDrained = null;
        private volatile bool drainNotified = true;

        /// <summary>本代号内已真正发出的帧数（Acquire 时归零）。用于计算未播出的部分。</summary>
        private long sentFrames = 0;
        public long SentFrames => Interlocked.Read(ref sentFrames);

        /// <summary>当前代号。生产者用它判断自己是否还持有出口。</summary>
        public int Current => generation;

        /// <summary>队列彻底空闲（无 owner 在生产）时触发一次。音乐用它做自动恢复。</summary>
        public event Action OnIdle;

        // 时间戳：最后一帧入队时间（用于耐心机制）、上次发送时间（用于最小间隔）
        private DateTime lastFrameTime = DateTime.MinValue;
        private DateTime lastSendTime = DateTime.MinValue;

        public PcmStreamer()
        {
            thread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "PcmSend",
                Priority = ThreadPriority.AboveNormal      // 节拍线程优先，减少调度抖动
            };
            thread.Start();
        }

        public void UpdateClient(IWebSocketConnection connection)
        {
            client = connection;
            if (connection == null)
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 抢占出口。清空前一个生产者留下的未发数据，返回新代号。
        /// </summary>
        /// <param name="producing">回调：告诉发送线程"我还有数据在路上"，用于区分欠载与结束</param>
        /// <param name="drained">回调：队列耗尽且生产结束时触发一次</param>
        public int Acquire(Func<bool> producing, Action drained)
        {
            lock (acquireLock)
            {
                int gen = Interlocked.Increment(ref generation);
                while (queue.TryDequeue(out _)) { }
                isProducing = producing;
                onDrained = drained;
                drainNotified = false;
                started = false;
                Interlocked.Exchange(ref sentFrames, 0);
                return gen;
            }
        }

        /// <summary>
        /// 作废当前内容。传入代号时只在自己仍持有出口的情况下生效（避免误伤后来者）。
        /// </summary>
        public void Invalidate(int gen = -1)
        {
            lock (acquireLock)
            {
                if (gen >= 0 && gen != generation) return;

                Interlocked.Increment(ref generation);
                while (queue.TryDequeue(out _)) { }
                isProducing = null;
                onDrained = null;
                drainNotified = true;
                started = false;
                Interlocked.Exchange(ref sentFrames, 0);
            }
        }

        /// <summary>队列中已入队但尚未发出的帧数。被抢占的生产者用它回退读取位置。</summary>
        public int QueuedFrames => queue.Count;

        /// <summary>出口空闲：队列已空且没有生产者在供数据。音乐的看门狗用它判断能否恢复。</summary>
        public bool IsIdle
        {
            get
            {
                var p = isProducing;
                return queue.IsEmpty && (p == null || !p());
            }
        }

        /// <summary>
        /// 推入一帧。队列满时阻塞等待（反压），代号过期返回 false。
        /// 必须从生产者自己的线程调用 —— 阻塞在这里是设计意图，不是 bug。
        /// </summary>
        public bool Push(byte[] frame, int gen)
        {
            if (gen != generation) return false;

            while (queue.Count >= MaxQueueFrames)
            {
                if (gen != generation || !running) return false;
                Thread.Sleep(5);
            }

            if (gen != generation) return false;
            queue.Enqueue(frame);
            lastFrameTime = DateTime.UtcNow;   // 记录入队时间，用于耐心机制
            return true;
        }

        private void SendLoop()
        {
            var sw = Stopwatch.StartNew();
            double framesDue = 0;
            double framesPerMs = TargetSampleRate / 1000.0 / FrameSamples;   // 0.05 帧/ms
            byte[] packet = new byte[FrameBytes * FramesPerPacket];
            var diagSw = Stopwatch.StartNew();

            while (running)
            {
                // ---- 起播水位 ----
                if (!started)
                {
                    bool producerDone = isProducing == null || !isProducing();

                    if (queue.Count >= StartWatermarkFrames ||
                        (producerDone && queue.Count > 0))
                    {
                        started = true;
                        sw.Restart();
                        framesDue = 0;
                        lastFrameTime = DateTime.UtcNow;
                        lastSendTime = DateTime.UtcNow;
                    }
                    else
                    {
                        if (queue.IsEmpty && producerDone && !drainNotified)
                        {
                            drainNotified = true;
                            var cb = onDrained;
                            onDrained = null;
                            try { cb?.Invoke(); } catch (Exception e) { Console.WriteLine("[PCM] drained 回调异常: " + e.Message); }
                            try { OnIdle?.Invoke(); } catch (Exception e) { Console.WriteLine("[PCM] idle 回调异常: " + e.Message); }
                        }
                        Thread.Sleep(5);
                        continue;
                    }
                }

                if (queue.Count == 0)
                {
                    // 队列空了：检查耐心值
                    bool producerDone = isProducing == null || !isProducing();
                    int emptyMs = (int)(DateTime.UtcNow - lastFrameTime).TotalMilliseconds;

                    // 生产者说完了，或者耐心耗尽了 -> 收尾
                    if (producerDone || emptyMs > PatienceMs)
                    {
                        started = false;
                        if (!drainNotified)
                        {
                            drainNotified = true;
                            var cb = onDrained;
                            onDrained = null;
                            try { cb?.Invoke(); } catch (Exception e) { Console.WriteLine("[PCM] drained 回调异常: " + e.Message); }
                            try { OnIdle?.Invoke(); } catch (Exception e) { Console.WriteLine("[PCM] idle 回调异常: " + e.Message); }
                        }
                        continue;
                    }

                    // 还有耐心，继续等
                    Thread.Sleep(2);
                    continue;
                }

                // ---- 按实时节拍发送，绝不超前冲爆 ESP32 缓冲 ----
                // framesDue 累加的是「已经发出的帧数」，sw.Elapsed 对应「实时已流逝
                // 时间应发的帧数」。发送超前量 = framesDue - due，是正数表示
                // 已经比实时多发了 N 帧。只要超前量超过上限就暂停，等实时时钟追上。
                // 这样发送速率严格贴合 50 帧/秒，有数据立刻发（不欠载），
                // 又不超前太多（不冲爆 ESP32 缓冲丢帧）。
                double due = sw.Elapsed.TotalMilliseconds * framesPerMs;
                if (framesDue - due > SendAheadLimitFrames)
                {
                    // 已比实时领先 32 帧(640ms)，暂停等 ESP32 消费追上
                    Thread.Sleep(2);
                    continue;
                }

                int filled = 0;
                int maxFrames = Math.Min(FramesPerPacket, queue.Count);
                while (filled < maxFrames && queue.TryDequeue(out byte[] f))
                {
                    Buffer.BlockCopy(f, 0, packet, filled * FrameBytes, FrameBytes);
                    filled++;
                }
                if (filled == 0) { Thread.Sleep(2); continue; }

                SendPacket(packet, filled * FrameBytes);
                Interlocked.Add(ref sentFrames, filled);
                framesDue += filled;

                // 周期诊断：队列水位 + 生产状态，用于定位"供给不足"还是"发送卡住"
                if (diagSw.ElapsedMilliseconds >= 5000)
                {
                    diagSw.Restart();
                    bool prod = isProducing != null && isProducing();
                    Console.WriteLine($"[PCM] 队列={queue.Count} 已发={SentFrames} 生产中={prod} started={started}");
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
                Console.WriteLine("[PCM] 发送异常: " + e.Message);
            }
        }

        public void Stop()
        {
            running = false;
            thread?.Join(500);
        }
    }
}
