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

        // 每包聚合 3 帧 = 60ms。20ms 小包在 TCP 上易被 Nagle 聚合成不规律突发，
        // 聚合后包数降到 1/3，抖动明显下降。
        private const int FramesPerPacket = 3;

        // 队列上限 8 秒。满了阻塞生产者（反压），绝不丢帧 —— 丢帧就是跳音。
        private const int MaxQueueFrames = 400;

        // 起播水位 400ms，用于吸收生产端抖动
        private const int StartWatermarkFrames = 20;

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
            return true;
        }

        private void SendLoop()
        {
            var sw = Stopwatch.StartNew();
            double framesDue = 0;                                  // 已计入时钟的帧数
            double framesPerMs = TargetSampleRate / 1000.0 / FrameSamples;   // 0.05 帧/ms
            byte[] packet = new byte[FrameBytes * FramesPerPacket];

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

                // ---- 到点了吗 ----
                double due = sw.Elapsed.TotalMilliseconds * framesPerMs;
                if (due - framesDue < FramesPerPacket)
                {
                    Thread.Sleep(2);
                    continue;
                }

                if (queue.Count < FramesPerPacket)
                {
                    // 欠载：还有数据在路上就等（不退出 started，避免重复预缓冲）
                    if (isProducing != null && isProducing())
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    // 确实结束了：把零散尾帧发完再收尾
                    int tail = 0;
                    while (tail < FramesPerPacket && queue.TryDequeue(out byte[] tf))
                    {
                        Buffer.BlockCopy(tf, 0, packet, tail * FrameBytes, FrameBytes);
                        tail++;
                    }
                    if (tail > 0)
                    {
                        SendPacket(packet, tail * FrameBytes);
                        Interlocked.Add(ref sentFrames, tail);
                        framesDue += tail;
                    }

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

                int filled = 0;
                while (filled < FramesPerPacket && queue.TryDequeue(out byte[] f))
                {
                    Buffer.BlockCopy(f, 0, packet, filled * FrameBytes, FrameBytes);
                    filled++;
                }
                SendPacket(packet, filled * FrameBytes);
                Interlocked.Add(ref sentFrames, filled);
                framesDue += filled;

                // 落后太多（进程被挂起等）重置时钟，避免疯狂追帧把 ESP32 缓冲冲爆
                if (due - framesDue > 25)
                {
                    sw.Restart();
                    framesDue = 0;
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
