namespace Server
{
    /// <summary>
    /// 流式重采样器（自实现，不依赖任何第三方编解码库）
    ///
    /// 用途：TTS 模型原生采样率与链路约定的 16000Hz 不一致时做转换。
    /// 算法：窗函数 sinc 插值（Hann 窗），降采样时按比例收缩截止频率做抗混叠，
    ///       避免直接线性插值导致的高频折叠噪声（听起来像沙沙的金属噪音）。
    ///
    /// 支持连续调用：内部保留 taps 长度的历史采样，块与块之间不会出现接缝爆音。
    /// </summary>
    public class PcmResampler
    {
        private readonly int inRate;
        private readonly int outRate;
        private readonly double ratio;        // inRate / outRate，每输出一个点在输入上前进的距离
        private readonly int halfTaps;
        private readonly double cutoff;       // 归一化截止频率（相对输入采样率）

        private float[] history;              // 尾部保留的输入样本
        private double fracPos;               // 下一个输出点在（history + 新数据）中的位置

        public PcmResampler(int inRate, int outRate, int halfTaps = 16)
        {
            if (inRate <= 0 || outRate <= 0)
            {
                throw new ArgumentException("采样率必须为正数");
            }
            this.inRate = inRate;
            this.outRate = outRate;
            this.ratio = (double)inRate / outRate;
            this.halfTaps = halfTaps;
            // 降采样时收缩截止频率抗混叠；升采样保持 0.5（奈奎斯特）
            this.cutoff = outRate < inRate ? 0.5 * outRate / inRate : 0.5;
            this.history = new float[halfTaps * 2];
            this.fracPos = halfTaps;
        }

        public bool NeedsResample => inRate != outRate;

        /// <summary>
        /// 处理一块输入采样，返回重采样后的输出。可连续调用。
        /// </summary>
        public float[] Process(float[] input, int length)
        {
            if (!NeedsResample)
            {
                float[] passthrough = new float[length];
                Array.Copy(input, passthrough, length);
                return passthrough;
            }

            // work = 历史 + 本次输入
            int histLen = history.Length;
            float[] work = new float[histLen + length];
            Array.Copy(history, 0, work, 0, histLen);
            Array.Copy(input, 0, work, histLen, length);

            // 可安全输出的最大位置：需要右侧留够 halfTaps 个样本
            double limit = work.Length - halfTaps - 1;
            var output = new List<float>((int)(length / ratio) + 4);

            double pos = fracPos;
            while (pos <= limit)
            {
                output.Add(Interpolate(work, pos));
                pos += ratio;
            }

            // 保留尾部 2*halfTaps 个样本作为下次的历史
            int keep = halfTaps * 2;
            if (work.Length < keep) keep = work.Length;
            float[] newHistory = new float[keep];
            Array.Copy(work, work.Length - keep, newHistory, 0, keep);

            // 位置换算到新历史坐标系
            fracPos = pos - (work.Length - keep);
            if (fracPos < 0) fracPos = 0;
            history = newHistory;

            return output.ToArray();
        }

        private float Interpolate(float[] data, double pos)
        {
            int center = (int)Math.Floor(pos);
            double sum = 0;
            double norm = 0;

            for (int i = center - halfTaps + 1; i <= center + halfTaps; i++)
            {
                if (i < 0 || i >= data.Length) continue;

                double x = pos - i;
                double w = Sinc(2.0 * cutoff * x) * Hann(x);
                sum += data[i] * w;
                norm += w;
            }

            if (norm > 1e-9)
            {
                sum /= norm;   // 归一化，避免整体音量随相位起伏
            }

            if (sum > 1.0) sum = 1.0;
            else if (sum < -1.0) sum = -1.0;
            return (float)sum;
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-9) return 1.0;
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private double Hann(double x)
        {
            double a = Math.Abs(x) / halfTaps;
            if (a >= 1.0) return 0.0;
            return 0.5 * (1.0 + Math.Cos(Math.PI * a));
        }

        public void Reset()
        {
            Array.Clear(history, 0, history.Length);
            fracPos = halfTaps;
        }
    }
}
