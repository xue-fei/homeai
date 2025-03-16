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

        public async void RequestAsync(string prompt)
        {
            GenerateRequest gr = new GenerateRequest();
            gr.Prompt = prompt;
            gr.Stream = false;
            var resp = ollama.GenerateAsync(gr);
            await foreach (GenerateResponseStream? stream in resp)
            {
                if (stream != null)
                {
                    // 如果已结束
                    if (stream.Done)
                    {
                        Console.WriteLine("模型回答:"+stream.Response);
                        if (tts != null)
                        {
                            tts.Generate(stream.Response, 1f, 0);
                        }
                    }
                }
            }
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