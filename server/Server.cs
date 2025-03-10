using Fleck;
using Newtonsoft.Json;

namespace server
{
    public class Server
    {
        static WebSocketServer webSocketServer = null;
        static Dictionary<int, Asr> asrs = new Dictionary<int, Asr>();
        static Dictionary<int, Tts> ttss = new Dictionary<int, Tts>();

        public Server()
        {
            webSocketServer = new WebSocketServer("ws://172.32.151.240:9999");
            //webSocketServer.Certificate =
            //    new System.Security.Cryptography.X509Certificates.X509Certificate2(
            //        Environment.CurrentDirectory + "/usherpa.xuefei.net.cn.pfx", "xb5ceehg");
            //webSocketServer.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;

            //开启监听
            webSocketServer.Start(OnStart);
        }

        private static void OnStart(IWebSocketConnection connection)
        {
            connection.OnOpen += () => OnOpen(connection);
            connection.OnBinary = bytes => OnBinary(connection, bytes);
            connection.OnMessage = msg => OnMessage(connection, msg);
            connection.OnClose += () => OnClose(connection);
        }

        private static void OnOpen(IWebSocketConnection connection)
        {
            BaseMsg textMsg2 = new BaseMsg(-1, "tts not ready");
            connection.Send(JsonConvert.SerializeObject(textMsg2));
            Console.WriteLine("tts not ready");

            Tts tts = new Tts();
            ttss.Add(connection.GetHashCode(), tts);
            tts.Start(connection);

            BaseMsg textMsg1 = new BaseMsg(-1, "asr not ready");
            connection.Send(JsonConvert.SerializeObject(textMsg1));
            Console.WriteLine("asr not ready");

            Asr asr = new Asr();
            asrs.Add(connection.GetHashCode(), asr);
            asr.Start(connection, tts);
        }

        private static void OnBinary(IWebSocketConnection connection, byte[] bytes)
        {
            Asr asr = null;
            asrs.TryGetValue(connection.GetHashCode(), out asr);
            if (asr != null)
            {
                asr.Receive(bytes);
            }
        }

        private static void OnMessage(IWebSocketConnection connection, string msg)
        {
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
                // 收到code 1时，开始录音
                if (baseMsg.code == 1)
                {
                    Tts tts = null;
                    ttss.TryGetValue(connection.GetHashCode(), out tts);
                    if (tts != null)
                    {
                        tts.Interrupt();
                    }
                }
                // 收到code 2时，开始识别
                if (baseMsg.code == 2)
                {
                    Asr asr = null;
                    asrs.TryGetValue(connection.GetHashCode(), out asr);
                    if (asr != null)
                    {
                        asr.EndReceive();
                    }
                }
            }
        }

        private static void OnClose(IWebSocketConnection connection)
        {
            Asr asr = null;
            asrs.TryGetValue(connection.GetHashCode(), out asr);
            if (asr != null)
            {
                asr.Stop();
                asrs.Remove(connection.GetHashCode());
                asr = null;
            }
        }

        ~Server()
        {
            webSocketServer.Dispose();
        }
    }
}