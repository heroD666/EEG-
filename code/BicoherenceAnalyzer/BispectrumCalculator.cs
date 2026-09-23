using System.Numerics;
using SeeSharpTools.JY.DSP.Fundamental;
using SeeSharpTools.JY.DSP.TimeSeriesAnalysis;
using SeeSharpTools.JY.Mathematics;

namespace BicoherenceAnalyzer;

/// <summary>
/// 双谱（Bispectrum）和双相干性（Bicoherence）计算器
/// 
/// 参考论文: "Changes in Electroencephalographic Bicoherence During Sevoflurane Anesthesia"
/// 
/// 公式:
///   Bispectrum:  B(f₁, f₂) = E[X(f₁) · X(f₂) · X*(f₁+f₂)]
///   Bicoherence: b²(f₁, f₂) = |B(f₁, f₂)|² / (E[|X(f₁)X(f₂)|²] · E[|X(f₁+f₂)|²])
/// 
/// 其中 X(f) 是信号的傅里叶变换，E[] 表示对所有分段的平均
/// </summary>
public class BispectrumCalculator
{
    /// <summary>采样率 (Hz)</summary>
    public int SampleRate { get; }

    /// <summary>FFT点数（分段长度）</summary>
    public int Nfft { get; private set; }

    /// <summary>重叠点数</summary>
    public int Noverlap { get; private set; }

    /// <summary>最大频率 (Hz)，只计算 f1+f2 ≤ maxFreq 的区域</summary>
    public double MaxFrequency { get; }

    /// <summary>频率分辨率 (Hz)</summary>
    public double FreqResolution => (double)SampleRate / Nfft;

    /// <summary>最大频率对应的索引</summary>
    public int MaxFreqIndex => (int)(MaxFrequency / FreqResolution);

    /// <summary>频率数组（Hz）</summary>
    public double[] Frequencies { get; private set; } = Array.Empty<double>();

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="sampleRate">采样率 (Hz)</param>
    /// <param name="nfft">FFT点数（分段长度），默认512点</param>
    /// <param name="noverlap">重叠点数，默认256点（50%重叠）</param>
    /// <param name="maxFrequency">最大计算频率 (Hz)，默认45 Hz</param>
    public BispectrumCalculator(int sampleRate = 256, int nfft = 512, int noverlap = 256, double maxFrequency = 45.0)
    {
        SampleRate = sampleRate;
        MaxFrequency = maxFrequency;
        SetWindowParams(nfft, noverlap);
    }

    /// <summary>
    /// 运行时切换 FFT 窗长和重叠参数（不影响已计算的结果）
    /// 用于 State 模式（512/256）和 Stimulus 模式（256/128）之间切换
    /// </summary>
    public void SetWindowParams(int nfft, int noverlap)
    {
        Nfft = nfft;
        Noverlap = noverlap;
        RecalculateFrequencies();
    }

    private void RecalculateFrequencies()
    {
        int nFreqBins = Nfft / 2 + 1;
        double[] allFreqs = new double[nFreqBins];
        for (int i = 0; i < nFreqBins; i++)
            allFreqs[i] = i * FreqResolution;

        Frequencies = allFreqs.Take(MaxFreqIndex + 1).ToArray();
    }

