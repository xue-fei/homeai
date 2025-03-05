﻿using Newtonsoft.Json;
using SherpaOnnx;
using Fleck;
using RNNoise.NET;

namespace server
{
    public class Asr
    {
        OfflineRecognizer recognizer = null;
        OfflineStream offlineStream = null;
        string tokensPath = "tokens.txt";
        string paraformer = "model.int8.onnx";
        string decodingMethod = "greedy_search";
        int numThreads = 1;
        string modelPath;
        int sampleRate = 16000;

        OfflinePunctuation offlinePunctuation = null;

        public IWebSocketConnection client = null;
        Tts tts;

        public Asr()
        {
            //需要将此文件夹拷贝到exe所在的目录
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-paraformer-zh-small-2024-03-09";
            OfflineRecognizerConfig config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = sampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.DecodingMethod = decodingMethod;

            OfflineModelConfig offlineModelConfig = new OfflineModelConfig();
            offlineModelConfig.Tokens = Path.Combine(modelPath, tokensPath);
            offlineModelConfig.NumThreads = numThreads;
            offlineModelConfig.Provider = "cpu";
            offlineModelConfig.Debug = 1;

            OfflineParaformerModelConfig paraformerConfig = new OfflineParaformerModelConfig();
            paraformerConfig.Model = Path.Combine(modelPath, paraformer);

            offlineModelConfig.Paraformer = paraformerConfig;
            config.ModelConfig = offlineModelConfig;

            OfflineLMConfig offlineLMConfig = new OfflineLMConfig();
            offlineLMConfig.Scale = 0.5f;
            config.LmConfig = offlineLMConfig;
            recognizer = new OfflineRecognizer(config);

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

        public void Start(IWebSocketConnection connection, Tts tts = null)
        {
            this.tts = tts;
            client = connection;
            BaseMsg tempMsg = new BaseMsg(-1, "asr is ready");
            client.Send(JsonConvert.SerializeObject(tempMsg));
            Console.WriteLine("asr is ready");
        }

        List<byte> buffer = new List<byte>();
        public void Receive(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                buffer.Add(bytes[i]);
            }
        }

        /// <summary>
        /// 结束接收语音数据
        /// </summary>
        public void EndReceive()
        {
            File.WriteAllBytes(Environment.CurrentDirectory + "/"
                + "test.pcm", buffer.ToArray());
            Recognize(buffer.ToArray());
            buffer.Clear();
        }

        /// <summary>
        /// 识别语音数据
        /// </summary>
        short[] int16Array;
        float[] floatArray;
        private void Recognize(byte[] bytes)
        {
            int16Array = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, bytes.Length);
            floatArray = new float[int16Array.Length];
            for (int i = 0; i < int16Array.Length; i++)
            {
                floatArray[i] = int16Array[i] / 32768.0f;
            }
            // 降噪
            using (var denoiser = new Denoiser())
            {
                int count = denoiser.Denoise(floatArray.AsSpan());
                Console.WriteLine("denoised count:" + count);
            }

            offlineStream = recognizer.CreateStream();
            offlineStream.AcceptWaveform(sampleRate, floatArray);
            recognizer.Decode(offlineStream);
            string result = offlineStream.Result.Text;
            offlineStream.Dispose();
            Console.WriteLine("result:" + result);
            if (!string.IsNullOrWhiteSpace(result))
            {
                result = offlinePunctuation.AddPunct(result.ToLower());
                BaseMsg textMsg = new BaseMsg(1, result);
                client.Send(JsonConvert.SerializeObject(textMsg));
                if (tts != null)
                {
                    tts.Generate(result, 1f, 0);
                }
            }
        }

        public void Stop()
        {
            client = null;
            recognizer.Dispose();
            recognizer = null;
            offlineStream.Dispose();
            offlineStream = null;
            offlinePunctuation.Dispose();
            offlinePunctuation = null;
        }
    }
}