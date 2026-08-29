namespace Server
{
    public enum MusicState
    {
        Stopped,
        Playing,
        Paused,
        /// <summary>被语音抢占，语音说完会自动接着放</summary>
        Ducked
    }

    /// <summary>
    /// 背景音乐播放器
    ///
    /// 播放 music 文件夹下的 16000Hz / 16bit / 单声道 WAV，
    /// PCM 走 PcmStreamer 这个唯一出口，与 TTS 语音互斥。
    ///
    /// 【与语音的关系】
    /// 语音优先。TTS 一 Acquire 出口，音乐的代号立刻过期，Push 失败，
    /// 播放线程转入 Ducked 状态并把「已入队但没播出去」的字节退回文件读取位置 ——
    /// 所以语音结束后音乐是从被打断的地方接着放，不会跳过一段。
    /// 语音播完 PcmStreamer 触发 OnIdle，音乐自动恢复。
    ///
    /// 【为什么 Ducked 不是暂停】
    /// 暂停是用户意图，Ducked 是系统行为。用户按了暂停，语音说完也不该自己响起来；
    /// 被语音压下去的，说完就该接回来。两者必须区分，否则行为很怪。
    /// </summary>
    public class MusicPlayer
    {
        private readonly PcmStreamer streamer;
        private readonly string musicDir;

        private readonly object stateLock = new object();
        private List<string> playlist = new List<string>();
        private int index = -1;
        private WavStreamReader reader = null;
        private volatile MusicState state = MusicState.Stopped;
        private volatile bool loopAll = true;
        private float volume = 0.6f;                 // 背景音乐默认压低一点

        private volatile int myGen = -1;
        private volatile bool pumping = false;       // 供 PcmStreamer 判断"还有数据在路上"
        private readonly Thread pumpThread;
        private volatile bool running = true;
        private readonly SemaphoreSlim wake = new(0);

        // 音乐自己只预读 1 秒，远小于 PcmStreamer 的 8 秒上限。
        //
        // 【为什么要额外限制，而不是靠 Push 的反压】
        // 音乐是"取之不尽"的流，放开推的话 pump 会瞬间把队列灌满 8 秒。后果有三：
        //   1. State / CurrentName 会超前真实音频 8 秒，切歌日志和状态回执全是假的
        //   2. 被语音抢占时白丢 8 秒音乐，Rewind 甚至可能已跨到下一首，退不回去
        //   3. 短音频文件在读取阶段就"播完"并连续切歌，行为彻底失控
        // TTS 不需要这个限制 —— 语音是有限长度的，攒满队列反而是好事。
        private const int MusicBufferFrames = 50;    // 50 * 20ms = 1s

        public MusicState State => state;
        public string CurrentName
        {
            get
            {
                lock (stateLock)
                {
                    return (index >= 0 && index < playlist.Count)
                        ? Path.GetFileNameWithoutExtension(playlist[index])
                        : string.Empty;
                }
            }
        }

        public MusicPlayer(PcmStreamer streamer)
        {
            this.streamer = streamer;
            musicDir = Path.Combine(Environment.CurrentDirectory, "music");

            if (!Directory.Exists(musicDir))
            {
                Directory.CreateDirectory(musicDir);
                Console.WriteLine($"[音乐] 已创建目录 {musicDir}，把 16kHz/单声道/16bit 的 wav 放进去即可");
            }

            RefreshPlaylist();

            // 语音播完出口空闲 -> 如果音乐是被压下去的，接着放
            streamer.OnIdle += OnStreamerIdle;

            pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "MusicPump" };
            pumpThread.Start();
        }

        /// <summary>扫描 music 目录。运行中新增文件后可再调一次。</summary>
        public int RefreshPlaylist()
        {
            lock (stateLock)
            {
                string current = (index >= 0 && index < playlist.Count) ? playlist[index] : null;

                playlist = Directory.Exists(musicDir)
                    ? Directory.GetFiles(musicDir, "*.wav", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();

                // 保持当前曲目不变（文件仍在的话）
                index = current != null ? playlist.IndexOf(current) : -1;

                Console.WriteLine($"[音乐] 曲目 {playlist.Count} 首");
                return playlist.Count;
            }
        }

        public List<string> GetTrackNames()
        {
            lock (stateLock)
            {
                return playlist.Select(Path.GetFileNameWithoutExtension).ToList()!;
            }
        }

        /// <summary>
        /// 播放。name 为空则播当前/第一首；否则按文件名模糊匹配（忽略大小写、支持部分匹配）。
        /// </summary>
        public bool Play(string name = null)
        {
            lock (stateLock)
            {
                if (playlist.Count == 0)
                {
                    RefreshPlaylist();
                    if (playlist.Count == 0)
                    {
                        Console.WriteLine("[音乐] music 目录下没有 wav 文件");
                        return false;
                    }
                }

                int target = index;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    target = FindTrack(name);
                    if (target < 0)
                    {
                        Console.WriteLine($"[音乐] 找不到匹配「{name}」的曲目");
                        return false;
                    }
                }
                else if (state == MusicState.Paused && reader != null)
                {
                    // 暂停后原地续播，不重新打开文件
                    state = MusicState.Playing;
                    myGen = streamer.Acquire(() => pumping, null);
                    wake.Release();
                    Console.WriteLine($"[音乐] 续播 {CurrentName}");
                    return true;
                }

                if (target < 0) target = 0;
                return OpenAndStart(target);
            }
        }

        public bool PlayIndex(int i)
        {
            lock (stateLock)
            {
                if (i < 0 || i >= playlist.Count) return false;
                return OpenAndStart(i);
            }
        }

        public bool Next()
        {
            lock (stateLock)
            {
                if (playlist.Count == 0) return false;
                return OpenAndStart((index + 1) % playlist.Count);
            }
        }

        public bool Previous()
        {
            lock (stateLock)
            {
                if (playlist.Count == 0) return false;
                int i = index - 1;
                if (i < 0) i = playlist.Count - 1;
                return OpenAndStart(i);
            }
        }

        /// <summary>用户暂停。语音结束后不会自动恢复，要显式 Play()。</summary>
        public void Pause()
        {
            lock (stateLock)
            {
                if (state != MusicState.Playing && state != MusicState.Ducked) return;
                state = MusicState.Paused;
                ReleaseOutput();
                Console.WriteLine("[音乐] 已暂停");
            }
        }

        public void Stop()
        {
            lock (stateLock)
            {
                if (state == MusicState.Stopped) return;
                state = MusicState.Stopped;
                ReleaseOutput();
                reader?.Dispose();
                reader = null;
                Console.WriteLine("[音乐] 已停止");
            }
        }

        public void SetVolume(float v)
        {
            volume = Math.Clamp(v, 0f, 2f);
            Console.WriteLine($"[音乐] 音量 {volume:0.00}");
        }

        public float GetVolume() => volume;

        public void SetLoopAll(bool on)
        {
            loopAll = on;
            Console.WriteLine($"[音乐] 列表循环 {(on ? "开" : "关")}");
        }

        /// <summary>
        /// 语音即将开始：把音乐降级为 Ducked。
        /// 注意此处不 Invalidate 出口 —— TTS 自己会 Acquire，那才是权威的抢占动作。
        /// 这里只负责标记状态，让语音结束后知道要恢复。
        /// </summary>
        public void DuckForSpeech()
        {
            lock (stateLock)
            {
                if (state == MusicState.Playing)
                {
                    state = MusicState.Ducked;
                    Console.WriteLine("[音乐] 让位给语音");
                }
            }
        }

        private void OnStreamerIdle()
        {
            // 回调来自 PcmStreamer 发送线程，只唤醒供给线程，重活交给它做，
            // 避免在发送线程里持锁 -> 节拍抖动。
            if (state == MusicState.Ducked) wake.Release();
        }

        // ---- 以下均需在 stateLock 内调用 ----

        private int FindTrack(string name)
        {
            string key = name.Trim();

            for (int i = 0; i < playlist.Count; i++)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(playlist[i]), key,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            for (int i = 0; i < playlist.Count; i++)
            {
                if (Path.GetFileNameWithoutExtension(playlist[i])!
                        .Contains(key, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private bool OpenAndStart(int i)
        {
            ReleaseOutput();
            reader?.Dispose();
            reader = null;

            try
            {
                reader = new WavStreamReader(playlist[i]);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[音乐] 打不开 {Path.GetFileName(playlist[i])}: {e.Message}");
                state = MusicState.Stopped;
                return false;
            }

            index = i;
            state = MusicState.Playing;
            pumping = true;
            myGen = streamer.Acquire(() => pumping, null);
            wake.Release();

            Console.WriteLine($"[音乐] ▶ {CurrentName}  {reader.DurationSeconds:0.0}s");
            return true;
        }

        private void ReleaseOutput()
        {
            int gen = myGen;
            if (gen < 0) return;

            // 把已入队但还没播出去的字节退回读取位置，续播才不会跳段
            RewindUnplayed(gen);
            streamer.Invalidate(gen);
            myGen = -1;
            pumping = false;
        }

        private void RewindUnplayed(int gen)
        {
            if (reader == null || gen != streamer.Current) return;
            int queued = streamer.QueuedFrames;
            if (queued > 0)
            {
                reader.Rewind((long)queued * PcmStreamer.FrameBytes);
            }
        }

        /// <summary>
        /// 供给线程：把 WAV 字节切成 20ms 帧塞进出口。
        /// 只保持 MusicBufferFrames 的预读深度，多了就等 —— 见该常量处的说明。
        /// </summary>
        private void PumpLoop()
        {
            byte[] buf = new byte[PcmStreamer.FrameBytes];

            while (running)
            {
                if (state == MusicState.Ducked)
                {
                    // 看门狗：OnIdle 只在「正常播完」时触发；如果语音是被 Interrupt
                    // 强行掐掉的（用户按键打断），出口直接失效，不会有 OnIdle。
                    // 所以这里主动轮询出口状态，避免音乐永远卡在 Ducked。
                    wake.Wait(200);
                    TryResumeFromDuck();
                    continue;
                }

                if (state != MusicState.Playing)
                {
                    wake.Wait(200);
                    continue;
                }

                // 预读到位就歇着，让出口按实时节拍慢慢消费
                if (streamer.QueuedFrames >= MusicBufferFrames)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int gen = myGen;
                WavStreamReader r;
                lock (stateLock)
                {
                    r = reader;
                }
                if (r == null || gen < 0)
                {
                    wake.Wait(50);
                    continue;
                }

                int got;
                try
                {
                    got = r.Read(buf, PcmStreamer.FrameBytes);
                }
                catch (Exception e)
                {
                    Console.WriteLine("[音乐] 读取异常: " + e.Message);
                    lock (stateLock) { state = MusicState.Stopped; }
                    continue;
                }

                if (got <= 0)
                {
                    OnTrackFinished();
                    continue;
                }

                // 末尾不足一帧：补零凑满，避免 ESP32 侧拼帧残留半帧数据
                if (got < PcmStreamer.FrameBytes)
                {
                    Array.Clear(buf, got, PcmStreamer.FrameBytes - got);
                }

                byte[] frame = ApplyVolume(buf);
                pumping = true;

                if (!streamer.Push(frame, gen))
                {
                    // 代号失效 = 被语音抢占（或被 Stop/Pause）
                    lock (stateLock)
                    {
                        if (state == MusicState.Playing && myGen == gen)
                        {
                            // 队列已被抢占者清空，本帧也没发出去 -> 退回一帧
                            reader?.Rewind(PcmStreamer.FrameBytes);
                            state = MusicState.Ducked;
                            myGen = -1;
                            pumping = false;
                            Console.WriteLine("[音乐] 被语音抢占，等待恢复");
                        }
                    }
                }
            }
        }

        /// <summary>Ducked 状态下轮询：出口空了就接着放</summary>
        private void TryResumeFromDuck()
        {
            lock (stateLock)
            {
                if (state != MusicState.Ducked || reader == null) return;
                if (!streamer.IsIdle) return;

                state = MusicState.Playing;
                pumping = true;
                myGen = streamer.Acquire(() => pumping, null);
                Console.WriteLine($"[音乐] 恢复 {CurrentName}");
            }
        }

        private void OnTrackFinished()
        {
            lock (stateLock)
            {
                pumping = false;
                Console.WriteLine($"[音乐] 播完 {CurrentName}");
                reader?.Dispose();
                reader = null;

                if (!loopAll || playlist.Count == 0)
                {
                    state = MusicState.Stopped;
                    myGen = -1;
                    return;
                }

                int next = (index + 1) % playlist.Count;
                // 单曲列表时 next == index，同样重新打开即可（循环播放）
                if (!OpenAndStartInternalNoRelease(next))
                {
                    state = MusicState.Stopped;
                }
            }
        }

        /// <summary>
        /// 自然播完后切下一首：不 Acquire 新代号，沿用当前出口继续推，
        /// 这样两首歌之间不会重新走一遍起播水位（否则每首歌开头都要静音等 400ms）。
        /// </summary>
        private bool OpenAndStartInternalNoRelease(int i)
        {
            try
            {
                reader = new WavStreamReader(playlist[i]);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[音乐] 打不开 {Path.GetFileName(playlist[i])}: {e.Message}");
                return false;
            }

            index = i;
            state = MusicState.Playing;
            pumping = true;
            if (myGen < 0)
            {
                myGen = streamer.Acquire(() => pumping, null);
            }
            wake.Release();
            Console.WriteLine($"[音乐] ▶ {CurrentName}  {reader.DurationSeconds:0.0}s");
            return true;
        }

        private byte[] ApplyVolume(byte[] src)
        {
            var dst = new byte[src.Length];
            float v = volume;

            if (Math.Abs(v - 1f) < 0.001f)
            {
                Buffer.BlockCopy(src, 0, dst, 0, src.Length);
                return dst;
            }

            for (int i = 0; i < src.Length; i += 2)
            {
                short s = (short)(src[i] | (src[i + 1] << 8));
                int scaled = (int)(s * v);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                dst[i] = (byte)(scaled & 0xFF);
                dst[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
            return dst;
        }

        public void Shutdown()
        {
            running = false;
            wake.Release();
            streamer.OnIdle -= OnStreamerIdle;
            pumpThread?.Join(500);
            lock (stateLock)
            {
                reader?.Dispose();
                reader = null;
            }
            wake.Dispose();
        }
    }
}
