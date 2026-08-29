using Fleck;
using Timer = System.Timers.Timer;
using Newtonsoft.Json;
using System.Timers;
using Server.Tts;

namespace Server
{
    /// <summary>
    /// WebSocket 服务端
    ///
    /// 音频协议（全链路统一，无任何编解码）：
    ///   上行 : Binary = 16000Hz/16bit/mono 小端裸 PCM
    ///   下行 : Binary = 同上，由 PcmStreamer 按 60ms/包 节拍发送
    ///   控制 : Text   = {"code":n,"msg":"..."}
    ///          -1 连接  0 心跳  1 开始说话  2 结束说话  3 音乐控制  99 错误回执
    ///
    /// 下行音频只有一个出口 PcmStreamer，TTS 语音与背景音乐互斥共享：
    /// 语音抢占音乐，语音结束后音乐从被打断处自动接回。
    /// </summary>
    public class Server
    {
        WebSocketServer webSocketServer = null;
        Asr asr = null;
        TtsMatchaIcefall tts = null;
        Llm llm = null;
        PcmStreamer streamer = null;
        MusicPlayer music = null;
        IWebSocketConnection client;
        readonly object clientLock = new object();

        const double checkRate = 1000;
        const long offlineTime = 10;          // 心跳超时秒数（原 3 秒过于激进，弱网易误断）
        long lastTickTime = 0;
        Timer timer;

        public Server()
        {
            // 下行音频唯一出口，必须先建
            streamer = new PcmStreamer();

            asr = new Asr();
            llm = new Llm();

            tts = new TtsMatchaIcefall(streamer);
            music = new MusicPlayer(streamer);

            llm.tts = tts;
            asr.llm = llm;

            // 语音开口前让音乐让位；语音说完 PcmStreamer 触发 OnIdle，音乐自己接回
            tts.OnSpeechStarting = () => music.DuckForSpeech();

            // ASR 结果先过音乐指令解析，命中就不打扰 LLM
            asr.CommandInterceptor = HandleMusicCommand;

            Console.WriteLine("tts llm asr music ok");

            // 心跳检测定时器全局只建一个，避免每次上线泄漏一个 Timer
            timer = new Timer(checkRate);
            timer.Elapsed += CheckTickTime;
            timer.AutoReset = true;

            webSocketServer = new WebSocketServer("ws://192.168.2.177:9999");
            webSocketServer.Start(OnStart);
        }

        private void OnStart(IWebSocketConnection connection)
        {
            connection.OnOpen += () => OnOpen(connection);
            connection.OnBinary = bytes => OnBinary(connection, bytes);
            connection.OnMessage = msg => OnMessage(connection, msg);
            connection.OnClose += () => OnClose(connection);
            connection.OnError = ex => Console.WriteLine("[WS] 连接异常: " + ex.Message);
        }

        private void OnOpen(IWebSocketConnection connection)
        {
            lock (clientLock)
            {
                // 单客户端模型：新连接进来时把旧连接踢掉，避免两个连接抢同一套 ASR/TTS
                if (client != null && client != connection && client.IsAvailable)
                {
                    Console.WriteLine("[" + client.ConnectionInfo.ClientIpAddress + " 被新连接顶替]");
                    try { client.Close(); } catch { }
                }

                client = connection;
                streamer.UpdateClient(client);
                asr.UpdateClient(client);
                asr.ResetBuffer();

                Console.WriteLine("[" + client.ConnectionInfo.ClientIpAddress + "上线了]");
                lastTickTime = GetTimeStamp();
                timer.Enabled = true;
            }
        }

        void CheckTickTime(object sender, ElapsedEventArgs e)
        {
            IWebSocketConnection toClose = null;

            lock (clientLock)
            {
                if (client == null)
                {
                    timer.Enabled = false;      // 没人在线就停掉轮询，不销毁 Timer
                    return;
                }

                if (GetTimeStamp() - lastTickTime <= offlineTime)
                {
                    return;
                }

                Console.WriteLine("[" + client.ConnectionInfo.ClientIpAddress + "心跳超时]");
                toClose = client;
                client = null;
                music.Stop();
                streamer.UpdateClient(null);
                asr.UpdateClient(null);
                timer.Enabled = false;
            }

            // Close 放在锁外，避免回调重入死锁
            try { toClose?.Close(); } catch { }
        }

        private void OnBinary(IWebSocketConnection connection, byte[] bytes)
        {
            // 只接受当前活跃连接的音频，防止旧连接残留数据污染
            lock (clientLock)
            {
                if (client != connection)
                {
                    return;
                }
                lastTickTime = GetTimeStamp();   // 音频流本身也算活跃信号
            }
            asr?.Receive(bytes);
        }