    /// <summary>
    /// 计算双谱和双相干性
    /// </summary>
    /// <param name="signal">输入EEG信号（一维数组）</param>
    /// <returns>包含双谱矩阵和双相干性矩阵的结果</returns>
    public BispectrumResult Compute(double[] signal)
    {
        int nFreq = MaxFreqIndex + 1; // 频率轴长度

        if (signal.Length < Nfft)
            throw new ArgumentException($"信号长度 ({signal.Length}) 小于 FFT 长度 ({Nfft})，无法计算");

        // 分段的步长
        int step = Nfft - Noverlap;

        // 计算分段数量
        int numSegments = (signal.Length - Nfft) / step + 1;
        if (numSegments < 2)
            throw new ArgumentException($"信号太短，只有 {numSegments} 个分段（至少需要2个）");

        Console.WriteLine($"[双谱计算] 信号长度: {signal.Length}, 分段数: {numSegments}, 频率分辨率: {FreqResolution:F2} Hz, 频率点数: {nFreq}");

        // === 初始化累加器 ===
        // Bispectrum累加器：复数
        Complex[,] bispectrumAccum = new Complex[nFreq, nFreq];
        // 分母项1：E[|X(f1)X(f2)|²] 累加器
        double[,] powerProductAccum = new double[nFreq, nFreq];
        // 分母项2：E[|X(f1+f2)|²] 累加器（只依赖于和频率）
        double[] powerSumAccum = new double[nFreq]; // 索引对应 f1+f2 的和频率

        // 用于并行计算的锁
        object lockObj = new object();

        // === 逐段处理（并行） ===
        Parallel.For(0, numSegments, segIdx =>
        {
            int startSample = segIdx * step;
            double[] segment = new double[Nfft];
            Array.Copy(signal, startSample, segment, 0, Math.Min(Nfft, signal.Length - startSample));

            // 减去均值（去直流分量）
            double mean = Statistics.Mean(segment);
            for (int i = 0; i < Nfft; i++)
                segment[i] = segment[i] - mean;

            // 计算FFT（SeeSharpTools 内置 Hanning 窗，只需 N/2+1 个复数）
            Complex[] spectrum = new Complex[Nfft / 2 + 1];
            Spectrum.AdvanceComplexFFT(segment, WindowType.Hanning, ref spectrum);

            // 提取所需频率范围（0 到 MaxFreqIndex）
            Complex[] X = new Complex[nFreq];
            double[] magSq = new double[nFreq];
            for (int i = 0; i < nFreq; i++)
            {
                X[i] = spectrum[i];
                magSq[i] = X[i].Real * X[i].Real + X[i].Imaginary * X[i].Imaginary; // |X[i]|²
            }

            // 计算本段的贡献
            Complex[,] localBispectrum = new Complex[nFreq, nFreq];
            double[,] localPowerProduct = new double[nFreq, nFreq];
            double[] localPowerSum = new double[nFreq];

            // 只计算 f1+f2 <= maxFreq 的区域（避免无效循环）
            for (int f1 = 0; f1 < nFreq; f1++)
            {
                for (int f2 = 0; f2 < nFreq; f2++)
                {
                    int fSum = f1 + f2;
                    if (fSum >= nFreq) break; // f1+f2 超出有效频率范围

                    // Bispectrum 累积: X(f1) * X(f2) * conj(X(f1+f2))
                    Complex prod = X[f1] * X[f2];
                    localBispectrum[f1, f2] = prod * Complex.Conjugate(X[fSum]);

                    // 分母项1: |X(f1) * X(f2)|²
                    localPowerProduct[f1, f2] = (prod.Real * prod.Real + prod.Imaginary * prod.Imaginary);

                    // 分母项2: |X(f1+f2)|²
                    localPowerSum[fSum] += magSq[fSum];
                }
            }

            // 线程安全地累加到全局累加器
            lock (lockObj)
            {
                for (int f1 = 0; f1 < nFreq; f1++)
                {
                    for (int f2 = 0; f2 < nFreq; f2++)
                    {
                        if (f1 + f2 >= nFreq) break;
                        bispectrumAccum[f1, f2] += localBispectrum[f1, f2];
                        powerProductAccum[f1, f2] += localPowerProduct[f1, f2];
                    }
                }
                for (int f = 0; f < nFreq; f++)
                    powerSumAccum[f] += localPowerSum[f];
            }
        });

        // === 计算平均值和双相干性 ===
        // Bispectrum: B(f1,f2) = average of segment bispectra
        Complex[,] bispectrum = new Complex[nFreq, nFreq];
        double[,] bicoherence = new double[nFreq, nFreq];
        double[,] bispectrumMagnitude = new double[nFreq, nFreq];

        double epsilon = 1e-15; // 避免除以零

        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;

                // 平均双谱
                bispectrum[f1, f2] = bispectrumAccum[f1, f2] / numSegments;

                // 双谱幅度
                double mag = bispectrum[f1, f2].Magnitude;
                bispectrumMagnitude[f1, f2] = mag;

                // 平均功率乘积
                double avgPowerProduct = powerProductAccum[f1, f2] / numSegments;
                // 平均功率和频率（注意 powerSumAccum[fSum] 被重复加了每个对 f1,f2 的次数）
                // 需要除以该频率出现的次数 = fSum + 1 (所有使得 f1+f2=fSum 的(f1,f2)对，f1,f2≥0)
                int fSum = f1 + f2;
                int countForSum = fSum + 1; // 使得 f1+f2=fSum 的有效对数: (0,fSum), (1,fSum-1), ..., (fSum,0)
                double avgPowerSum = 0;
                if (countForSum > 0)
                    avgPowerSum = powerSumAccum[fSum] / (numSegments * countForSum);

                // 计算 Bicoherence
                // b²(f1,f2) = |B(f1,f2)|² / (E[|X(f1)X(f2)|²] * E[|X(f1+f2)|²])
                double denominator = avgPowerProduct * avgPowerSum;
                if (denominator > epsilon)
                {
                    bicoherence[f1, f2] = (mag * mag) / denominator;
                    // 限制在 [0, 1] 范围内
                    if (bicoherence[f1, f2] > 1.0) bicoherence[f1, f2] = 1.0;
                    if (bicoherence[f1, f2] < 0.0) bicoherence[f1, f2] = 0.0;
                }
            }
        }

        return new BispectrumResult
        {
            Bispectrum = bispectrum,
            BispectrumMagnitude = bispectrumMagnitude,
            Bicoherence = bicoherence,
            Frequencies = Frequencies,
            FreqResolution = FreqResolution,
            MaxFreqIndex = MaxFreqIndex,
            NumSegments = numSegments
        };
    }

    /// <summary>
    /// 使用 SeeSharpTools 内置 Bispecrtum API 计算双谱（仅双谱幅度，不含 bicoherence）
    /// 可作为自研算法的对照验证
    /// </summary>
    /// <param name="signal">输入EEG信号</param>
    /// <returns>双谱幅度 (log10) 和对应的频率轴</returns>
    public (Complex[,] Bispectrum, double[] FreqBins) ComputeWithBispecrtum(double[] signal)
    {
        int nFreq = MaxFreqIndex + 1;

        // Bispecrtum.GetBispectrum 输出全频率范围的双谱（复数）
        Complex[,] fullBispectrum = new Complex[Nfft / 2 + 1, Nfft / 2 + 1];
        Bispecrtum.GetBispectrum(signal, WindowType.Hanning, ref fullBispectrum);

        // 裁剪到 MaxFreqIndex 范围
        Complex[,] bispectrum = new Complex[nFreq, nFreq];
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
                if (f1 + f2 < nFreq)
                    bispectrum[f1, f2] = fullBispectrum[f1, f2];

        return (bispectrum, Frequencies);
    }

    /// <summary>
    /// 计算信号的 Welch 分段累加器（不计算 bicoherence，用于 ensemble averaging）
    /// 
    /// 返回原始累加器，可通过 Merge 合并多个 epoch 后统一计算 bicoherence。
    /// 论文依据：Tacchino et al. (2020) — inter-trial averaging 将有效段数
    /// 从 ~17 提升到 N×17（N = 同条件 epoch 数），显著提升统计可靠性。
    /// </summary>
    public BispectrumAccumulator ComputeAccumulators(double[] signal)
    {
        int nFreq = MaxFreqIndex + 1;

        if (signal.Length < Nfft)
            throw new ArgumentException($"信号长度 ({signal.Length}) 小于 FFT 长度 ({Nfft})");

        int step = Nfft - Noverlap;
        int numSegments = (signal.Length - Nfft) / step + 1;
        if (numSegments < 2)
            throw new ArgumentException($"信号太短，只有 {numSegments} 个分段（至少需要2个）");

        var accum = new BispectrumAccumulator(nFreq);
        accum.Frequencies = (double[])Frequencies.Clone();
        accum.FreqResolution = FreqResolution;
        accum.MaxFreqIndex = MaxFreqIndex;
        accum.AddedSegments = numSegments;

        object lockObj = new();

        Parallel.For(0, numSegments, segIdx =>
        {
            int startSample = segIdx * step;
            double[] segment = new double[Nfft];
            Array.Copy(signal, startSample, segment, 0, Math.Min(Nfft, signal.Length - startSample));

            double mean = Statistics.Mean(segment);
            for (int i = 0; i < Nfft; i++)
                segment[i] = segment[i] - mean;

            Complex[] spectrum = new Complex[Nfft / 2 + 1];
            Spectrum.AdvanceComplexFFT(segment, WindowType.Hanning, ref spectrum);

            Complex[] X = new Complex[nFreq];
            double[] magSq = new double[nFreq];
            for (int i = 0; i < nFreq; i++)
            {
                X[i] = spectrum[i];
                magSq[i] = X[i].Real * X[i].Real + X[i].Imaginary * X[i].Imaginary;
            }

            Complex[,] localBis = new Complex[nFreq, nFreq];
            double[,] localProd = new double[nFreq, nFreq];
            double[] localSum = new double[nFreq];

            for (int f1 = 0; f1 < nFreq; f1++)
            {
                for (int f2 = 0; f2 < nFreq; f2++)
                {
                    int fSum = f1 + f2;
                    if (fSum >= nFreq) break;

                    Complex prod = X[f1] * X[f2];
                    localBis[f1, f2] = prod * Complex.Conjugate(X[fSum]);
                    localProd[f1, f2] = prod.Real * prod.Real + prod.Imaginary * prod.Imaginary;
                    localSum[fSum] += magSq[fSum];
                }
            }

            lock (lockObj)
            {
                for (int f1 = 0; f1 < nFreq; f1++)
                {
                    for (int f2 = 0; f2 < nFreq; f2++)
                    {
                        if (f1 + f2 >= nFreq) break;
                        accum.RawBispectrum[f1, f2] += localBis[f1, f2];
                        accum.RawPowerProduct[f1, f2] += localProd[f1, f2];
                    }
                }
                for (int f = 0; f < nFreq; f++)
                    accum.RawPowerSum[f] += localSum[f];
            }
        });

        return accum;
    }
}

