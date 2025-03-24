using Newtonsoft.Json;
using SherpaOnnx;
using Fleck;

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
        OfflineSpeechDenoiser offlineSpeechDenoiser = null;

        IWebSocketConnection client = null;
        Keyword keyword;

        public Llm llm = null;

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
            offlineModelConfig.Debug = 0;

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
            OfflineSpeechDenoiserGtcrnModelConfig osdgmc = new OfflineSpeechDenoiserGtcrnModelConfig();
            osdgmc.Model = Environment.CurrentDirectory + "/gtcrn_simple.onnx";
            OfflineSpeechDenoiserModelConfig osdmc = new OfflineSpeechDenoiserModelConfig();
            osdmc.NumThreads = numThreads;
            osdmc.Provider = "cpu";
            osdmc.Debug = 0;
            osdmc.Gtcrn = osdgmc;
            OfflineSpeechDenoiserConfig osdc = new OfflineSpeechDenoiserConfig();
            osdc.Model = osdmc;
            offlineSpeechDenoiser = new OfflineSpeechDenoiser(osdc);

            keyword = new Keyword();
        }

        public void UpdateClient(IWebSocketConnection connection)
        {
            client = connection;
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
            //File.WriteAllBytes(Environment.CurrentDirectory + "/"
            //    + "test.pcm", buffer.ToArray());
            Recognize(buffer.ToArray());
            buffer.Clear();
        }

        DenoisedAudio denoisedAudio;
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
            // 语音增强
            denoisedAudio = offlineSpeechDenoiser.Run(floatArray, sampleRate);
            floatArray = denoisedAudio.Samples; 

            keyword.Recognize(floatArray);

            offlineStream = recognizer.CreateStream();
            offlineStream.AcceptWaveform(sampleRate, floatArray);
            recognizer.Decode(offlineStream);
            string result = offlineStream.Result.Text;
            offlineStream.Dispose();
            Console.WriteLine("识别结果:" + result);
            if (!string.IsNullOrWhiteSpace(result))
            {
                result = offlinePunctuation.AddPunct(result.ToLower());
                BaseMsg textMsg = new BaseMsg(1, result);
                client.Send(JsonConvert.SerializeObject(textMsg));
                if (llm != null)
                {
                    llm.RequestAsync(result);
                }
            }
        }

        public void Stop()
        {
            client = null;
            if (recognizer != null)
            {
                recognizer.Dispose();
                recognizer = null;
            }
            if (offlineStream != null)
            {
                offlineStream.Dispose();
                offlineStream = null;
            }
            if (offlinePunctuation != null)
            {
                offlinePunctuation.Dispose();
                offlinePunctuation = null;
            }
            if (llm != null)
            {
                llm.Stop();
                llm = null;
            }
            if (offlineSpeechDenoiser != null)
            {
                offlineSpeechDenoiser.Dispose();
                offlineSpeechDenoiser = null;
            }
        }
    }
}