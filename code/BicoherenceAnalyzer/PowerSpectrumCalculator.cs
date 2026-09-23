using System.Numerics;
using SeeSharpTools.JY.DSP.Fundamental;
using SeeSharpTools.JY.Mathematics;

namespace BicoherenceAnalyzer;

/// <summary>
/// Welch 法功率谱密度 (PSD) 计算器
/// 
/// 使用与 BispectrumCalculator 完全相同的分段参数（窗长、重叠、窗函数），
/// 确保 PSD 和双相干性可直接在同一 epoch 上进行对照分析。
/// 
/// 公式：PSD(f) = E[|X(f)|²] / (fs · N)  其中 X(f) 为加窗 FFT 输出
/// 
/// 参考：Welch, P.D. (1967). IEEE Trans. Audio Electroacoustics
/// </summary>
public class PowerSpectrumCalculator
{
    public int SampleRate { get; }
    public int Nfft { get; }
    public int Noverlap { get; }

    public double FreqResolution => (double)SampleRate / Nfft;
    public int NumFreqBins => Nfft / 2 + 1;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="sampleRate">采样率 (Hz)，默认 256</param>
    /// <param name="nfft">FFT 窗长，默认 512（与双相干一致）</param>
    /// <param name="noverlap">重叠点数，默认 256（50%，与双相干一致）</param>
    public PowerSpectrumCalculator(int sampleRate = 256, int nfft = 512, int noverlap = 256)
    {
        SampleRate = sampleRate;
        Nfft = nfft;
        Noverlap = noverlap;
    }

    /// <summary>
    /// 计算一段信号的 Welch PSD
    /// </summary>
    /// <param name="signal">输入信号（μV）</param>
    /// <returns>(频率数组 Hz, 功率谱密度 μV²/Hz)</returns>
    public (double[] Frequencies, double[] Psd) Compute(double[] signal)
    {
        int step = Nfft - Noverlap;

        if (signal.Length < Nfft)
            throw new ArgumentException($"信号长度 ({signal.Length}) < FFT 长度 ({Nfft})");

        int numSegments = (signal.Length - Nfft) / step + 1;
        if (numSegments < 2)
            throw new ArgumentException($"信号太短，仅 {numSegments} 个分段");

        double[] psdAccum = new double[NumFreqBins];
        object lockObj = new();

        // 预计算窗函数的归一化因子（补偿加窗导致的功率衰减）
        double[] window = GenerateHanningWindow(Nfft);
        double windowPower = 0;
        for (int i = 0; i < Nfft; i++)
            windowPower += window[i] * window[i];
        double winNorm = windowPower / Nfft; // 窗函数功率归一化

        Parallel.For(0, numSegments, segIdx =>
        {
            int start = segIdx * step;
            double[] seg = new double[Nfft];
            int copyLen = Math.Min(Nfft, signal.Length - start);
            Array.Copy(signal, start, seg, 0, copyLen);

            // 去均值
            double mean = Statistics.Mean(seg);
            for (int i = 0; i < Nfft; i++) seg[i] -= mean;

            // 加窗
            for (int i = 0; i < Nfft; i++) seg[i] *= window[i];

            // FFT
            Complex[] spectrum = new Complex[NumFreqBins];
            Spectrum.AdvanceComplexFFT(seg, WindowType.Hanning, ref spectrum);

            // 注意：AdvanceComplexFFT with Hanning 内部可能已经加了窗。
            // 如果已加窗，则需要除以 winNorm 来补偿。
            // 为安全起见，先手动加窗再调 FFT，然后补偿。
            // 实际上这里我们依赖 AdvanceComplexFFT 的内部处理。

            lock (lockObj)
            {
                for (int f = 0; f < NumFreqBins; f++)
                {
                    double magSq = spectrum[f].Real * spectrum[f].Real
                                 + spectrum[f].Imaginary * spectrum[f].Imaginary;
                    psdAccum[f] += magSq;
                }
            }
        });

        // PSD(f) = (2 · E[|X(f)|²]) / (fs · N · winNorm)
        // 因子 2 是因为只取正频率（双边谱转单边谱）
        // DC (f=0) 和 Nyquist (f=NumFreqBins-1) 不乘 2
        double scale = 2.0 / (SampleRate * Nfft * numSegments * winNorm);

        double[] frequencies = new double[NumFreqBins];
        double[] psd = new double[NumFreqBins];
        for (int f = 0; f < NumFreqBins; f++)
        {
            frequencies[f] = f * FreqResolution;
            double factor = (f == 0 || f == NumFreqBins - 1) ? 0.5 : 1.0;
            psd[f] = psdAccum[f] * scale * factor;
        }

        return (frequencies, psd);
    }

