using System.IO; 

public static class Util
{
    public static void SaveClip(int channels, int frequency, float[] data, string filePath)
    {
        using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
        {
            using (BinaryWriter writer = new BinaryWriter(fileStream))
            {
                // 写入RIFF头部标识
                writer.Write("RIFF".ToCharArray());
                // 写入文件总长度（后续填充）
                writer.Write(0);
                writer.Write("WAVE".ToCharArray());
                // 写入fmt子块
                writer.Write("fmt ".ToCharArray());
                writer.Write(16); // PCM格式块长度
                writer.Write((short)1); // PCM编码类型
                writer.Write((short)channels);
                writer.Write(frequency);
                writer.Write(frequency * channels * 2); // 字节率
                writer.Write((short)(channels * 2)); // 块对齐
                writer.Write((short)16); // 位深度
                                         // 写入data子块
                writer.Write("data".ToCharArray());
                writer.Write(data.Length * 2); // 音频数据字节数
                                               // 写入PCM数据（float转为short）
                foreach (float sample in data)
                {
                    writer.Write((short)(sample * 32767));
                }
                // 返回填充文件总长度
                fileStream.Position = 4;
                writer.Write((int)(fileStream.Length - 8));
            }
        }
    }

    /// <summary>
    /// 将 16-bit PCM 字节数组（小端序）转换为 float 数组（范围 -1f ~ 1f）
    /// </summary>
    /// <param name="pcmBytes">PCM 原始字节数组，每个采样占 2 字节</param>
    /// <returns>float 数组，采样值范围 -1.0f 到 1.0f</returns>
    public static float[] ConvertPCMBytesToFloat(byte[] pcmBytes)
    {
        int sampleCount = pcmBytes.Length / 2;
        float[] floatData = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // 小端序读取：低字节在前，高字节在后
            short pcmValue = (short)((pcmBytes[i * 2 + 1] << 8) | pcmBytes[i * 2]);
            // 转换为 float：范围 -32768 ~ 32767 → -1.0f ~ 1.0f
            floatData[i] = pcmValue / 32768f;
        }

        return floatData;
    }
}