using Fleck;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Text;

namespace Server.Tts
{
    public class TtsIndex
    {
        public IWebSocketConnection client = null;
        private ConcurrentQueue<byte> sendQueue = new();

        public async Task Generate(string text, float speed, int speakerId)
        {
            byte[] audioData = await SynthesizeSpeechAsync( text,
            Environment.CurrentDirectory + "/wanwan.wav");
        }

        public void Interrupt()
        {

        }

        public class TtsRequest
        {
            public string text { get; set; }
            public string audio_prompt { get; set; }
            public string speaker_wav_base64 { get; set; }
            public float speed { get; set; } = 1.0f;
        }

        public async Task<byte[]> SynthesizeSpeechAsync(string text, string speakerWavPath)
        {
            byte[] audioBytes = File.ReadAllBytes(speakerWavPath);
            string base64 = Convert.ToBase64String(audioBytes);

            var request = new TtsRequest
            {
                text = text,
                audio_prompt = "",
                speaker_wav_base64 = base64
            };
            var json = JsonConvert.SerializeObject(request);
            using var client = new HttpClient();
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("http://localhost:6006/tts", content);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"TTS failed: {response.StatusCode}, {error}");
            }
        }
    }
}