/// <summary>
/// 双谱原始累加器 — 支持跨 epoch 的 ensemble averaging
/// 
/// Tacchino et al. (2020): 将同条件下多个 trial 的复数双谱累加后
/// 统一计算 bicoherence，有效段数 = N_epochs × N_segments_per_epoch，
/// 显著提升统计可靠性。
/// </summary>
public class BispectrumAccumulator
{
    /// <summary>原始双谱累加（复数），跨段/跨 epoch 求和</summary>
    public Complex[,] RawBispectrum { get; }

    /// <summary>功率乘积累加 E[|X(f1)X(f2)|²]</summary>
    public double[,] RawPowerProduct { get; }

    /// <summary>和频率功率累加 E[|X(f1+f2)|²]</summary>
    public double[] RawPowerSum { get; }

    /// <summary>已添加的总分段数（epoch 数 × 每 epoch 的 Welch 段数）</summary>
    public int AddedSegments { get; set; }

    /// <summary>频率轴 (Hz)</summary>
    public double[] Frequencies { get; set; } = Array.Empty<double>();

    /// <summary>频率分辨率 (Hz)</summary>
    public double FreqResolution { get; set; }

    /// <summary>最大频率索引</summary>
    public int MaxFreqIndex { get; set; }

    public int NumFreq => RawBispectrum.GetLength(0);

