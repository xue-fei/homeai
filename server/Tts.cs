using SherpaOnnx; 
using System.Runtime.InteropServices; 

namespace server
{
    public class Tts
    {
        OfflineTts ot;
        OfflineTtsGeneratedAudio otga;
        OfflineTtsConfig config;
        OfflineTtsCallback otc;
        bool initDone = false;
        int SampleRate = 22050;
        string modelPath;
        List<float> audioData = new List<float>();
        float audioLength = 0f;

        public Tts()
        {
            modelPath = Environment.CurrentDirectory + "/vits-melo-tts-zh_en";
            config = new OfflineTtsConfig();
            config.Model.Vits.Model = Path.Combine(modelPath, "vits-melo-tts-zh_en/model.onnx");
            config.Model.Vits.Lexicon = Path.Combine(modelPath, "vits-melo-tts-zh_en/lexicon.txt");
            config.Model.Vits.Tokens = Path.Combine(modelPath, "vits-melo-tts-zh_en/tokens.txt");
            config.Model.Vits.DictDir = Path.Combine(modelPath, "vits-melo-tts-zh_en/dict");
            config.Model.Vits.NoiseScale = 0.667f;
            config.Model.Vits.NoiseScaleW = 0.8f;
            config.Model.Vits.LengthScale = 1f;
            config.Model.NumThreads = 5;
            config.Model.Debug = 1;
            config.Model.Provider = "cpu";
            config.RuleFsts = modelPath + "/vits-melo-tts-zh_en/phone.fst" + ","
                        + modelPath + "/vits-melo-tts-zh_en/date.fst" + ","
                    + modelPath + "/vits-melo-tts-zh_en/number.fst";
            config.MaxNumSentences = 1;
            ot = new OfflineTts(config);
            SampleRate = ot.SampleRate;
            otc = new OfflineTtsCallback(OnAudioData);
        }

        public void Generate(string text, float speed, int speakerId)
        {
            if (!initDone)
            {
                Console.WriteLine("文字转语音未完成初始化");
                return;
            }
            otga = ot.GenerateWithCallback(text, speed, speakerId, otc);
        }

        int OnAudioData(IntPtr samples, int n)
        {
            float[] tempData = new float[n];
            Marshal.Copy(samples, tempData, 0, n);
            audioData.AddRange(tempData);
            Console.WriteLine("n:" + n);
            audioLength += (float)n / (float)SampleRate;
            Console.WriteLine("音频长度增加 " + (float)n / (float)SampleRate + "秒");
            return n;
        }

        public void Stop()
        {
            if (ot != null)
            {
                ot.Dispose();
            }
            if (otc != null)
            {
                otc = null;
            }
            if (otga != null)
            {
                otga.Dispose();
            }
        }
    }
}
