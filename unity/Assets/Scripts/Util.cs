using System;
using System.IO;
using UnityEngine;

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

    /// <summary>
    /// 将WAV文件的字节数组转换为Unity的AudioClip。
    /// 支持16位PCM格式的单声道和立体声音频。
    /// </summary>
    /// <param name="wavData">WAV文件的字节数组，应该包含完整的WAV文件头。</param>
    /// <returns>生成的AudioClip，如果解析失败则返回null。</returns>
    public static AudioClip BytesToAudioClip(byte[] wavData)
    {
        if (wavData == null || wavData.Length == 0)
        {
            Debug.LogError("WAV数据为空");
            return null;
        }

        // 1. 解析WAV文件头
        int sampleRate = 0;
        int channels = 0;
        int audioDataStart = 0; // 音频数据在字节数组中的起始位置
        int audioDataLength = 0; // 音频数据的字节长度

        try
        {
            // 检查"RIFF"和"WAVE"标识 (前12字节)
            if (!(System.Text.Encoding.ASCII.GetString(wavData, 0, 4) == "RIFF" &&
                  System.Text.Encoding.ASCII.GetString(wavData, 8, 4) == "WAVE"))
            {
                Debug.LogError("非法的WAV文件格式");
                return null;
            }

            // 查找"fmt "块 (小端)
            int offset = 12;
            while (offset + 8 <= wavData.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(wavData, offset, 4);
                int chunkSize = BitConverter.ToInt32(wavData, offset + 4);

                if (chunkId == "fmt ")
                {
                    // 解析关键音频参数 (offset+8是音频格式, offset+10是声道数, 等)
                    int audioFormat = BitConverter.ToInt16(wavData, offset + 8);
                    if (audioFormat != 1) // 1 代表PCM格式
                    {
                        Debug.LogError($"不支持的音频格式: {audioFormat}。仅支持PCM (未压缩) 格式。");
                        return null;
                    }

                    channels = BitConverter.ToInt16(wavData, offset + 10);      // 声道数 (1=单声道, 2=立体声)
                    sampleRate = BitConverter.ToInt32(wavData, offset + 12);    // 采样率 (常见: 16000, 24000)
                }
                else if (chunkId == "data")
                {
                    // 记录数据块的起始位置和长度
                    audioDataStart = offset + 8;
                    audioDataLength = chunkSize;
                    // 找到data块后可以跳出循环，因为我们只需要它了
                    break;
                }

                // 移动到下一个块 (+8是块头大小)
                offset += 8 + chunkSize;
            }

            if (sampleRate == 0 || channels == 0 || audioDataLength == 0)
            {
                Debug.LogError("WAV文件解析失败：未能正确读取文件头信息。");
                return null;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"WAV文件解析异常: {e.Message}");
            return null;
        }

        // 2. 将PCM数据转换为float数组
        float[] audioSamples;
        try
        {
            // 每个采样16位 = 2字节，所以总采样点数是 字节总数 / 2 / 声道数
            int totalSamples = audioDataLength / 2; // 每个采样2字节
            audioSamples = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++)
            {
                // 以小端序方式从字节数组中读取短整型 (Int16)
                // 小端序: 低字节在前，高字节在后
                short pcmValue = (short)((wavData[audioDataStart + i * 2 + 1] << 8) | wavData[audioDataStart + i * 2]);
                // 将有符号的 Int16 (−32768–32767) 转换为 float (−1.0f–1.0f)
                audioSamples[i] = pcmValue / 32768.0f;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"PCM数据转换异常: {e.Message}");
            return null;
        }

        // 3. 创建AudioClip
        // AudioClip.Create(名称, 采样点数, 声道数, 采样率, false是否流式)
        AudioClip clip = AudioClip.Create("GeneratedClip", audioSamples.Length / channels, channels, sampleRate, false);
        clip.SetData(audioSamples, 0);
        return clip;
    }
}