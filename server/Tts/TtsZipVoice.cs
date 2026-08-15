using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Concentus;

namespace Server.Tts
{
    public class TtsZipVoice
    {
        OfflineTts ot;
        OfflineTtsConfig config;
        OfflineTtsGenerationConfig genConfig;
        bool initDone = false;
        int SampleRate = 22050;
        string modelPath;
        public IWebSocketConnection client = null;
        float volume = 1f;

        private ConcurrentQueue<byte> sendQueue = new();
        private CancellationTokenSource cts = new();
        private Task generateTask = Task.CompletedTask;
        private readonly object generateLock = new();
        private int sendChunkSize = 2048;

        // 播放完毕回调，队列耗尽且生成结束时触发
        public Action OnPlaybackFinished;

        // 标记当前是否有生成任务正在进行
        private volatile bool isGenerating = false;

        public TtsZipVoice()
        {
            modelPath = Environment.CurrentDirectory + "/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia";
            config = new OfflineTtsConfig();
            config.Model.ZipVoice.Encoder = Path.Combine(modelPath, "encoder.int8.onnx");
            config.Model.ZipVoice.Decoder = Path.Combine(modelPath, "decoder.int8.onnx");
            config.Model.ZipVoice.Lexicon = Path.Combine(modelPath, "lexicon.txt");
            config.Model.ZipVoice.Tokens = Path.Combine(modelPath, "tokens.txt");
            config.Model.ZipVoice.DataDir = Path.Combine(modelPath, "espeak-ng-data");
            config.Model.ZipVoice.Vocoder = Path.Combine(modelPath, "vocos_24khz.onnx");

            float[] samples = WavUtility.ReadMono16kWavToFloat(Path.Combine(modelPath, "test_wavs/wanwan.wav"));
            genConfig = new OfflineTtsGenerationConfig();
            genConfig.ReferenceAudio = samples;
            genConfig.ReferenceSampleRate = 16000;
            genConfig.ReferenceText = "今天我们准备了澳白、拿铁、美式和热牛奶，您想要哪一款，请跟我说。";
            genConfig.NumSteps = 4;
            genConfig.Extra["min_char_in_sentence"] = "10";

            config.Model.NumThreads = 5;
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            Console.WriteLine("SampleRate:" + SampleRate);

            if (!Directory.Exists(Environment.CurrentDirectory + "/audio"))
            {
                Directory.CreateDirectory(Environment.CurrentDirectory + "/audio");
            }
            initDone = true;

            Thread sendThread = new Thread(SendLoop) { IsBackground = true };
            sendThread.Start();
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

            lock (generateLock)
            {
                // 取消上一个任务
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                try { generateTask.Wait(500); } catch { }

                // 清空队列和帧缓冲区
                while (sendQueue.TryDequeue(out _)) { }
                pcmFrameBuffer.Clear();

                var localCts = new CancellationTokenSource();
                cts = localCts;
                isGenerating = true;

                generateTask = Task.Run(() =>
                {
                    try
                    {
                        OfflineTtsCallbackProgressWithArg callback = (samples, n, progress, arg) =>
                            OnAudioData(samples, n, localCts.Token);
                        ot.GenerateWithConfig(text, genConfig, callback);
                        //ot.GenerateWithCallback(text, speed, speakerId, callback);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("生成异常: " + e.Message);
                    }
                    finally
                    {
                        isGenerating = false;
                        // 生成结束后，如果队列也空了就立即通知
                        if (sendQueue.IsEmpty)
                        {
                            OnPlaybackFinished?.Invoke();
                        }
                    }
                }, localCts.Token);
            }
        }

        /// <summary>
        /// 打断当前生成并清空队列
        /// </summary>
        public void Interrupt()
        {
            lock (generateLock)
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    Console.WriteLine("[TTS] 已打断生成");
                }

                try { generateTask.Wait(500); } catch { }

