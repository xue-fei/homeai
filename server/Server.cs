using Fleck;
using Timer = System.Timers.Timer;
using Newtonsoft.Json;
using System.Timers;

namespace server
{
    public class Server
    {
        WebSocketServer webSocketServer = null;
        Asr asr = null;
        Tts tts = null;
        Llm llm = null;
        IWebSocketConnection client;
        float checkRate = 1000;
        float offlineTime = 3;
        long lastTickTime = 0;
        Timer timer;

        public Server()
        {
            asr = new Asr();
            llm = new Llm();

            tts = new Tts();
            llm.tts = tts;
            asr.llm = llm;

            Console.WriteLine("tts llm asr ok");

            webSocketServer = new WebSocketServer("ws://192.168.2.177:9999");
            //webSocketServer.Certificate =
            //    new System.Security.Cryptography.X509Certificates.X509Certificate2(
            //        Environment.CurrentDirectory + "/usherpa.xuefei.net.cn.pfx", "xb5ceehg");
            //webSocketServer.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12; 

            //开启监听
            webSocketServer.Start(OnStart);
        }

        private void OnStart(IWebSocketConnection connection)
        {
            connection.OnOpen += () => OnOpen(connection);
            connection.OnBinary = bytes => OnBinary(connection, bytes);
            connection.OnMessage = msg => OnMessage(connection, msg);
            connection.OnClose += () => OnClose(connection);
        }

        private void OnOpen(IWebSocketConnection connection)
        {
            client = connection;
            tts.UpdateClient(client);
            asr.UpdateClient(client);
            Console.WriteLine("上线了");
            timer = new Timer(checkRate);
            lastTickTime = GetTimeStamp();
            timer.Elapsed += CheckTickTime;
            timer.AutoReset = true;
            timer.Enabled = true;
        }

        void CheckTickTime(object sender, ElapsedEventArgs e)
        { 
            if (GetTimeStamp() - lastTickTime > offlineTime)
            {
                client.Close();
                client = null;
                tts.UpdateClient(client);
                asr.UpdateClient(client);
                Console.WriteLine("下线了");

                timer.Elapsed -= CheckTickTime;
                timer.Stop();
                timer.Dispose();
            }
        }

        private void OnBinary(IWebSocketConnection connection, byte[] bytes)
        {
            client = connection;
            if (asr != null)
            {
                asr.Receive(bytes);
            }
        }

        private void OnMessage(IWebSocketConnection connection, string msg)
        {
            client = connection;
            BaseMsg baseMsg = null;
            try
            {
                baseMsg = JsonConvert.DeserializeObject<BaseMsg>(msg);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            if (baseMsg != null)
            {
                // 收到code 0时，心跳消息
                if (baseMsg.code == 0)
                {
                    lastTickTime = GetTimeStamp();
                }
                // 收到code 1时，开始录音
                if (baseMsg.code == 1)
                {
                    if (llm != null)
                    {
                        llm.Interrupt();
                    }
                    if (tts != null)
                    {
                        tts.Interrupt();
                    }
                }
                // 收到code 2时，开始识别
                if (baseMsg.code == 2)
                {
                    if (asr != null)
                    {
                        asr.EndReceive();
                    }
                }
            }
        }

        private void OnClose(IWebSocketConnection connection)
        {
            client = connection;
            client = null;
            tts.UpdateClient(client);
            asr.UpdateClient(client);

            Console.WriteLine("下线了");
        }

        private long GetTimeStamp()
        {
            TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return Convert.ToInt64(ts.TotalSeconds);
        }

        ~Server()
        {
            if (asr != null)
            {
                asr.Stop();
            }
            if (tts != null)
            {
                tts.Stop();
            }
            webSocketServer.Dispose();
        }
    }
}