    public BispectrumAccumulator(int nFreq)
    {
        RawBispectrum = new Complex[nFreq, nFreq];
        RawPowerProduct = new double[nFreq, nFreq];
        RawPowerSum = new double[nFreq];
    }

    /// <summary>
    /// 合并另一个累加器的数据（用于跨 epoch 累加）
    /// </summary>
    public void Merge(BispectrumAccumulator other)
    {
        int nFreq = NumFreq;
        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                RawBispectrum[f1, f2] += other.RawBispectrum[f1, f2];
                RawPowerProduct[f1, f2] += other.RawPowerProduct[f1, f2];
            }
        }
        for (int f = 0; f < nFreq; f++)
            RawPowerSum[f] += other.RawPowerSum[f];
        AddedSegments += other.AddedSegments;
    }

    /// <summary>
    /// 从累加器计算最终的双相干性结果
    /// </summary>
    /// <param name="totalSegments">总段数（用于归一化），默认使用 AddedSegments</param>
    public BispectrumResult ToBicoherenceResult(int? totalSegments = null)
    {
        int segs = totalSegments ?? AddedSegments;
        if (segs < 1) segs = 1;

        int nFreq = NumFreq;
        var bispectrum = new Complex[nFreq, nFreq];
        var bicoherence = new double[nFreq, nFreq];
        var magnitude = new double[nFreq, nFreq];
        double epsilon = 1e-15;

        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;

                bispectrum[f1, f2] = RawBispectrum[f1, f2] / segs;

                double mag = bispectrum[f1, f2].Magnitude;
                magnitude[f1, f2] = mag;

                double avgPowerProduct = RawPowerProduct[f1, f2] / segs;
                int fSum = f1 + f2;
                int countForSum = fSum + 1; // (0,fSum), (1,fSum-1), ..., (fSum,0)
                double avgPowerSum = 0;
                if (countForSum > 0)
                    avgPowerSum = RawPowerSum[fSum] / (segs * countForSum);

                double denominator = avgPowerProduct * avgPowerSum;
                if (denominator > epsilon)
                {
                    bicoherence[f1, f2] = (mag * mag) / denominator;
                    if (bicoherence[f1, f2] > 1.0) bicoherence[f1, f2] = 1.0;
                    if (bicoherence[f1, f2] < 0.0) bicoherence[f1, f2] = 0.0;
                }
            }
        }

        return new BispectrumResult
        {
            Bispectrum = bispectrum,
            BispectrumMagnitude = magnitude,
            Bicoherence = bicoherence,
            Frequencies = Frequencies,
            FreqResolution = FreqResolution,
            MaxFreqIndex = MaxFreqIndex,
            NumSegments = segs
        };
    }
}

