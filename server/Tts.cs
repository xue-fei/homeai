using Fleck;
using Newtonsoft.Json;
using SherpaOnnx;
using System.Runtime.InteropServices;

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
            otc = new OfflineTtsCallback(OnAudioData);
            otga = ot.GenerateWithCallback(text, speed, speakerId, otc);
        }

        /// <summary>
        /// 打断
        /// </summary>
        public void Interrupt()
        {
            if (otga != null)
            {
                otga.Dispose();
            }
            if (otc != null)
            {
                otc = null;
            }
            sendQueue.Clear();
        }

        private int OnAudioData(nint samples, int n)
        {
            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);
            short[] shortData = new short[n];
            for (int i = 0; i < n; i++)
            {
                shortData[i] = Math.Clamp((short)(floatData[i] * 32768f), short.MinValue, short.MaxValue);
            }
            Task task = new Task(() =>
            {
                HandleFloatData(shortData);
            });
            task.Start();
            return n;
        }

        void HandleFloatData(short[] shortData)
        {
            byte[] byteData = new byte[shortData.Length * 2];
            Buffer.BlockCopy(shortData, 0, byteData, 0, byteData.Length);
            foreach (byte b in byteData)
            {
                sendQueue.Enqueue(b);
            }
        }

        /// <summary>
        /// 20M的音频数据队列
        /// </summary>
        private Queue<byte> sendQueue = new Queue<byte>(10240000*2);

        public void Update()
        {
            while (true)
            {
                List<byte> bytesToSend = new List<byte>();
                if (sendQueue.Count >= 1536)
                {
                    for (int i = 0; i < 1536; i++)
                    {
                        bytesToSend.Add(sendQueue.Dequeue());
                    }
                }
                else if (sendQueue.Count > 0)
                {
                    int count = sendQueue.Count;
                    for (int i = 0; i < count; i++)
                    {
                        bytesToSend.Add(sendQueue.Dequeue());
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