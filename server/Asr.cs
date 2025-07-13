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
        SileroVadModelConfig svmc;
        VoiceActivityDetector vad;

        public Llm llm = null;

        public Asr()
        {
            //需要将此文件夹拷贝到exe所在的目录
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

            VadModelConfig vadModelConfig = new VadModelConfig();

            svmc = new SileroVadModelConfig();
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
                llm.Interrupt();
            }
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
            Denoise(buffer.ToArray());
            buffer.Clear();
        }

        DenoisedAudio denoisedAudio;
        short[] int16Array;
        float[] floatArray;

        void Denoise(byte[] bytes)
        {
            int16Array = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, int16Array, 0, bytes.Length);
            floatArray = new float[int16Array.Length];
            for (int i = 0; i < int16Array.Length; i++)
            {
                floatArray[i] = (int16Array[i] / 32767.0f);
            }
            denoisedAudio = offlineSpeechDenoiser.Run(floatArray, sampleRate);
            string file = Environment.CurrentDirectory + "/audio/" + DateTime.Now.ToFileTime() + ".wav";
            if (denoisedAudio.SaveToWaveFile(file))
            { 
                float[] audioFs = ReadMono16kWavToFloat(file);
                Recognize(audioFs);
            }
            else
            {
                Console.WriteLine("降噪音频保存失败");
            }
            denoisedAudio.Dispose();
        }
        
        private void Recognize(float[] floatArray)
        { 
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
                if (client != null && client.IsAvailable)
                {
                    client.Send(JsonConvert.SerializeObject(textMsg));
                    if (llm != null)
                    {
                        llm.Interrupt();
                        llm.RequestAsync(result);
                    }
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

        public float[] ReadMono16kWavToFloat(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                // 读取WAV文件头
                string riff = new string(reader.ReadChars(4));    // "RIFF"
                int fileSize = reader.ReadInt32();                // 文件总大小-8
                string wave = new string(reader.ReadChars(4));    // "WAVE"
                string fmt = new string(reader.ReadChars(4));     // "fmt "
                int fmtSize = reader.ReadInt32();                 // fmt块大小（至少16）

                // 读取音频格式信息
                short audioFormat = reader.ReadInt16();           // 1=PCM
                short numChannels = reader.ReadInt16();           // 通道数
                int sampleRate = reader.ReadInt32();              // 采样率
                int byteRate = reader.ReadInt32();                // 字节率
                short blockAlign = reader.ReadInt16();            // 块对齐
                short bitsPerSample = reader.ReadInt16();         // 采样深度

                // 验证文件格式
                if (riff != "RIFF" || wave != "WAVE" || fmt != "fmt ")
                    throw new Exception("无效的WAV文件头");

                // 跳过fmt块的额外信息（如果有）
                if (fmtSize > 16)
                    reader.ReadBytes(fmtSize - 16);

                // 查找数据块
                string dataChunkId;
                do
                {
                    dataChunkId = new string(reader.ReadChars(4));
                    if (dataChunkId != "data")
                        reader.ReadBytes(reader.ReadInt32()); // 跳过非数据块
                } while (dataChunkId != "data");

                int dataSize = reader.ReadInt32(); // 数据块大小（字节）

                // 验证音频参数
                if (audioFormat != 1)
                    throw new Exception("仅支持PCM格式");
                if (numChannels != 1)
                    throw new Exception("仅支持单声道音频");
                if (sampleRate != 16000)
                    throw new Exception("仅支持16kHz采样率");
                if (bitsPerSample != 16)
                    throw new Exception("仅支持16位采样深度");

                // 读取PCM数据并转换为float
                int sampleCount = dataSize / 2; // 16位 = 2字节/样本
                float[] floatData = new float[sampleCount];

                for (int i = 0; i < sampleCount; i++)
                {
                    // 小端序读取16位样本
                    byte lowByte = reader.ReadByte();
                    byte highByte = reader.ReadByte();
                    short pcmValue = (short)((highByte << 8) | lowByte);

                    // 将16位PCM值转换为[-1.0, 1.0]范围的float
                    floatData[i] = pcmValue / 32768.0f;
                }

                return floatData;
            }
        }
    }
}