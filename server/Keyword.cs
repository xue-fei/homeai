using SherpaOnnx;

namespace server
{
    /// <summary>
    /// 关键字识别
    /// </summary>
    public class Keyword
    {
        KeywordSpotter keywordSpotter;
        string modelPath;
        OnlineStream onlineStream;
        int sampleRate = 16000;

        public Keyword()
        {
            //需要将此文件夹拷贝到exe所在的目录
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01";
            KeywordSpotterConfig config = new KeywordSpotterConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;

            config.ModelConfig.Transducer.Encoder = Path.Combine(modelPath, "encoder-epoch-12-avg-2-chunk-16-left-64.onnx");
            config.ModelConfig.Transducer.Decoder = Path.Combine(modelPath, "decoder-epoch-12-avg-2-chunk-16-left-64.onnx");
            config.ModelConfig.Transducer.Joiner = Path.Combine(modelPath, "joiner-epoch-12-avg-2-chunk-16-left-64.onnx");

            config.ModelConfig.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.NumThreads = 1;
            config.ModelConfig.Debug = 0;
            config.KeywordsFile = Path.Combine(modelPath, "keywords.txt");
            keywordSpotter = new KeywordSpotter(config);
            onlineStream = keywordSpotter.CreateStream();
        }

        public string Recognize(float[] audio)
        {
            onlineStream.AcceptWaveform(sampleRate, audio);
            float[] tailPadding = new float[(int)(sampleRate * 0.3)];
            onlineStream.AcceptWaveform(sampleRate, tailPadding);
            onlineStream.InputFinished();
            while (keywordSpotter.IsReady(onlineStream))
            {
                keywordSpotter.Decode(onlineStream);
                var result = keywordSpotter.GetResult(onlineStream);
                if (result.Keyword != string.Empty)
                {
                    // Remember to call Reset() right after detecting a keyword
                    keywordSpotter.Reset(onlineStream);
                    Console.WriteLine("Detected: {0}", result.Keyword);
                    return result.Keyword;
                }
            }
            return string.Empty;
        }
    }
}