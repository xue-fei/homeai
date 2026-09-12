using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.Tts;

namespace Server
{
    /// <summary>
    /// LongCat 云端大模型后端（OpenAI 兼容格式）。
    ///
    /// 接入端点  : https://api.longcat.chat/openai
    /// 模型      : LongCat-2.0（上下文 1M，输出最长 128K）
    /// 鉴权      : Authorization: Bearer &lt;APP_KEY&gt;
    ///
    /// 采用 HttpClient + 手写 SSE 流式解析，不引入 OpenAI SDK，避免额外依赖。
    /// 流式返回 data: {"choices":[{"delta":{"content":"..."}}]} 逐 token 推送，
    /// 与本地 Ollama 的 Llm 一样：按句切分，边生成边送 TTS，首字延迟最小。
    /// </summary>
    public class LlmLongCat : ILlm
    {
        public TtsMatchaIcefall tts { get; set; }

        private const string Endpoint = "https://api.longcat.chat/openai/v1/chat/completions";
        private const string Model = "LongCat-2.0";

        private readonly HttpClient http;
        private readonly string apiKey;
        private readonly string sysTip;

        // 对话历史（OpenAI 格式：role + content）
        private readonly List<ChatMsg> chatHistory = new();

        // 实时句子缓冲区（与 Llm 一致的逐句切分逻辑）
        private readonly StringBuilder sentenceBuffer = new();
        private static readonly Regex SentenceDelimiters =
            new(@"[。！？.!?](\s|$)|[。！？.!?][""'](\s|$)", RegexOptions.Compiled);

        private volatile CancellationTokenSource cts;

        public LlmLongCat(string apiKey, string sysTip = "")
        {
            this.apiKey = apiKey;
            this.sysTip = sysTip;

            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(600);   // 长文本流式，超时给足

            if (!string.IsNullOrEmpty(sysTip))
            {
                chatHistory.Add(new ChatMsg("system", sysTip));
            }
        }

        public async void RequestAsync(string prompt)
        {
            chatHistory.Add(new ChatMsg("user", prompt));

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            var body = new
            {
                model = Model,
                messages = chatHistory,
                stream = true,
                max_tokens = 1024,
                temperature = 0.7f
            };

            try
            {
                Console.WriteLine("[LongCat] 发起请求:" + prompt);

                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8, "application/json");

                using var resp = await http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, token);

                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync(token);
                    Console.WriteLine($"[LongCat] 请求失败 HTTP {(int)resp.StatusCode}: {err}");
                    // 失败时兜底播一句提示
                    tts?.Enqueue("网络有点问题，没连上云端大模型。", 1f, 0);
                    return;
                }

                var fullResponse = new StringBuilder();

                using var stream = await resp.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    // SSE 格式：data: {...}，空行分隔事件
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data:")) continue;

                    string payload = line.Substring(5).Trim();
                    if (payload == "[DONE]") break;
                    if (string.IsNullOrEmpty(payload)) continue;

                    string content = ExtractDeltaContent(payload);
                    if (string.IsNullOrEmpty(content)) continue;

                    sentenceBuffer.Append(content);
                    fullResponse.Append(content);
                    ProcessBuffer();
                }

                // 残留内容（无句尾标点）直接入队
                string remaining = sentenceBuffer.ToString().Trim();
                sentenceBuffer.Clear();
                if (!string.IsNullOrEmpty(remaining))
                {
                    Console.WriteLine("[LongCat] 刷新残留句子: " + remaining);
                    tts?.Enqueue(remaining, 1f, 0);
                }

                chatHistory.Add(new ChatMsg("assistant", fullResponse.ToString()));
            }
            catch (OperationCanceledException)
            {
                sentenceBuffer.Clear();
                Console.WriteLine("[LongCat] 已中断");
            }
            catch (Exception ex)
            {
                sentenceBuffer.Clear();
                Console.WriteLine($"[LongCat] 请求出错: {ex.Message}");
            }
        }

        /// <summary>从一行 SSE 里提取 delta.content</summary>
        private static string ExtractDeltaContent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
                if (choices.GetArrayLength() == 0) return null;

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) return null;
                if (!delta.TryGetProperty("content", out var content)) return null;

                return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        private void ProcessBuffer()
        {
            var content = sentenceBuffer.ToString();
            int lastIndex = 0;

            foreach (Match match in SentenceDelimiters.Matches(content))
            {
                int endPos = match.Index + match.Length;
                string sentence = content.Substring(lastIndex, endPos - lastIndex).Trim();

                if (!string.IsNullOrEmpty(sentence))
                {
                    Console.WriteLine("[LongCat] 模型回答: " + sentence);
                    tts?.Enqueue(sentence, 1f, 0);
                }

                lastIndex = endPos;
            }

            sentenceBuffer.Length = 0;
            sentenceBuffer.Append(content.Substring(lastIndex));
        }

        public void Interrupt()
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                cts = null;
            }

            sentenceBuffer.Clear();
            tts?.Interrupt();

            Console.WriteLine("[LongCat] 已打断");
        }

        public void ClearHistory()
        {
            chatHistory.Clear();
            if (!string.IsNullOrEmpty(sysTip))
            {
                chatHistory.Add(new ChatMsg("system", sysTip));
            }
        }

        public void Stop()
        {
            Interrupt();
            http?.Dispose();
        }

        /// <summary>OpenAI 兼容的消息结构</summary>
        private sealed class ChatMsg
        {
            public string role { get; set; }
            public string content { get; set; }

            public ChatMsg(string role, string content)
            {
                this.role = role;
                this.content = content;
            }
        }
    }
}
