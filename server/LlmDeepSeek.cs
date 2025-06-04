using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace server
{
    public class LlmDeepSeek
    {
        private const string API_URL = "https://api.deepseek.com/v1/chat/completions";
        private const string API_KEY = "your_api_key_here"; // 替换为你的实际API密钥
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly Regex _sentenceDelimiters = new Regex(@"[。！？.!?](\s|$)|[。！？.!?][”’](\s|$)", RegexOptions.Compiled);
        private readonly StringBuilder _sentenceBuffer = new StringBuilder();

        public async Task RequestAsync(string userMessage)
        {
            await SendChatRequest(userMessage);
        }

        private async Task SendChatRequest(string userMessage)
        {
            // 构建请求体
            var requestData = new ChatRequest
            {
                model = "deepseek-chat",
                messages = new List<Message> { new Message { role = "user", content = userMessage } },
                stream = true
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, API_URL);
            request.Headers.Add("Authorization", $"Bearer {API_KEY}");
            request.Headers.Add("Accept", "text/event-stream");

            var json = JsonConvert.SerializeObject(requestData);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

                var eventData = line.Substring(6).Trim();
                if (eventData == "[DONE]") break;

                try
                {
                    var streamResponse = JsonConvert.DeserializeObject<StreamResponse>(eventData);
                    var content = streamResponse?.choices?[0].delta?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        HandleStreamData(content);
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"JSON解析错误: {ex.Message}");
                }
            }
        }

        private void HandleStreamData(string content)
        {
            Console.Write(content); // 实时输出每个字符
            _sentenceBuffer.Append(content);
            ProcessBuffer();
        }

        private void ProcessBuffer()
        {
            var content = _sentenceBuffer.ToString();
            var lastIndex = 0;

            // 查找所有完整句子
            var matches = _sentenceDelimiters.Matches(content);
            foreach (Match match in matches)
            {
                var endPos = match.Index + match.Length;
                var sentence = content.Substring(lastIndex, endPos - lastIndex).Trim();

                if (!string.IsNullOrEmpty(sentence))
                {
                    Console.WriteLine($"\n完整句子: {sentence}");
                    // 在实际应用中调用TTS引擎
                    // CallTts(sentence); 
                }
                lastIndex = endPos;
            }

            // 保留未完成部分
            _sentenceBuffer.Clear();
            if (lastIndex < content.Length)
            {
                _sentenceBuffer.Append(content.Substring(lastIndex));
            }
        }
    }

    public class Message
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    public class ChatRequest
    {
        public string model { get; set; }
        public List<Message> messages { get; set; }
        public bool stream { get; set; } = true;
        public float temperature { get; set; } = 0.5f;
        public int max_tokens { get; set; } = 2048;
    }

    public class Delta
    {
        public string content { get; set; }
    }

    public class StreamChoice
    {
        public Delta delta { get; set; }
    }

    public class StreamResponse
    {
        public List<StreamChoice> choices { get; set; }
    }
}