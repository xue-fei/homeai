using Fleck;
using Newtonsoft.Json;
using SherpaOnnx;

namespace server
{
    public class Tts
    {
        OfflineTts ot;
        OfflineTtsGeneratedAudio otga;
        OfflineTtsConfig config;
        OfflineTtsCallback otc;
        bool initDone = false;
        int SampleRate = 22050;
        string modelPath;

        public IWebSocketConnection client = null;

        public Tts()
        {
            modelPath = Environment.CurrentDirectory + "/vits-melo-tts-zh_en";
            config = new OfflineTtsConfig();
            config.Model.Vits.Model = Path.Combine(modelPath, "model.onnx");
            config.Model.Vits.Lexicon = Path.Combine(modelPath, "lexicon.txt");
            config.Model.Vits.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.Model.Vits.DictDir = Path.Combine(modelPath, "dict");
            config.Model.Vits.NoiseScale = 0.667f;
            config.Model.Vits.NoiseScaleW = 0.8f;
            config.Model.Vits.LengthScale = 1f;
            config.Model.NumThreads = 5;
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";
            config.RuleFsts = modelPath + "/phone.fst" + ","
                        + modelPath + "/date.fst" + ","
                    + modelPath + "/number.fst";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            Console.WriteLine("SampleRate:" + SampleRate);
            if (!Directory.Exists(Environment.CurrentDirectory + "/audio"))
            {
                Directory.CreateDirectory(Environment.CurrentDirectory + "/audio");
            }
            initDone = true;
            Thread thread = new Thread(Update);
            thread.Start();
        }

        public void Start(IWebSocketConnection connection)
        {
            client = connection;
            BaseMsg tempMsg = new BaseMsg(-1, "tts is ready");
            client.Send(JsonConvert.SerializeObject(tempMsg));
            Console.WriteLine("tts is ready");
        }

        public void Generate(string text, float speed, int speakerId)
        {
            if (!initDone)
            {
                Console.WriteLine("文字转语音未完成初始化");
                return;
            }
            otga = ot.Generate(text, speed, speakerId);
            string fileName = Environment.CurrentDirectory + "/audio/" + DateTime.Now.ToFileTime() + ".wav";
            bool ok = otga.SaveToWaveFile(fileName);
            if (ok)
            {
                //Console.WriteLine("Save file succeeded!");
                if (File.Exists(fileName))
                {
                    try
                    {
                        using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                        {
                            byte[] audiobs = new byte[fs.Length];

                            // 安全读取全部数据
                            int totalBytesRead = 0;
                            while (totalBytesRead < audiobs.Length)
                            {
                                int bytesRead = fs.Read(audiobs, totalBytesRead, audiobs.Length - totalBytesRead);
                                if (bytesRead == 0)
                                {
                                    break; // 流结束
                                }
                                totalBytesRead += bytesRead;
                            }

                            // 验证实际读取长度
                            if (totalBytesRead > 0)
                            {
                                // 使用Buffer.BlockCopy优化入队
                                byte[] validData = new byte[totalBytesRead];
                                Buffer.BlockCopy(audiobs, 0, validData, 0, totalBytesRead);

                                foreach (byte b in validData)
                                {
                                    dataQueue.Enqueue(b);
                                }
                            }
                        }

                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
            else
            {
                //Console.WriteLine("Failed");
            }
        }

        private Queue<byte> dataQueue = new Queue<byte>();
        private readonly object queueLock = new object();

        public void Update()
        {
            while (true)
            {
                List<byte> bytesToSend = new List<byte>();
                lock (queueLock)
                {
                    if (dataQueue.Count >= 2048)
                    {
                        for (int i = 0; i < 2048; i++)
                        {
                            bytesToSend.Add(dataQueue.Dequeue());
                        }
                    }
                    else if (dataQueue.Count > 0)
                    {
                        int count = dataQueue.Count;
                        for (int i = 0; i < count; i++)
                        {
                            bytesToSend.Add(dataQueue.Dequeue());
                        }
                    }
                }
                if (bytesToSend.Count > 0 && client != null && client.IsAvailable)
                {
                    client.Send(bytesToSend.ToArray());
                }
                Thread.Sleep(10);
            }
        }

        public void Stop()
        {
            if (ot != null)
            {
                ot.Dispose();
            }
            if (otc != null)
            {
                otc = null;
            }
            if (otga != null)
            {
                otga.Dispose();
            }
        }
    }
}