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
    ///   下行 : Binary = 同上，固定 640 字节（20ms）一帧
    ///   控制 : Text   = {"code":n,"msg":"..."}
    ///          -1 连接  0 心跳  1 开始说话  2 结束说话  99 错误回执
    /// </summary>
    public class Server
    {
        WebSocketServer webSocketServer = null;
        Asr asr = null;
        TtsMatchaIcefall tts = null;
        Llm llm = null;
        IWebSocketConnection client;
        readonly object clientLock = new object();

        const double checkRate = 1000;
        const long offlineTime = 10;          // 心跳超时秒数（原 3 秒过于激进，弱网易误断）
        long lastTickTime = 0;
        Timer timer;

        public Server()
        {
            asr = new Asr();
            llm = new Llm();

            tts = new TtsMatchaIcefall();
            llm.tts = tts;
            asr.llm = llm;

            Console.WriteLine("tts llm asr ok");

            // 心跳检测定时器全局只建一个，避免每次上线泄漏一个 Timer
            timer = new Timer(checkRate);
            timer.Elapsed += CheckTickTime;
            timer.AutoReset = true;

            webSocketServer = new WebSocketServer("ws://172.32.151.240:9999");
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
                tts.UpdateClient(client);
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
                tts.UpdateClient(null);
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
                    // 开始说话：打断上一轮全链路，并清掉 ASR 里的陈旧数据
                    llm?.Interrupt();
                    tts?.Interrupt();
                    asr?.ResetBuffer();
                    break;

                case 2:
                    // 结束说话：触发识别
                    tts?.Interrupt();
                    asr?.EndReceive();
                    break;

                default:
                    Console.WriteLine($"[WS] 未知 code={baseMsg.code}");
                    break;
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
                tts.UpdateClient(null);
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
            webSocketServer?.Dispose();
            webSocketServer = null;
        }
    }
}
