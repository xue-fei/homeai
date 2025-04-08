using System.Text;
using System.Text.RegularExpressions;
using OllamaSharp;
using OllamaSharp.Models;

namespace server
{
    public class Llm
    {
        public Tts tts;
        OllamaApiClient ollama;

        public Llm()
        {
            var uri = new Uri("http://localhost:11434");
            ollama = new OllamaApiClient(uri, "qwen2:1.5b");
        }

        // 实时句子缓冲区
        StringBuilder sentenceBuffer = new StringBuilder();
        // 句子结束符正则（支持中英文标点）
        Regex sentenceDelimiters = new Regex(@"[。！？.!?](\s|$)|[。！？.!?][”’](\s|$)");

        public async void RequestAsync(string prompt)
        {
            GenerateRequest gr = new GenerateRequest();
            gr.Prompt = prompt;
            gr.Stream = true;
            var resp = ollama.GenerateAsync(gr);
            await foreach (GenerateResponseStream? stream in resp)
            {
                if (stream != null)
                {
                    //Console.WriteLine("模型回答:" + stream.Response);

                    // 追加新内容到缓冲区
                    sentenceBuffer.Append(stream.Response);

                    // 实时处理缓冲区
                    ProcessBuffer(ref sentenceBuffer);

                    // 如果已结束
                    if (stream.Done)
                    {
                        break;
                        //Console.WriteLine("模型回答:"+stream.Response);
                        //if (tts != null)
                        //{
                        //    tts.Generate(stream.Response, 1f, 0);
                        //}
                    }
                }
            }
        }

        void ProcessBuffer(ref StringBuilder buffer)
        {
            var content = buffer.ToString();
            var lastIndex = 0;

            // 查找所有完整句子
            var matches = sentenceDelimiters.Matches(content);
            foreach (Match match in matches)
            {
                // 截取到标点符号的位置
                var endPos = match.Index + match.Length;
                var sentence = content.Substring(lastIndex, endPos - lastIndex).Trim();

                if (!string.IsNullOrEmpty(sentence))
                {
                    // 触发句子处理（此处示例为控制台输出）
                    Console.WriteLine($"模型回答: {sentence}");
                    if (tts != null)
                    {
                        tts.Generate(sentence, 1f, 0);
                    }
                }

                lastIndex = endPos;
            }

            // 保留未完成部分
            buffer = new StringBuilder(content.Substring(lastIndex));
        }

        public void Stop()
        {
            if (ollama != null)
            {
                ollama.Dispose();
                ollama = null;
            }
        }
    }
}