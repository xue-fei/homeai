
namespace server
{
    using System.IO;
    using System.Text;

    public class WavUtility
    {
        public static byte[] AddWavHeader(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                // RIFF 头
                writer.Write(Encoding.Default.GetBytes("RIFF"));
                int riffChunkSize = 36 + pcmData.Length; // 36 = 4 (WAVE) + 24 (fmt) + 8 (data头)
                writer.Write(riffChunkSize);
                writer.Write(Encoding.Default.GetBytes("WAVE"));

                // fmt 子块
                writer.Write(Encoding.Default.GetBytes("fmt "));
                writer.Write(16); // fmt块大小（16字节）
                writer.Write((short)1); // 音频格式（PCM）
                writer.Write((short)channels); // 声道数
                writer.Write(sampleRate); // 采样率
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                writer.Write(byteRate); // 字节率
                short blockAlign = (short)(channels * bitsPerSample / 8);
                writer.Write(blockAlign); // 块对齐
                writer.Write((short)bitsPerSample); // 位深度

                // data 子块
                writer.Write(Encoding.Default.GetBytes("data"));
                writer.Write(pcmData.Length); // 数据大小
                writer.Write(pcmData); // PCM数据

                return stream.ToArray();
            }
        }

        public static float[] ReadMono16kWavToFloat(string filePath)
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new BinaryReader(fs);

            string riff = new string(reader.ReadChars(4));
            int fileSize = reader.ReadInt32();
            string wave = new string(reader.ReadChars(4));
            string fmt = new string(reader.ReadChars(4));
            int fmtSize = reader.ReadInt32();

            short audioFormat = reader.ReadInt16();
            short numChannels = reader.ReadInt16();
            int fileSampleRate = reader.ReadInt32();
            int byteRate = reader.ReadInt32();
            short blockAlign = reader.ReadInt16();
            short bitsPerSample = reader.ReadInt16();

            if (riff != "RIFF" || wave != "WAVE" || fmt != "fmt ")
                throw new Exception("无效的WAV文件头");
            if (fmtSize > 16)
                reader.ReadBytes(fmtSize - 16);

            string dataChunkId;
            do
            {
                dataChunkId = new string(reader.ReadChars(4));
                if (dataChunkId != "data")
                    reader.ReadBytes(reader.ReadInt32());
            } while (dataChunkId != "data");

            int dataSize = reader.ReadInt32();

            if (audioFormat != 1) throw new Exception("仅支持PCM格式");
            if (numChannels != 1) throw new Exception("仅支持单声道音频");
            if (fileSampleRate != 16000) throw new Exception("仅支持16kHz采样率");
            if (bitsPerSample != 16) throw new Exception("仅支持16位采样深度");

            int sampleCount = dataSize / 2;
            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                byte lo = reader.ReadByte();
                byte hi = reader.ReadByte();
                short pcm = (short)((hi << 8) | lo);
                floatData[i] = pcm / 32768.0f;
            }

            return floatData;
        }

        public static float[] ReadMono24kWavToFloat(string filePath)
        {
            using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new BinaryReader(fs);

            string riff = new string(reader.ReadChars(4));
            int fileSize = reader.ReadInt32();
            string wave = new string(reader.ReadChars(4));
            string fmt = new string(reader.ReadChars(4));
            int fmtSize = reader.ReadInt32();

            short audioFormat = reader.ReadInt16();
            short numChannels = reader.ReadInt16();
            int fileSampleRate = reader.ReadInt32();
            int byteRate = reader.ReadInt32();
            short blockAlign = reader.ReadInt16();
            short bitsPerSample = reader.ReadInt16();

            if (riff != "RIFF" || wave != "WAVE" || fmt != "fmt ")
                throw new Exception("无效的WAV文件头");
            if (fmtSize > 16)
                reader.ReadBytes(fmtSize - 16);

            string dataChunkId;
            do
            {
                dataChunkId = new string(reader.ReadChars(4));
                if (dataChunkId != "data")
                    reader.ReadBytes(reader.ReadInt32());
            } while (dataChunkId != "data");

            int dataSize = reader.ReadInt32();

            // 修改点1：允许采样率为 24000
            if (audioFormat != 1) throw new Exception("仅支持PCM格式");
            if (numChannels != 1) throw new Exception("仅支持单声道音频");
            if (fileSampleRate != 24000) throw new Exception("仅支持24kHz采样率");
            if (bitsPerSample != 16) throw new Exception("仅支持16位采样深度");

            int sampleCount = dataSize / 2;
            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                byte lo = reader.ReadByte();
                byte hi = reader.ReadByte();
                short pcm = (short)((hi << 8) | lo);
                floatData[i] = pcm / 32768.0f;
            }

            return floatData;
        }
    }
}