        private void OnMessage(IWebSocketConnection connection, string msg)
        {
            BaseMsg baseMsg = null;
            try
            {
                baseMsg = JsonConvert.DeserializeObject<BaseMsg>(msg);
            }
            catch (Exception e)
            {
                Console.WriteLine("[WS] 消息解析失败: " + e.Message);
                return;
            }

            if (baseMsg == null)
            {
                return;
            }

            lock (clientLock)
            {
                if (client != connection)
                {
                    return;
                }
                lastTickTime = GetTimeStamp();
            }

            switch (baseMsg.code)
            {
                case -1:
                    Console.WriteLine(baseMsg.msg);
                    break;

                case 0:
                    // 心跳，时间戳已在上面刷新
                    break;

                case 1:
                    // 开始说话：打断上一轮全链路，并清掉 ASR 里的陈旧数据。
                    // 音乐不停，只是让位 —— 用户说完话音乐会自己接回来。
                    llm?.Interrupt();
                    tts?.Interrupt();
                    music?.DuckForSpeech();
                    asr?.ResetBuffer();
                    break;

                case 2:
                    // 结束说话：触发识别
                    tts?.Interrupt();
                    asr?.EndReceive();
                    break;

                case 3:
                    // 音乐控制：msg 为指令文本，与语音指令走同一套解析
                    HandleMusicCommand(baseMsg.msg);
                    break;

                default:
                    Console.WriteLine($"[WS] 未知 code={baseMsg.code}");
                    break;
            }
        }

        /// <summary>
        /// 处理音乐指令。返回 true 表示已消费（调用方不再送 LLM）。
        /// 同时服务 ASR 语音指令和客户端 code=3 的显式控制。
        /// </summary>
        private bool HandleMusicCommand(string text)
        {
            if (music == null || string.IsNullOrWhiteSpace(text)) return false;

            var intent = MusicCommandParser.Parse(text);
            if (intent.Command == MusicCommand.None) return false;

            switch (intent.Command)
            {
                case MusicCommand.Play:
                    // 音乐要开口，先把可能还在说话的 TTS 停掉，否则两者抢出口
                    llm?.Interrupt();
                    if (!music.Play(intent.TrackName))
                    {
                        Speak(string.IsNullOrEmpty(intent.TrackName)
                            ? "音乐文件夹里还没有歌曲。"
                            : $"没有找到{intent.TrackName}。");
                    }
                    break;

                case MusicCommand.Resume:
                    llm?.Interrupt();
                    if (!music.Play()) Speak("没有可以播放的音乐。");
                    break;

                case MusicCommand.Pause:
                    music.Pause();
                    break;

                case MusicCommand.Stop:
                    music.Stop();
                    break;

                case MusicCommand.Next:
                    llm?.Interrupt();
                    if (!music.Next()) Speak("没有下一首了。");
                    break;

                case MusicCommand.Previous:
                    llm?.Interrupt();
                    if (!music.Previous()) Speak("没有上一首了。");
                    break;

                case MusicCommand.VolumeUp:
                    music.SetVolume(music.GetVolume() + 0.2f);
                    break;

                case MusicCommand.VolumeDown:
                    music.SetVolume(music.GetVolume() - 0.2f);
                    break;

                case MusicCommand.List:
                    var names = music.GetTrackNames();
                    if (names.Count == 0)
                    {
                        Speak("音乐文件夹里还没有歌曲。");
                    }
                    else
                    {
                        // 只念前 5 首，念一长串没人听得下去
                        var head = names.Take(5).ToList();
                        string listText = string.Join("、", head);
                        Speak(names.Count > head.Count
                            ? $"一共{names.Count}首，比如{listText}。"
                            : $"有这些歌：{listText}。");
                    }
                    break;
            }

            NotifyMusicState(intent.Command);
            return true;
        }

        /// <summary>让设备用 TTS 说一句话（音乐会自动让位）</summary>
        private void Speak(string text)
        {
            tts?.Enqueue(text, 1f, 0);
        }

        private void NotifyMusicState(MusicCommand cmd)
        {
            var conn = client;
            if (conn == null || !conn.IsAvailable) return;

            string info = $"{music.State}|{music.CurrentName}|{music.GetVolume():0.00}";
            try
            {
                conn.Send(JsonConvert.SerializeObject(new BaseMsg(3, info)));
            }
            catch (Exception e)
            {
                Console.WriteLine("[音乐] 状态回执发送失败: " + e.Message);
            }
        }

        private void OnClose(IWebSocketConnection connection)
        {
            lock (clientLock)
            {
                if (client != connection)
                {
                    return;      // 已被新连接顶替，不影响当前活跃会话
                }
                Console.WriteLine("[" + connection.ConnectionInfo.ClientIpAddress + "下线了]");
                client = null;
                music.Stop();
                streamer.UpdateClient(null);
                asr.UpdateClient(null);
                asr.ResetBuffer();
                timer.Enabled = false;
            }
        }

        private long GetTimeStamp()
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds);
        }

        public void Shutdown()
        {
            if (timer != null)
            {
                timer.Enabled = false;
                timer.Elapsed -= CheckTickTime;
                timer.Dispose();
                timer = null;
            }
            asr?.Stop();
            tts?.Stop();
            music?.Shutdown();
            streamer?.Stop();
            webSocketServer?.Dispose();
            webSocketServer = null;
        }

        // ===== 供控制台调试使用（Program.cs 里的手输指令）=====
        public MusicPlayer Music => music;
    }
}