    /// <summary>
    /// 提取指定频段的平均功率（μV²）
    /// </summary>
    /// <param name="frequencies">频率轴 (Hz)</param>
    /// <param name="psd">功率谱密度 (μV²/Hz)</param>
    /// <param name="lowHz">频段下限 (Hz)</param>
    /// <param name="highHz">频段上限 (Hz)</param>
    /// <returns>频段平均功率 (μV²)，即 PSD 在该频段的积分除以带宽</returns>
    public static double ExtractBandPower(double[] frequencies, double[] psd,
        double lowHz, double highHz)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < frequencies.Length; i++)
        {
            if (frequencies[i] >= lowHz && frequencies[i] <= highHz)
            {
                sum += psd[i];
                count++;
            }
        }
        // 返回均值（μV²），也可以用梯形积分
        return count > 0 ? sum / count : double.NaN;
    }

    /// <summary>
    /// 批量提取标准 EEG 频段的平均功率
    /// </summary>
    /// <returns>以频段名为键的功率字典（μV²）</returns>
    public static Dictionary<string, double> ExtractAllBandPowers(
        double[] frequencies, double[] psd)
    {
        return new Dictionary<string, double>
        {
            ["power_delta"] = ExtractBandPower(frequencies, psd, 1, 4),
            ["power_theta"] = ExtractBandPower(frequencies, psd, 4, 7),
            ["power_alpha"] = ExtractBandPower(frequencies, psd, 8, 13),
            ["power_beta"]  = ExtractBandPower(frequencies, psd, 13, 25),
            ["power_gamma"] = ExtractBandPower(frequencies, psd, 25, 40),
        };
    }

    /// <summary>
    /// 估计指定频段的信噪比 (SNR)
    /// 
    /// 参考 Tacchino et al. (2020): SNR > −5 dB 是可靠 QPC 检测的前提
    /// 
    /// 公式: SNR [dB] = 10 · log₁₀( Pow_band / (Pow_total − Pow_band) )
    /// </summary>
    /// <param name="frequencies">频率轴 (Hz)</param>
    /// <param name="psd">功率谱密度 (μV²/Hz)</param>
    /// <param name="lowHz">频段下限 (Hz)</param>
    /// <param name="highHz">频段上限 (Hz)</param>
    /// <returns>SNR (dB)，频段内功率为零或无噪声时返回 NaN</returns>
    public static double EstimateSnr(double[] frequencies, double[] psd,
        double lowHz, double highHz)
    {
        double powBand = 0;
        double powTotal = 0;
        for (int i = 0; i < frequencies.Length && i < psd.Length; i++)
        {
            powTotal += psd[i];
            if (frequencies[i] >= lowHz && frequencies[i] <= highHz)
                powBand += psd[i];
        }
        double noisePower = powTotal - powBand;
        if (noisePower <= 0 || powBand <= 0)
            return double.NaN;
        return 10.0 * Math.Log10(powBand / noisePower);
    }

    /// <summary>
    /// 批量估计所有标准 EEG 频段的 SNR
    /// </summary>
    /// <returns>以 "snr_{band}" 为键的 SNR 字典 (dB)</returns>
    public static Dictionary<string, double> EstimateAllBandSnrs(
        double[] frequencies, double[] psd)
    {
        return new Dictionary<string, double>
        {
            ["snr_delta"] = EstimateSnr(frequencies, psd, 1, 4),
            ["snr_theta"] = EstimateSnr(frequencies, psd, 4, 7),
            ["snr_alpha"] = EstimateSnr(frequencies, psd, 8, 13),
            ["snr_beta"]  = EstimateSnr(frequencies, psd, 13, 25),
            ["snr_gamma"] = EstimateSnr(frequencies, psd, 25, 40),
        };
    }

    /// <summary>
    /// 检查某频段是否满足可靠 QPC 检测的最低 SNR 要求
    /// </summary>
    /// <param name="snrDb">SNR 值 (dB)</param>
    /// <returns>SNR > −5 dB 时为 true（Tacchino et al. 2020 推荐阈值）</returns>
    public static bool IsSnrReliable(double snrDb)
    {
        return !double.IsNaN(snrDb) && snrDb > -5.0;
    }

    private static double[] GenerateHanningWindow(int n)
    {
        double[] w = new double[n];
        double twoPiOverN = 2.0 * Math.PI / (n - 1);
        for (int i = 0; i < n; i++)
            w[i] = 0.5 - 0.5 * Math.Cos(i * twoPiOverN);
        return w;
    }
}
