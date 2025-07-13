using Fleck;
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
        float volume = 1f;

        public Tts()
        {
            modelPath = Environment.CurrentDirectory + "/matcha-icefall-zh-baker";
            config = new OfflineTtsConfig();
            config.Model.Matcha.AcousticModel = Path.Combine(modelPath, "model-steps-3.onnx");
            config.Model.Matcha.Vocoder = Path.Combine(modelPath, "vocos-22khz-univ.onnx");
            config.Model.Matcha.Lexicon = Path.Combine(modelPath, "lexicon.txt");
            config.Model.Matcha.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.Model.Matcha.DictDir = Path.Combine(modelPath, "dict"); 
            config.Model.Matcha.LengthScale = 1f;
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

        public void UpdateClient(IWebSocketConnection connection)
        {
            client = connection;
            if (connection == null)
            {
                Interrupt();
            }
        }

        public void Generate(string text, float speed, int speakerId)
        {
            if (!initDone)
            {
                Console.WriteLine("文字转语音未完成初始化");
                return;
            }
            stopped = false;
            otc = new OfflineTtsCallback(OnAudioData);
            otga = ot.GenerateWithCallback(text, speed, speakerId, otc);
        }

        bool stopped = false;
        /// <summary>
        /// 打断
        /// </summary>
        public void Interrupt()
        {
            stopped = true;
            sendQueue.Clear();
            sendQueue = new(10240000 * 2);
        }

        private int OnAudioData(nint samples, int n)
        {
            //Console.WriteLine("OnAudioData n:" + n);
            if (stopped)
            {
                sendQueue.Clear();
                sendQueue = new(10240000 * 2);
                Console.WriteLine("停止生成");
                stopped = false;
                return 0;
            }
            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);
            short[] shortData = new short[n];
            for (int i = 0; i < n; i++)
            {
                shortData[i] = Math.Clamp((short)(floatData[i] * 32767f * volume), short.MinValue, short.MaxValue);
            }
            HandleFloatData(shortData);
            return n;
        }

        void HandleFloatData(short[] shortData)
        {
            if (stopped)
            {
                sendQueue.Clear();
                sendQueue = new(10240000 * 2);
                Console.WriteLine("停止生成");
                return;
            }
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
        private Queue<byte> sendQueue = new(10240000 * 2);
        private int count = 2048;
        public void Update()
        {
            while (true)
            {
                if (!stopped)
                {
                    List<byte> bytesToSend = new List<byte>();
                    if (sendQueue.Count >= count)
                    {
                        for (int i = 0; i < count; i++)
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