/// <summary>
/// 双谱计算结果
/// </summary>
public class BispectrumResult
{
    /// <summary>复数双谱矩阵 B[f1, f2]</summary>
    public Complex[,] Bispectrum { get; init; } = new Complex[0, 0];

    /// <summary>双谱幅度矩阵 |B[f1, f2]|</summary>
    public double[,] BispectrumMagnitude { get; init; } = new double[0, 0];

    /// <summary>双相干性矩阵 b²[f1, f2]，取值范围 [0, 1]</summary>
    public double[,] Bicoherence { get; init; } = new double[0, 0];

    /// <summary>频率轴 (Hz)</summary>
    public double[] Frequencies { get; init; } = Array.Empty<double>();

    /// <summary>频率分辨率 (Hz)</summary>
    public double FreqResolution { get; init; }

    /// <summary>最大频率索引</summary>
    public int MaxFreqIndex { get; init; }

    /// <summary>分段数量</summary>
    public int NumSegments { get; init; }

    /// <summary>获取对角线 b²(f, f) 上的双相干性值</summary>
    public (double[] freqs, double[] values) GetDiagonalBicoherence()
    {
        int n = Math.Min(Frequencies.Length, MaxFreqIndex + 1);
        var freqs = new double[n];
        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            freqs[i] = Frequencies[i];
            // 对角线上的值：f1=f2=f
            if (i < Bicoherence.GetLength(0) && i < Bicoherence.GetLength(1))
                values[i] = Bicoherence[i, i];
        }
        return (freqs, values);
    }
}
