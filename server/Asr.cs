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
        private readonly object bufferLock = new object();

        // 上行为 16000Hz / 16bit / 单声道裸 PCM，无编解码、无长度前缀。
        // 缓冲上限 60 秒，超出丢弃最旧数据，防止长按导致内存无限增长。
        private const int MaxBufferBytes = 16000 * 2 * 60;

        public void Receive(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            lock (bufferLock)
            {
                buffer.AddRange(bytes);
                if (buffer.Count > MaxBufferBytes)
                {
                    int overflow = buffer.Count - MaxBufferBytes;
                    buffer.RemoveRange(0, overflow);
                    Console.WriteLine($"[ASR] 录音缓冲超过上限，丢弃最旧 {overflow} 字节");
                }
            }
        }

        /// <summary>
        /// 结束接收语音数据
        /// </summary>
        public void EndReceive()
        {
            byte[] pcm;
            lock (bufferLock)
            {
                pcm = buffer.ToArray();
                buffer.Clear();
            }

            // 至少 100ms 才送识别，过滤误触
            if (pcm.Length < 16000 * 2 / 10)
            {
                Console.WriteLine($"[ASR] 录音过短（{pcm.Length} 字节），忽略");
                return;
            }

            try
            {
                Denoise(pcm);
            }
            catch (Exception e)
            {
                Console.WriteLine("[ASR] 识别流程异常: " + e.Message);
                NotifyError("识别失败");
            }
        }

        /// <summary>
        /// 录音开始时清空残留缓冲，避免上一轮数据混入
        /// </summary>
        public void ResetBuffer()
        {
            lock (bufferLock)
            {
                buffer.Clear();
            }
        }

        private void NotifyError(string message)
        {
            var conn = client;
            if (conn != null && conn.IsAvailable)
            {
                try
                {
                    conn.Send(JsonConvert.SerializeObject(new BaseMsg(99, message)));
                }
                catch (Exception e)
                {
                    Console.WriteLine("[ASR] 错误回执发送失败: " + e.Message);
                }
            }
        }

        float[] denoisedSamples;
        void Denoise(byte[] bytes)
        {
            // 裸 PCM（16bit 小端）→ short[] → float[]
            int sampleCount = bytes.Length / 2;   // 丢掉可能出现的奇数尾字节
            if (sampleCount == 0)
            {
                return;
            }

            short[] int16Array = new short[sampleCount];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, sampleCount * 2);

            float[] floatArray = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                floatArray[i] = int16Array[i] / 32768.0f;
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

            if (string.IsNullOrWhiteSpace(result))
            {
                NotifyError("没有听清，请再说一次");
                return;
            }

            result = offlinePunctuation.AddPunct(result.ToLower());

            var conn = client;
            if (conn == null || !conn.IsAvailable)
            {
                Console.WriteLine("[ASR] 客户端已断开，丢弃识别结果");
                return;
            }

            try
            {
                conn.Send(JsonConvert.SerializeObject(new BaseMsg(1, result)));
            }
            catch (Exception e)
            {
                Console.WriteLine("[ASR] 识别结果发送失败: " + e.Message);
            }

            if (llm != null)
            {
                // 先打断上一轮（LLM + TTS 全链路）
                llm.Interrupt();
                llm.RequestAsync(result);
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