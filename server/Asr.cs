using Newtonsoft.Json;
using SherpaOnnx;
using Fleck;

namespace Server
{
    public class Asr
    {
        OfflineRecognizer recognizer = null;
        OfflineStream offlineStream = null;
        string tokensPath = "tokens.txt";
        string encoder = "encoder-epoch-99-avg-1.onnx";
        string decoder = "decoder-epoch-99-avg-1.onnx";
        string joiner = "joiner-epoch-99-avg-1.onnx";
        string decodingMethod = "modified_beam_search";
        int numThreads = 1;
        string modelPath;
        int sampleRate = 16000;

        OfflinePunctuation offlinePunctuation = null;
        OfflineSpeechDenoiser offlineSpeechDenoiser = null;

        IWebSocketConnection client = null;
        Keyword keyword;
        VoiceActivityDetector vad;

        public Llm llm = null;

        public Asr()
        {
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-conformer-zh-stateless2-2023-05-23";
            OfflineRecognizerConfig config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = sampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.DecodingMethod = decodingMethod;

            OfflineModelConfig offlineModelConfig = new OfflineModelConfig();
            offlineModelConfig.Tokens = Path.Combine(modelPath, tokensPath);
            offlineModelConfig.Transducer.Encoder = Path.Combine(modelPath, encoder);
            offlineModelConfig.Transducer.Decoder = Path.Combine(modelPath, decoder);
            offlineModelConfig.Transducer.Joiner = Path.Combine(modelPath, joiner);
            offlineModelConfig.NumThreads = numThreads;
            offlineModelConfig.Provider = "cpu";
            config.ModelConfig.ModelingUnit = "cjkchar";
            config.HotwordsFile = Path.Combine(modelPath, "hotwords_cn.txt");
            config.HotwordsScore = 2.0f;
            offlineModelConfig.Debug = 0;
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

            #region 语音降噪
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
            #endregion

            keyword = new Keyword();

            VadModelConfig vadModelConfig = new VadModelConfig();
            SileroVadModelConfig svmc = new SileroVadModelConfig();
            svmc.Model = Environment.CurrentDirectory + "/silero_vad.onnx";
            svmc.MinSilenceDuration = 0.25f;
            svmc.MinSpeechDuration = 0.5f;
            svmc.Threshold = 0.5f;
            svmc.WindowSize = 512;
            vadModelConfig.SileroVad = svmc;
            vadModelConfig.SampleRate = sampleRate;
            vadModelConfig.NumThreads = numThreads;
            vadModelConfig.Provider = "cpu";
            vadModelConfig.Debug = 0;
            vad = new VoiceActivityDetector(vadModelConfig, 60);
        }

        public void UpdateClient(IWebSocketConnection connection)
        {
            client = connection;
            if (connection == null)
            {
                // 断开时同时打断 LLM 和 TTS
                llm?.Interrupt();
            }
        }

        List<byte> buffer = new List<byte>();

        // Opus 解码器
        private OpusCodec opusCodec = null;
        private bool isOpusEncoded = false;
        
        // Opus 帧解析缓冲区
        private List<byte> opusParseBuffer = new List<byte>();
        private bool opusReadingLength = true;
        private int opusPacketLength = 0;

        public void Receive(byte[] bytes)
        {
            // 如果是 Opus 编码的数据，先解码为 PCM
            if (isOpusEncoded)
            {
                ParseAndDecodeOpus(bytes);
            }
            else
            {
                buffer.AddRange(bytes);
            }
        }

        /// <summary>
        /// 解析并解码 Opus 帧数据
        /// 帧格式: [2字节长度前缀(小端序)] + [Opus数据] 循环
        /// </summary>
        private void ParseAndDecodeOpus(byte[] bytes)
        {
            opusParseBuffer.AddRange(bytes);

            while (opusParseBuffer.Count > 0)
            {
                if (opusReadingLength)
                {
                    if (opusParseBuffer.Count < 2)
                        break;

                    opusPacketLength = opusParseBuffer[0] | (opusParseBuffer[1] << 8);
                    opusParseBuffer.RemoveRange(0, 2);
                    opusReadingLength = false;
                }
                else
                {
                    if (opusParseBuffer.Count < opusPacketLength)
                        break;

                    byte[] opusPacket = opusParseBuffer.GetRange(0, opusPacketLength).ToArray();
                    opusParseBuffer.RemoveRange(0, opusPacketLength);
                    opusReadingLength = true;

                    try
                    {
                        byte[] pcmBytes = opusCodec.DecodeToBytes(opusPacket);
                        if (pcmBytes != null)
                        {
                            buffer.AddRange(pcmBytes);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Opus 解码失败: " + e.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 启用 Opus 解码（当客户端发送 Opus 编码音频时调用）
        /// </summary>
        public void EnableOpusDecoding()
        {
            if (!isOpusEncoded)
            {
                opusCodec = new OpusCodec(sampleRate, 1, 24000);
                isOpusEncoded = true;
                Console.WriteLine("[ASR] 已启用 Opus 解码");
            }
        }

        /// <summary>
        /// 结束接收语音数据
        /// </summary>
        public void EndReceive()
        {
            Denoise(buffer.ToArray());
            buffer.Clear();
        }

        string tempFile;
        float[] denoisedSamples;
        void Denoise(byte[] bytes)
        {
            // 字节数组 → short[] → float[]
            int sampleCount = bytes.Length / 2;
            short[] int16Array = new short[sampleCount];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, bytes.Length);

            float[] floatArray = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                floatArray[i] = int16Array[i] / 32767.0f;
            }
            DenoisedAudio denoisedAudio = offlineSpeechDenoiser.Run(floatArray, sampleRate);
            denoisedSamples = denoisedAudio.Samples;
            int denoisedRate = denoisedAudio.SampleRate;
            denoisedAudio.Dispose();
            if (denoisedSamples == null || denoisedSamples.Length == 0)
            {
                Console.WriteLine("降噪结果为空，跳过识别");
                return;
            }
            Recognize(denoisedSamples, denoisedRate);
        }

        private void Recognize(float[] floatArray, int rate)
        {
            // ✅ 关键词检测结果现在被使用
            string kw = keyword.Recognize(floatArray);
            if (!string.IsNullOrEmpty(kw))
            {
                Console.WriteLine("检测到关键词: " + kw);
            }
            offlineStream = recognizer.CreateStream();
            offlineStream.AcceptWaveform(rate, floatArray);
            recognizer.Decode(offlineStream);
            string result = offlineStream.Result.Text;
            offlineStream.Dispose();
            offlineStream = null;

            Console.WriteLine("识别结果:" + result);

            if (!string.IsNullOrWhiteSpace(result))
            {
                result = offlinePunctuation.AddPunct(result.ToLower());

                if (client != null && client.IsAvailable)
                {
                    BaseMsg textMsg = new BaseMsg(1, result);
                    client.Send(JsonConvert.SerializeObject(textMsg));

                    if (llm != null)
                    {
                        // 先打断上一轮（LLM + TTS 全链路）
                        llm.Interrupt();
                        llm.RequestAsync(result);
                    }
                }
            }
        }

        public void Stop()
        {
            client = null;

            recognizer?.Dispose();
            recognizer = null;

            offlineStream?.Dispose();
            offlineStream = null;

            offlinePunctuation?.Dispose();
            offlinePunctuation = null;

            offlineSpeechDenoiser?.Dispose();
            offlineSpeechDenoiser = null;

            llm?.Stop();
            llm = null;
        } 
    }
}