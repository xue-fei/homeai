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
            config.Model.Debug = 1;
            config.Model.Provider = "cpu";
            config.RuleFsts = modelPath + "/phone.fst" + ","
                        + modelPath + "/date.fst" + ","
                    + modelPath + "/number.fst";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            Console.WriteLine("SampleRate:" + SampleRate);
            otc = new OfflineTtsCallback(OnAudioData);
            initDone = true;
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
            otga = ot.GenerateWithCallback(text, speed, speakerId, otc);
        }

        byte[] tempData;
        int OnAudioData(IntPtr samples, int n)
        {
            tempData = new byte[n];
            Marshal.Copy(samples, tempData, 0, n);
            if (client != null && client.IsAvailable)
            {
                int offset = 0; // 当前发送的起始位置
                int chunkSize = 1024; // 每次发送的字节数

                while (offset < n) // 循环直到所有数据发送完毕
                {
                    int bytesToSend = Math.Min(chunkSize, n - offset); // 计算本次发送的字节数

                    // 创建一个临时数组，用于存储本次要发送的数据
                    byte[] chunk = new byte[bytesToSend];
                    Array.Copy(tempData, offset, chunk, 0, bytesToSend); // 复制数据到临时数组

                    client.Send(chunk); // 发送数据
                    offset += bytesToSend; // 更新偏移量
                }
            }
            Console.WriteLine("n:" + n);
            return n;
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