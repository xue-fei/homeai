using Fleck;
using Newtonsoft.Json;
using System.Text;

namespace server
{
    public class Server
    {
        static WebSocketServer webSocketServer = null;
        static Dictionary<int, Asr> asrs = new Dictionary<int, Asr>();
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
            connection.OnClose += () => OnClose(connection);
        }

        private static void OnOpen(IWebSocketConnection connection)
        {
            BaseMsg textMsg = new BaseMsg(-1, "asr not ready");
            connection.Send(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(textMsg)));
            Console.WriteLine("asr not ready");
            Asr asr = new Asr();
            asrs.Add(connection.GetHashCode(), asr);
            asr.Start(connection); 
        }

        private static void OnBinary(IWebSocketConnection connection, byte[] bytes)
        {
            Asr asr = null;
            asrs.TryGetValue(connection.GetHashCode(), out asr);
            if (asr != null)
            {
                asr.Recognize(bytes);
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