using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

namespace server
{
    public class TtsVolcengine
    {
        private const string ApiUrl = "https://openspeech.bytedance.com/api/v1/tts";
        private readonly string _appId;
        private readonly string _accessToken;
        private readonly string _audioPath;
        private readonly HttpClient _httpClient = new HttpClient();

        public TtsVolcengine(string appId, string accessToken, string outputDirectory = "audio_output")
        {
            _appId = appId;
            _accessToken = accessToken;
            _audioPath = Path.Combine(Directory.GetCurrentDirectory(), outputDirectory);

            if (!Directory.Exists(_audioPath))
            {
                Directory.CreateDirectory(_audioPath);
            }
        }

        public async Task<AudioResult> SpeakAsync(string text)
        {
            var ttsRequest = new TtsRequest
            {
                app = new App
                {
                    appid = _appId,
                    token = _accessToken,
                    cluster = "volcano_tts"
                },
                user = new User
                {
                    uid = "yincheng"
                },
                audio = new Audio
                {
                    voice_type = "zh_female_zhixingnvsheng_mars_bigtts",
                    encoding = "wav",
                    speed_ratio = 1.0f,
                    rate = 16000
                },
                request = new Request
                {
                    reqid = Guid.NewGuid().ToString(),
                    text = text,
                    operation = "query"
                }
            };

            string json = JsonConvert.SerializeObject(ttsRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.PostAsync(ApiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"TTS request failed: {response.StatusCode} - {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var ttsResponse = JsonConvert.DeserializeObject<TtsResponse>(responseContent);

            if (ttsResponse == null || string.IsNullOrEmpty(ttsResponse.data))
            {
                throw new Exception("Invalid TTS response");
            }

            byte[] pcmData = Convert.FromBase64String(ttsResponse.data);
            float[] floatData = BytesToFloat(pcmData);
            string filePath = SaveAsWav(pcmData);

            return new AudioResult
            {
                PcmData = pcmData,
                FloatData = floatData,
                FilePath = filePath,
                Text = text
            };
        }

        private float[] BytesToFloat(byte[] byteArray)
        {
            float[] soundData = new float[byteArray.Length / 2];
            for (int i = 0; i < soundData.Length; i++)
            {
                soundData[i] = BytesToFloat(byteArray[i * 2], byteArray[i * 2 + 1]);
            }
            return soundData;
        }

        private float BytesToFloat(byte firstByte, byte secondByte)
        {
            short s = BitConverter.IsLittleEndian
                ? (short)((secondByte << 8) | firstByte)
                : (short)((firstByte << 8) | secondByte);
            return s / 32768.0F;
        }

        private string SaveAsWav(byte[] pcmData)
        {
            var filePath = Path.Combine(_audioPath, $"{DateTime.Now:yyyyMMdd_HHmmssfff}.wav");

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                // WAV文件头
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(0); // 文件大小占位符
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt 块
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // fmt块大小
                writer.Write((short)1); // PCM格式
                writer.Write((short)1); // 单声道
                writer.Write(16000); // 采样率
                writer.Write(16000 * 2); // 字节率
                writer.Write((short)2); // 块对齐
                writer.Write((short)16); // 位深

                // data 块
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(pcmData.Length); // 数据大小

                // 写入PCM数据
                writer.Write(pcmData);

                // 更新文件大小
                fileStream.Position = 4;
                writer.Write((int)fileStream.Length - 8);
            }

            return filePath;
        }
    }

    public class AudioResult
    {
        public byte[] PcmData { get; set; }
        public float[] FloatData { get; set; }
        public string FilePath { get; set; }
        public string Text { get; set; }
    }

    // 数据模型保持不变
    public class TtsResponse
    {
        public string reqid { get; set; }
        public string code { get; set; }
        public string operation { get; set; }
        public string message { get; set; }
        public int sequence { get; set; }
        public string data { get; set; }
        public Addition addition { get; set; }
    }

    public class Addition
    {
        public string duration { get; set; }
    }

    public class TtsRequest
    {
        public App app { get; set; }
        public User user { get; set; }
        public Audio audio { get; set; }
        public Request request { get; set; }
    }

    public class App
    {
        public string appid { get; set; }
        public string token { get; set; }
        public string cluster { get; set; }
    }

    public class User
    {
        public string uid { get; set; }
    }

    public class Audio
    {
        public string voice_type { get; set; }
        public string encoding { get; set; }
        public float speed_ratio { get; set; }
        public int rate { get; set; }
    }

    public class Request
    {
        public string reqid { get; set; }
        public string text { get; set; }
        public string operation { get; set; }
    }
}
