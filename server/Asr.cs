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

        public void Receive(byte[] bytes)
        {
            buffer.AddRange(bytes);
        }

        /// <summary>
        /// 结束接收语音数据
        /// </summary>
        public void EndReceive()
        {
            Denoise(buffer.ToArray());
            buffer.Clear();
        }

        void Denoise(byte[] bytes)
        {
            // 字节数组 → short[] → float[]
            int sampleCount = bytes.Length / 2;
            short[] int16Array = new short[sampleCount];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, bytes.Length);

            float[] floatArray = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                floatArray[i] = int16Array[i] / 32767.0f;

            // ✅ 降噪后直接在内存中处理，不再落盘再读取
            DenoisedAudio denoisedAudio = offlineSpeechDenoiser.Run(floatArray, sampleRate);

            // 将降噪后的 float[] 重采样/直接送识别
            // DenoisedAudio.Samples 是 float[]，SampleRate 是输出采样率
            float[] denoisedSamples = denoisedAudio.Samples;
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
                Console.WriteLine("检测到关键词: " + kw);

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

        public float[] ReadMono16kWavToFloat(string filePath)
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new BinaryReader(fs);

            string riff = new string(reader.ReadChars(4));
            int fileSize = reader.ReadInt32();
            string wave = new string(reader.ReadChars(4));
            string fmt = new string(reader.ReadChars(4));
            int fmtSize = reader.ReadInt32();

            short audioFormat = reader.ReadInt16();
            short numChannels = reader.ReadInt16();
            int fileSampleRate = reader.ReadInt32();
            int byteRate = reader.ReadInt32();
            short blockAlign = reader.ReadInt16();
            short bitsPerSample = reader.ReadInt16();

            if (riff != "RIFF" || wave != "WAVE" || fmt != "fmt ")
                throw new Exception("无效的WAV文件头");
            if (fmtSize > 16)
                reader.ReadBytes(fmtSize - 16);

            string dataChunkId;
            do
            {
                dataChunkId = new string(reader.ReadChars(4));
                if (dataChunkId != "data")
                    reader.ReadBytes(reader.ReadInt32());
            } while (dataChunkId != "data");

            int dataSize = reader.ReadInt32();

            if (audioFormat != 1) throw new Exception("仅支持PCM格式");
            if (numChannels != 1) throw new Exception("仅支持单声道音频");
            if (fileSampleRate != 16000) throw new Exception("仅支持16kHz采样率");
            if (bitsPerSample != 16) throw new Exception("仅支持16位采样深度");

            int sampleCount = dataSize / 2;
            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                byte lo = reader.ReadByte();
                byte hi = reader.ReadByte();
                short pcm = (short)((hi << 8) | lo);
                floatData[i] = pcm / 32768.0f;
            }

            return floatData;
        }
    }
}