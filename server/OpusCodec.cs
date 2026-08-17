using Concentus.Structs;
using Concentus.Enums;

namespace Server
{
    /// <summary>
    /// Opus 编解码器封装类
    /// 用于将 PCM 音频数据编码为 Opus 格式，以及将 Opus 解码为 PCM
    /// 
    /// 注意：为兼容 esp32_opus (libopus) 和 Concentus 版本差异，
    /// 解码时使用最大帧长（60ms）让解码器自行判断，避免 "buffer too small" 错误
    /// </summary>
    public class OpusCodec
    {
        private OpusEncoder encoder;
        private OpusDecoder decoder;
        private int sampleRate;
        private int channels;
        private int frameSize; // 每帧采样数 (20ms) - 编码用
        
        // 解码时使用最大允许帧长，兼容不同 libopus 版本产生的帧长差异
        // Opus 支持 2.5/5/10/20/40/60ms 帧，60ms @ 16kHz = 960 samples
        private int maxDecodeFrameSize;

        public OpusCodec(int sampleRate = 16000, int channels = 1, int bitrate = 16000)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.frameSize = sampleRate / 50; // 20ms 帧 = 采样率/50 (编码用 320 @ 16kHz)
            this.maxDecodeFrameSize = (sampleRate * 60) / 1000; // 60ms 最大帧长 (960 @ 16kHz)

            // 创建 Opus 编码器
            encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = bitrate;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            encoder.Complexity = 10;

            // 创建 Opus 解码器
            decoder = new OpusDecoder(sampleRate, channels);
        }

        /// <summary>
        /// 将 16-bit PCM 字节数组编码为 Opus 格式
        /// </summary>
        /// <param name="pcmBytes">16-bit PCM 字节数组（小端序）</param>
        /// <returns>编码后的 Opus 字节数组，如果数据不足一帧则返回 null</returns>
        public byte[] Encode(byte[] pcmBytes)
        {
            if (pcmBytes == null || pcmBytes.Length < frameSize * 2)
                return null;

            // 确保数据长度是帧大小的整数倍
            int validLength = (pcmBytes.Length / (frameSize * 2)) * (frameSize * 2);
            if (validLength == 0)
                return null;

            short[] pcmShorts = new short[validLength / 2];
            Buffer.BlockCopy(pcmBytes, 0, pcmShorts, 0, validLength);

            // 只取第一帧编码
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
        /// 
        /// 关键修复：使用最大帧长 (60ms) 调用 Decode，让解码器根据包内容自行判断实际帧长。
        /// 如果传入 frame_size=320 (20ms) 但实际包是 40/60ms 帧，会报 "buffer too small"。
        /// Concentus 内部使用 opus_decode()，返回实际解码的采样数。
        /// </summary>
        public short[] Decode(byte[] opusBytes)
        {
            if (opusBytes == null || opusBytes.Length == 0)
                return null;

            short[] pcmBuffer = new short[maxDecodeFrameSize];
            int decodedSamples = decoder.Decode(opusBytes, 0, opusBytes.Length, pcmBuffer, 0, maxDecodeFrameSize, false);

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
            encoder?.Dispose();
            decoder?.Dispose();
        }
    }
}
