namespace Server
{
    /// <summary>
    /// 流式 WAV 读取器（16000Hz / 16bit / 单声道）
    ///
    /// 为什么要流式：一首 4 分钟的歌是 7.7MB PCM，整曲读进内存再播没必要；
    /// 而且播放本身就是实时消费，边读边发才是自然的形态。
    ///
    /// 只认链路约定的那一套格式（16k/mono/16bit PCM），不做任何转换 ——
    /// 格式不对就直接报错，让问题暴露在加载阶段而不是变成噪音。
    /// </summary>
    public class WavStreamReader : IDisposable
    {
        private FileStream fs;
        private long dataStart;          // data 块内容的起始文件偏移
        private long dataLength;         // data 块字节数
        private long dataRead;           // 已读字节数

        public string FilePath { get; }
        public int SampleRate { get; private set; }
        public double DurationSeconds => dataLength / 32000.0;   // 16000 * 2 字节
        public bool EndOfStream => dataRead >= dataLength;

        public WavStreamReader(string filePath)
        {
            FilePath = filePath;
            fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            ParseHeader();
        }

        private void ParseHeader()
        {
            using var reader = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true);

            if (new string(reader.ReadChars(4)) != "RIFF") throw new Exception("不是 RIFF 文件");
            reader.ReadInt32();                                  // RIFF 块大小，忽略
            if (new string(reader.ReadChars(4)) != "WAVE") throw new Exception("不是 WAVE 文件");

            short audioFormat = 0, numChannels = 0, bitsPerSample = 0;
            int sampleRate = 0;
            bool fmtSeen = false;

            // 逐块遍历。WAV 里 fmt/data 之间可能夹着 LIST/fact 等块，
            // 固定偏移读取是常见的踩坑点，这里老老实实按块长度跳。
            while (fs.Position < fs.Length - 8)
            {
                string chunkId = new string(reader.ReadChars(4));
                int chunkSize = reader.ReadInt32();

                if (chunkId == "fmt ")
                {
                    audioFormat = reader.ReadInt16();
                    numChannels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();                          // byteRate
                    reader.ReadInt16();                          // blockAlign
                    bitsPerSample = reader.ReadInt16();
                    int consumed = 16;
                    if (chunkSize > consumed) reader.ReadBytes(chunkSize - consumed);
                    fmtSeen = true;
                }
                else if (chunkId == "data")
                {
                    dataStart = fs.Position;
                    // 有些文件 data 块长度字段写的是 0 或超长，用实际文件长度兜底
                    long actual = fs.Length - dataStart;
                    dataLength = (chunkSize > 0 && chunkSize <= actual) ? chunkSize : actual;
                    break;
                }
                else
                {
                    if (chunkSize < 0) throw new Exception("WAV 块长度非法");
                    reader.ReadBytes(chunkSize + (chunkSize % 2));   // 奇数长度块有 1 字节填充
                }
            }

            if (!fmtSeen) throw new Exception("缺少 fmt 块");
            if (dataStart == 0) throw new Exception("缺少 data 块");
            if (audioFormat != 1) throw new Exception($"仅支持未压缩 PCM（当前 format={audioFormat}）");
            if (numChannels != 1) throw new Exception($"仅支持单声道（当前 {numChannels} 声道）");
            if (sampleRate != 16000) throw new Exception($"仅支持 16000Hz（当前 {sampleRate}Hz）");
            if (bitsPerSample != 16) throw new Exception($"仅支持 16bit（当前 {bitsPerSample}bit）");

            SampleRate = sampleRate;
            dataRead = 0;
            fs.Position = dataStart;
        }

        /// <summary>
        /// 读取原始 PCM 字节。返回实际读到的字节数，0 表示已到结尾。
        /// 保证返回偶数字节，不会把一个 16bit 采样劈成两半。
        /// </summary>
        public int Read(byte[] buffer, int count)
        {
            long remain = dataLength - dataRead;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            count &= ~1;
            if (count <= 0) return 0;

            int got = fs.Read(buffer, 0, count);
            if (got <= 0) return 0;
            got &= ~1;
            dataRead += got;
            return got;
        }

        /// <summary>
        /// 往回退若干字节。用于「已经读出但还堆在发送队列里、尚未真正播出」
        /// 的那部分数据被丢弃时，把读取位置修正回去，避免暂停/打断后跳段。
        /// </summary>
        public void Rewind(long bytes)
        {
            if (bytes <= 0) return;
            if (bytes > dataRead) bytes = dataRead;
            dataRead -= bytes;
            fs.Position = dataStart + dataRead;
        }

        public void Dispose()
        {
            fs?.Dispose();
            fs = null;
        }
    }
}
