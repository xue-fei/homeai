using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class Test : MonoBehaviour
{
    public AudioSource audioSource;

    // Start is called before the first frame update
    void Start()
    {
        OnButtonClick();
    }

    public async void OnButtonClick()
    {
        byte[] audioData = await SynthesizeSpeechAsync(
            "Unity 调用成功，正在播放合成语音。",
            Application.dataPath + "/wanwan.wav"
        );
        // 将 byte[] 转为 AudioClip 播放
        AudioClip clip = Util.BytesToAudioClip(audioData);
        audioSource.clip = clip;
        audioSource.Play();
    }

    // Update is called once per frame
    void Update()
    {

    }

    public class TtsRequest
    {
        public string text { get; set; }
        public string audio_prompt { get; set; }   // 注意字段名已改为 audio_prompt
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
            speaker_wav_base64 = base64   // 字段名匹配服务端要求
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