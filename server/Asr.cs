using Newtonsoft.Json;
using SherpaOnnx;
using Fleck;

namespace server
{
    public class Asr
    {
        OnlineRecognizer recognizer = null;
        OnlineStream onlineStream = null;
        string tokensPath = "tokens.txt";
        string encoder = "encoder-epoch-99-avg-1.onnx";
        string decoder = "decoder-epoch-99-avg-1.onnx";
        string joiner = "joiner-epoch-99-avg-1.onnx";
        int numThreads = 1;
        string decodingMethod = "modified_beam_search";

        string modelPath;
        int sampleRate = 16000;

        OfflinePunctuation offlinePunctuation = null;

        public IWebSocketConnection client = null;
        static float gain = 5.0f;
        Tts tts;

        public Asr()
        {
            //需要将此文件夹拷贝到exe所在的目录
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20";
            // 初始化配置
            OnlineRecognizerConfig config = new OnlineRecognizerConfig();
            config.FeatConfig.SampleRate = sampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = Path.Combine(modelPath, encoder);
            config.ModelConfig.Transducer.Decoder = Path.Combine(modelPath, decoder);
            config.ModelConfig.Transducer.Joiner = Path.Combine(modelPath, joiner);
            config.ModelConfig.Tokens = Path.Combine(modelPath, tokensPath);
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = decodingMethod;
            config.EnableEndpoint = 1;

            // 创建识别器和在线流
            recognizer = new OnlineRecognizer(config);
            onlineStream = recognizer.CreateStream();

            #region 添加标点符号
            OfflinePunctuationConfig opc = new OfflinePunctuationConfig();

            OfflinePunctuationModelConfig opmc = new OfflinePunctuationModelConfig();
            opmc.CtTransformer = Environment.CurrentDirectory + "/sherpa-onnx-punct-ct-transformer-zh-en-vocab272727-2024-04-12/model.onnx";
            opmc.NumThreads = numThreads;
            opmc.Provider = "cpu";
            opmc.Debug = 0;

            opc.Model = opmc;
            offlinePunctuation = new OfflinePunctuation(opc);
            #endregion
        }

        static string text = "";
        static string lastText = "";
        public void Start(IWebSocketConnection connection, Tts tts = null)
        {
            this.tts = tts;
            client = connection;
            BaseMsg tempMsg = new BaseMsg(-1, "asr is ready");
            client.Send(JsonConvert.SerializeObject(tempMsg));
            Console.WriteLine("asr is ready");

            while (true)
            {
                if (client == null || recognizer == null || onlineStream == null)
                {
                    break;
                }
                // 每帧更新识别器状态
                if (recognizer.IsReady(onlineStream))
                {
                    recognizer.Decode(onlineStream);
                }

                text = recognizer.GetResult(onlineStream).Text;
                bool isEndpoint = recognizer.IsEndpoint(onlineStream);
                if (!string.IsNullOrWhiteSpace(text) && lastText != text)
                {
                    if (string.IsNullOrWhiteSpace(lastText))
                    {
                        lastText = text;
                        if (client != null && client.IsAvailable)
                        {
                            BaseMsg textMsg = new BaseMsg(0, text.ToLower());
                            client.Send(JsonConvert.SerializeObject(textMsg));
                            //Console.WriteLine("text1:" + text);
                        }
                    }
                    else
                    {
                        if (client != null && client.IsAvailable)
                        {
                            //client.Send(Encoding.UTF8.GetBytes(text.Replace(lastText, "")));
                            BaseMsg textMsg = new BaseMsg(0, text.Replace(lastText, "").ToLower());
                            client.Send(JsonConvert.SerializeObject(textMsg));
                            lastText = text;
                        }
                    }
                }

                if (isEndpoint)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (client != null && client.IsAvailable)
                        {
                            BaseMsg textMsg = new BaseMsg(1, offlinePunctuation.AddPunct(text.ToLower()));
                            client.Send(JsonConvert.SerializeObject(textMsg));
                            if(tts!=null)
                            {
                                tts.Generate(offlinePunctuation.AddPunct(text.ToLower()), 1f, 0);
                            }
                        }
                        //Console.WriteLine(offlinePunctuation.AddPunctuation(text));
                    }
                    recognizer.Reset(onlineStream);
                    //Console.WriteLine("Reset");
                }
                Thread.Sleep(200); // ms
            }
        }

        short[] int16Array;
        float[] floatArray;
        public void Recognize(byte[] bytes)
        {
            int16Array = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, bytes.Length);
            floatArray = new float[int16Array.Length];
            for (int i = 0; i < int16Array.Length; i++)
            {
                floatArray[i] = int16Array[i] / 32768.0f * gain;
            }
            onlineStream.AcceptWaveform(sampleRate, floatArray);
        }

        public void Stop()
        {
            client = null;
            recognizer.Dispose();
            recognizer = null;
            onlineStream.Dispose();
            onlineStream = null;
            offlinePunctuation.Dispose();
            offlinePunctuation = null;
        }
    }
}