                while (sendQueue.TryDequeue(out _)) { }
                pcmFrameBuffer.Clear();
                isGenerating = false;
            }
        }

        private int OnAudioData(nint samples, int n, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                Console.WriteLine("[TTS] 停止生成（回调中断）");
                return 0;
            }

            float[] floatData = new float[n];
            Marshal.Copy(samples, floatData, 0, n);

            // 重采样到目标采样率（22050 -> 16000）
            if (resampler != null)
            {
                int inLen = n;
                float[] resampledOut = new float[n * 2 + 64];
                int outLen = resampledOut.Length;
                var inSpan = floatData.AsSpan();
                var outSpan = resampledOut.AsSpan();
                resampler.ProcessInterleaved(inSpan, ref inLen, outSpan, ref outLen);
                // 用重采样后的数据（截取有效长度）
                float[] resampledData = new float[outLen];
                Array.Copy(resampledOut, resampledData, outLen);
                floatData = resampledData;
                n = outLen;
            }

            // 将 float 转为 16-bit PCM 字节
            byte[] pcmBytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp(floatData[i] * 32767f * volume, short.MinValue, short.MaxValue);
                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)(s >> 8 & 0xFF);
            }

            // 如果有 Opus 编码器，将 PCM 编码为 Opus 后发送
            if (opusEncoder != null)
            {
                EncodeAndQueueOpus(pcmBytes);
            }
            else
            {
                // 兼容模式：直接发送原始 PCM
                for (int i = 0; i < pcmBytes.Length; i++)
                {
                    sendQueue.Enqueue(pcmBytes[i]);
                }
            }

            return n;
        }

        private OpusCodec opusEncoder = null;
        private List<byte> pcmFrameBuffer = new List<byte>();
        private int opusFrameSize; // 每帧采样数
        private IResampler resampler = null; // 22050 -> 16000 重采样器
        private const int TARGET_SAMPLE_RATE = 16000; // 目标采样率（客户端 I2S 播放采样率）

        /// <summary>
        /// 启用 Opus 编码（TTS 输出将编码为 Opus 格式发送）
        /// </summary>
        public void EnableOpusEncoding()
        {
            if (opusEncoder == null)
            {
                // Opus 只支持 8/12/16/24/48 kHz，模型输出 22050 非法，需重采样到 16000
                int targetRate = TARGET_SAMPLE_RATE;
                if (SampleRate != targetRate)
                {
                    resampler = ResamplerFactory.CreateResampler(1, SampleRate, targetRate, 5, Console.Out);
                    Console.WriteLine($"[TTS] 启用重采样 {SampleRate} -> {targetRate} Hz");
                }
                opusEncoder = new OpusCodec(targetRate, 1, 24000);
                opusFrameSize = targetRate / 50; // 320 samples @ 16kHz
                Console.WriteLine($"[TTS] 已启用 Opus 编码，采样率: {targetRate} Hz");
            }
        }

        /// <summary>
        /// 将 PCM 字节缓冲并编码为 Opus 包，放入发送队列
        /// </summary>
        private void EncodeAndQueueOpus(byte[] pcmBytes)
        {
            pcmFrameBuffer.AddRange(pcmBytes);

            // 当缓冲区有足够数据时，逐帧编码
            int frameBytes = opusFrameSize * 2; // 16-bit = 2 bytes per sample
            while (pcmFrameBuffer.Count >= frameBytes)
            {
                byte[] frame = pcmFrameBuffer.GetRange(0, frameBytes).ToArray();
                pcmFrameBuffer.RemoveRange(0, frameBytes);

                byte[] opusPacket = opusEncoder.Encode(frame);
                if (opusPacket != null)
                {
                    // 先写入 Opus 包长度（2字节，小端序），再写入数据
                    sendQueue.Enqueue((byte)(opusPacket.Length & 0xFF));
                    sendQueue.Enqueue((byte)(opusPacket.Length >> 8 & 0xFF));
                    foreach (byte b in opusPacket)
                    {
                        sendQueue.Enqueue(b);
                    }
                }
            }
        }

        private void SendLoop()
        {
            var packetBuffer = new List<byte>();

            while (true)
            {
                packetBuffer.Clear();

                if (opusEncoder != null)
                {
                    // Opus 模式：读取完整的 Opus 包（2字节长度前缀 + 数据）
                    int lengthPrefixBytes = 0;
                    short packetLength = 0;

                    while (lengthPrefixBytes < 2)
                    {
                        if (sendQueue.TryDequeue(out byte b))
                        {
                            if (lengthPrefixBytes == 0)
                                packetLength = b;
                            else
                                packetLength |= (short)(b << 8);
                            lengthPrefixBytes++;
                        }
                        else
                        {
                            Thread.Sleep(1);
                            if (sendQueue.IsEmpty && !isGenerating)
                                break;
                        }
                    }

                    if (lengthPrefixBytes == 2 && packetLength > 0)
                    {
                        int bytesRead = 0;
                        while (bytesRead < packetLength)
                        {
                            if (sendQueue.TryDequeue(out byte b))
                            {
                                packetBuffer.Add(b);
                                bytesRead++;
                            }
                            else
                            {
                                Thread.Sleep(1);
                                if (sendQueue.IsEmpty && !isGenerating)
                                    break;
                            }
                        }
                    }
                }
                else
                {
                    // PCM 兼容模式：按固定大小分块发送
                    while (packetBuffer.Count < sendChunkSize && sendQueue.TryDequeue(out byte b))
                    {
                        packetBuffer.Add(b);
                    }
                }

                if (packetBuffer.Count > 0)
                {
                    if (client != null && client.IsAvailable)
                    {
                        try
                        {
                            client.Send(packetBuffer.ToArray());
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("发送异常: " + e.Message);
                        }
                    }
                }
                else
                {
                    // 队列为空且生成已结束，触发播放完毕回调
                    if (!isGenerating && sendQueue.IsEmpty)
                    {
                        OnPlaybackFinished?.Invoke();
                    }
                    Thread.Sleep(10);
                }
            }
        }

        public void Stop()
        {
            Interrupt();
            ot?.Dispose();
        }
    }
}