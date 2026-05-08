using Fleck;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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

                // 清空队列
                while (sendQueue.TryDequeue(out _)) { }

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

            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp(floatData[i] * 32767f * volume, short.MinValue, short.MaxValue);
                sendQueue.Enqueue((byte)(s & 0xFF));
                sendQueue.Enqueue((byte)(s >> 8 & 0xFF));
            }

            return n;
        }

        private void SendLoop()
        {
            var buffer = new List<byte>(sendChunkSize);

            while (true)
            {
                buffer.Clear();

                while (buffer.Count < sendChunkSize && sendQueue.TryDequeue(out byte b))
                {
                    buffer.Add(b);
                }
                if (buffer.Count > 0)
                {
                    if (client != null && client.IsAvailable)
                    {
                        try
                        {
                            client.Send(buffer.ToArray());
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