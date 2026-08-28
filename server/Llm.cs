using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using Server.Tts;

namespace Server
{
    public class Llm
    {
        public TtsMatchaIcefall tts;
        OllamaApiClient ollama;
        List<Message> chatHistory;
        string modelName;
        string sysTip;

        // 实时句子缓冲区
        StringBuilder sentenceBuffer = new StringBuilder();
        // 句子结束符正则（支持中英文标点）
        Regex sentenceDelimiters = new Regex(@"[。！？.!?](\s|$)|[。！？.!?][""'](\s|$)");

        CancellationTokenSource cts;

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
        }

        /// <summary>
        /// 把句子直接交给 TTS 排队。
        ///
        /// 【为什么不再等上一句播完】
        /// 原来这里有一个"逐句等播放完毕再合成下一句"的调度线程，导致每句话之间
        /// 都空出一整段推理时间，ESP32 抖动缓冲被抽干 -> 重新预缓冲 -> 听感就是
        /// 每句都跳一下、顿一下。
        /// 现在 TTS 内部自带文本队列 + 连续 PCM 流，句子在 PCM 层无缝拼接，
        /// 上层只负责尽快把文本喂进去。
        /// </summary>
        private void EnqueueTts(string sentence)
        {
            tts?.Enqueue(sentence, 1f, 0);
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

            // 3. 打断 TTS（内部会作废文本队列 + PCM 队列）
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