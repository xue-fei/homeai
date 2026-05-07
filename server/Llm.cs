using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace server
{
    public class Llm
    {
        public Tts tts;
        OllamaApiClient ollama;
        List<Message> chatHistory;
        string modelName;
        string sysTip;

        // 实时句子缓冲区
        StringBuilder sentenceBuffer = new StringBuilder();
        // 句子结束符正则（支持中英文标点）
        Regex sentenceDelimiters = new Regex(@"[。！？.!?](\s|$)|[。！？.!?][""'](\s|$)");

        CancellationTokenSource cts;

        // ── TTS 句子播放队列 ──────────────────────────────────────
        private ConcurrentQueue<string> ttsQueue = new ConcurrentQueue<string>();
        private SemaphoreSlim ttsSemaphore = new SemaphoreSlim(0);
        private volatile bool ttsInterrupted = false;
        private volatile bool ttsPlaying = false;
        // ─────────────────────────────────────────────────────────

        public Llm(string modelName = "qwen2.5:1.5b", string sysTip = "")
        {
            this.modelName = modelName;
            this.sysTip = sysTip;

            var uri = new Uri("http://localhost:11434");
            ollama = new OllamaApiClient(uri, modelName);

            // 初始化对话历史
            chatHistory = new List<Message>();
            if (!string.IsNullOrEmpty(sysTip))
            {
                chatHistory.Add(new Message(ChatRole.System, sysTip));
            }
            // 启动 TTS 播放调度线程
            Thread ttsThread = new Thread(TtsPlayLoop) { IsBackground = true };
            ttsThread.Start();
        }

        /// <summary>
        /// 将句子加入 TTS 播放队列
        /// </summary>
        private void EnqueueTts(string sentence)
        {
            ttsQueue.Enqueue(sentence);
            ttsSemaphore.Release();
        }

        /// <summary>
        /// TTS 播放调度循环（独立线程）
        /// 逐句取出，等上一句播完再播下一句
        /// </summary>
        private void TtsPlayLoop()
        {
            while (true)
            {
                ttsSemaphore.Wait();

                // 被打断：清空队列残留
                if (ttsInterrupted)
                {
                    while (ttsQueue.TryDequeue(out _)) { }
                    ttsInterrupted = false;
                    ttsPlaying = false;
                    continue;
                }

                if (!ttsQueue.TryDequeue(out string sentence))
                {
                    continue;
                }

                if (tts == null)
                {
                    continue;
                }
                Console.WriteLine($"[TTS播放] {sentence}");
                ttsPlaying = true;

                using var playDone = new ManualResetEventSlim(false);

                // 注册播放完毕回调
                tts.OnPlaybackFinished = () =>
                {
                    tts.OnPlaybackFinished = null;
                    playDone.Set();
                };

                tts.Generate(sentence, 1f, 0);

                // 等待播放完毕，每 50ms 检查一次是否被打断
                while (!playDone.Wait(50))
                {
                    if (ttsInterrupted)
                    {
                        tts.OnPlaybackFinished = null;
                        break;
                    }
                }

                ttsPlaying = false;
            }
        }

        public async void RequestAsync(string prompt)
        {
            chatHistory.Add(new Message(ChatRole.User, prompt));

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            var chatRequest = new ChatRequest
            {
                Messages = chatHistory,
                Model = modelName
            };

            try
            {
                Console.WriteLine("发起请求:" + prompt);
                var responseStream = ollama.ChatAsync(chatRequest, token);

                StringBuilder fullResponse = new StringBuilder();

                await foreach (var response in responseStream)
                {
                    token.ThrowIfCancellationRequested();
                    if (response?.Message == null) continue;

                    string content = response.Message.Content;
                    if (string.IsNullOrEmpty(content))
                    {
                        if (response.Done)
                        {
                            Console.WriteLine("模型回答结束(空内容Done)");
                        }
                        continue;
                    }

                    sentenceBuffer.Append(content);
                    fullResponse.Append(content);
                    ProcessBuffer(ref sentenceBuffer);
                }

                // ✅ 残留内容直接入队，不走 ProcessBuffer（无句尾标点无法匹配）
                string remaining = sentenceBuffer.ToString().Trim();
                sentenceBuffer.Clear();
                if (!string.IsNullOrEmpty(remaining))
                {
                    Console.WriteLine("刷新残留句子: " + remaining);
                    EnqueueTts(remaining);
                }

                chatHistory.Add(new Message(ChatRole.Assistant, fullResponse.ToString()));
            }
            catch (OperationCanceledException)
            {
                sentenceBuffer.Clear();
                Console.WriteLine("[Ollama] 已中断");
            }
            catch (Exception ex)
            {
                sentenceBuffer.Clear();
                Console.WriteLine($"请求出错: {ex.Message}");
            }
        }

        void ProcessBuffer(ref StringBuilder buffer)
        {
            var content = buffer.ToString();
            var lastIndex = 0;

            var matches = sentenceDelimiters.Matches(content);
            foreach (Match match in matches)
            {
                var endPos = match.Index + match.Length;
                var sentence = content.Substring(lastIndex, endPos - lastIndex).Trim();

                if (!string.IsNullOrEmpty(sentence))
                {
                    Console.WriteLine($"模型回答: {sentence}");
                    EnqueueTts(sentence); // ✅ 入队而不是直接 Generate
                }

                lastIndex = endPos;
            }

            buffer = new StringBuilder(content.Substring(lastIndex));
        }

        /// <summary>
        /// 打断 LLM 生成 + TTS 播放
        /// </summary>
        public void Interrupt()
        {
            // 1. 停止 LLM 流式输出
            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                cts = null;
            }

            // 2. 清空句子缓冲区
            sentenceBuffer.Clear();

            // 3. 通知 TTS 播放线程清空并停止
            ttsInterrupted = true;
            ttsSemaphore.Release(); // 唤醒播放线程执行清空

            // 4. 打断 TTS 硬件层
            tts?.Interrupt();

            Console.WriteLine("[Llm] 已打断");
        }

        public void ClearHistory()
        {
            chatHistory.Clear();
            if (!string.IsNullOrEmpty(sysTip))
            {
                chatHistory.Add(new Message(ChatRole.System, sysTip));
            }
        }

        public List<Message> GetHistory() => new List<Message>(chatHistory);

        public void SetHistory(List<Message> history) => chatHistory = new List<Message>(history);

        public void Stop()
        {
            Interrupt();
            if (ollama != null)
            {
                ollama.Dispose();
                ollama = null;
            }
        }
    }
}