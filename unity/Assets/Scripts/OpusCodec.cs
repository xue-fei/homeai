using System;
using Concentus.Structs;
using Concentus.Enums;

// 抑制 Concentus 过时 API 警告（Unity 端使用旧版本库，经典数组 API 仍可用）
#pragma warning disable CS0618

/// <summary>
/// Opus 编解码器封装类 - Unity 版本
/// 需要引用 Concentus.dll（通过 NuGet 安装 Concentus 包）
/// </summary>
public class OpusCodec
{
    private OpusEncoder encoder;
    private OpusDecoder decoder;
    private int sampleRate;
    private int channels;
    private int frameSize; // 每帧采样数 (20ms)

    public OpusCodec(int sampleRate = 16000, int channels = 1, int bitrate = 24000)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        this.frameSize = sampleRate / 50; // 20ms 帧 = 采样率/50

            // 创建 Opus 编码器
            encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = bitrate;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            encoder.Complexity = 10;

            // 创建 Opus 解码器
            decoder = new OpusDecoder(sampleRate, channels);
    }

    /// <summary>
    /// 将 16-bit PCM 字节数组编码为 Opus 格式（只编码第一帧）
    /// </summary>
    public byte[] Encode(byte[] pcmBytes)
    {
        if (pcmBytes == null || pcmBytes.Length < frameSize * 2)
            return null;

        int validLength = (pcmBytes.Length / (frameSize * 2)) * (frameSize * 2);
        if (validLength == 0)
            return null;

        short[] pcmShorts = new short[validLength / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcmShorts, 0, validLength);

        byte[] opusBuffer = new byte[1275];
        int encodedBytes = encoder.Encode(pcmShorts, 0, frameSize, opusBuffer, 0, opusBuffer.Length);

        if (encodedBytes <= 0)
            return null;

        byte[] result = new byte[encodedBytes];
        Buffer.BlockCopy(opusBuffer, 0, result, 0, encodedBytes);
        return result;
    }

    /// <summary>
    /// 将 16-bit PCM short数组编码为 Opus 格式
    /// </summary>
    public byte[] Encode(short[] pcmShorts, int samplesPerChannel)
    {
        if (pcmShorts == null || samplesPerChannel < frameSize)
            return null;

        byte[] opusBuffer = new byte[1275];
        int encodedBytes = encoder.Encode(pcmShorts, 0, frameSize, opusBuffer, 0, opusBuffer.Length);

        if (encodedBytes <= 0)
            return null;

        byte[] result = new byte[encodedBytes];
        Buffer.BlockCopy(opusBuffer, 0, result, 0, encodedBytes);
        return result;
    }

    /// <summary>
    /// 将 Opus 数据解码为 16-bit PCM short数组
    /// </summary>
    public short[] Decode(byte[] opusBytes)
    {
        if (opusBytes == null || opusBytes.Length == 0)
            return null;

        short[] pcmBuffer = new short[frameSize * 6];
        int decodedSamples = decoder.Decode(opusBytes, 0, opusBytes.Length, pcmBuffer, 0, frameSize, false);

        if (decodedSamples <= 0)
            return null;

        short[] result = new short[decodedSamples];
        Buffer.BlockCopy(pcmBuffer, 0, result, 0, decodedSamples * 2);
        return result;
    }

    /// <summary>
    /// 将 Opus 数据解码为 16-bit PCM 字节数组（小端序）
    /// </summary>
    public byte[] DecodeToBytes(byte[] opusBytes)
    {
        short[] pcmShorts = Decode(opusBytes);
        if (pcmShorts == null)
            return null;

        byte[] result = new byte[pcmShorts.Length * 2];
        Buffer.BlockCopy(pcmShorts, 0, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// 将 Opus 数据解码为 float 数组（范围 -1.0 ~ 1.0）
    /// </summary>
    public float[] DecodeToFloat(byte[] opusBytes)
    {
        short[] pcmShorts = Decode(opusBytes);
        if (pcmShorts == null)
            return null;

        float[] result = new float[pcmShorts.Length];
        for (int i = 0; i < pcmShorts.Length; i++)
        {
            result[i] = pcmShorts[i] / 32768.0f;
        }
        return result;
    }

    public void Dispose()
    {
        if (encoder != null) { encoder.Dispose(); encoder = null; }
        if (decoder != null) { decoder.Dispose(); decoder = null; }
    }
}

#pragma warning restore CS0618
