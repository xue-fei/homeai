
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
    }
}