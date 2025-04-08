using Fleck;
using Newtonsoft.Json;

namespace server
{
    public class Server
    {
        WebSocketServer webSocketServer = null;
        Asr asr = null;
        Tts tts = null;
        Llm llm = null;

        public Server()
        {
            asr = new Asr();
            llm = new Llm();

            tts = new Tts();
            llm.tts = tts;
            asr.llm = llm;

            Console.WriteLine("tts llm asr ok");

            webSocketServer = new WebSocketServer("ws://192.168.0.164:9999");
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
            tts.UpdateClient(connection);
            asr.UpdateClient(connection);
            Console.WriteLine("上线了");
        }

        private void OnBinary(IWebSocketConnection connection, byte[] bytes)
        {
            if (asr != null)
            {
                asr.Receive(bytes);
            }
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
                Console.WriteLine(e);
            }
            if (baseMsg != null)
            {
                // 收到code 0时，心跳消息
                if (baseMsg.code == 0)
                {

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
            tts.UpdateClient(null);
            asr.UpdateClient(null);
            Console.WriteLine("下线了");
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