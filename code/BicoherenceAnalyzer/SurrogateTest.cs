using System.Numerics;
using SeeSharpTools.JY.DSP.Fundamental;
using ScottPlot;

namespace BicoherenceAnalyzer;

/// <summary>
/// Surrogate 数据显著性检验 — 基于相位随机化的零假设检验
/// 
/// 参考 Tacchino et al. (2020) Section II-B:
///   对原始信号做 FFT → 保持幅度、随机化相位 → IFFT 得到 surrogate
///   重复 N 次生成 surrogate 分布，取 95% 分位数为显著性阈值
///   原始 bicoherence 超过阈值的位置即为统计显著的 QPC
/// </summary>
public class SurrogateTest
{
    /// <summary>surrogate 生成的默认次数</summary>
    public const int DefaultNumSurrogates = 100;

    /// <summary>显著性水平（95% = 0.05）</summary>
    public const double DefaultAlpha = 0.05;

    private readonly BispectrumCalculator _calculator;
    private readonly Random _rng;

    public SurrogateTest(BispectrumCalculator calculator)
    {
        _calculator = calculator;
        _rng = new Random(42); // 固定种子保证可复现
    }

    /// <summary>
    /// 生成一个相位随机化的 surrogate 信号
    /// 
    /// 步骤:
    ///   1. 对原始信号做 FFT 得到频谱 X(f) = A(f)·e^{iφ(f)}
    ///   2. 对每个频率分量（除 DC 和 Nyquist）: φ'(f) = φ(f) + 2π·U(0,1)
    ///   3. 保持共轭对称性: X'(N−f) = X'(f)*
    ///   4. IFFT 回到时域
    /// </summary>
    /// <param name="signal">原始信号</param>
    /// <returns>相位随机化的 surrogate 信号（与输入等长）</returns>
    public double[] GeneratePhaseRandomizedSurrogate(double[] signal)
    {
        int n = signal.Length;

        // 零填充到 2 的幂（BasicFFT 要求）
        int nFft = NextPowerOfTwo(n);
        double[] padded = new double[nFft];
        Array.Copy(signal, padded, n);

        // Forward FFT: real → complex
        Complex[] spectrum = new Complex[nFft / 2 + 1];
        BasicFFT.RealFFT(padded, ref spectrum);

        // 随机化相位（保持共轭对称性通过 RealIFFT 自动处理）
        int usableBins = spectrum.Length;
        for (int i = 1; i < usableBins; i++)
        {
            double magnitude = spectrum[i].Magnitude;
            // 完全随机相位，等价于破坏原始相位结构
            double randomPhase = 2.0 * Math.PI * _rng.NextDouble();
            spectrum[i] = Complex.FromPolarCoordinates(magnitude, randomPhase);
        }
        // DC (i=0) 和可能的 Nyquist 保持原幅度（随机符号）
        // 对 Nyquist (如果存在): 也随机化相位但限制为 ±π（保持实值）
        if (nFft % 2 == 0 && usableBins > 0)
        {
            int nyquist = usableBins - 1;
            double mag = spectrum[nyquist].Magnitude;
            double sign = _rng.NextDouble() < 0.5 ? 0 : Math.PI;
            spectrum[nyquist] = Complex.FromPolarCoordinates(mag, sign);
        }

        // Inverse FFT: complex → real
        double[] reconstructed = new double[nFft];
        BasicFFT.RealIFFT(spectrum, ref reconstructed);

        // 裁剪回原始长度
        double[] surrogate = new double[n];
        Array.Copy(reconstructed, surrogate, n);

        return surrogate;
    }

    /// <summary>
    /// 计算单个信号的 surrogate bicoherence 阈值
    /// 
    /// 生成 N 个 surrogate → 每个计算 bicoherence → 取 95% 分位数为逐像素阈值
    /// </summary>
    /// <param name="signal">原始信号（已滤波）</param>
    /// <param name="numSurrogates">surrogate 数量，默认 100</param>
    /// <returns>(阈值矩阵, 所有 surrogate 的 bicoherence 列表)</returns>
    public (double[,] Threshold, List<double[,]> SurrogateBicoherences)
        ComputeSurrogateThreshold(double[] signal, int numSurrogates = DefaultNumSurrogates)
    {
        int nFreq = _calculator.MaxFreqIndex + 1;
        var surrogateBicos = new List<double[,]>(numSurrogates);

        Console.WriteLine($"[Surrogate] 生成 {numSurrogates} 个 surrogate，计算 bicoherence...");

        for (int s = 0; s < numSurrogates; s++)
        {
            double[] surrogate = GeneratePhaseRandomizedSurrogate(signal);
            // 去 DC（surrogate 生成过程可能引入微小 DC 偏移）
            double mean = surrogate.Average();
            for (int i = 0; i < surrogate.Length; i++) surrogate[i] -= mean;

            try
            {
                var result = _calculator.Compute(surrogate);
                surrogateBicos.Add((double[,])result.Bicoherence.Clone());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ⚠ Surrogate #{s} 计算失败: {ex.Message}");
            }

            if ((s + 1) % 20 == 0)
                Console.WriteLine($"    ... {s + 1}/{numSurrogates} surrogates 完成");
        }

        if (surrogateBicos.Count == 0)
            throw new InvalidOperationException("所有 surrogate 计算均失败");

        // 逐像素计算 95% 分位数
        double[,] threshold = new double[nFreq, nFreq];
        int actualN = surrogateBicos.Count;
        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;

                var values = new double[actualN];
                for (int s = 0; s < actualN; s++)
                    values[s] = surrogateBicos[s][f1, f2];

                Array.Sort(values);
                int idx = (int)Math.Ceiling((1.0 - DefaultAlpha) * actualN) - 1;
                if (idx < 0) idx = 0;
                if (idx >= actualN) idx = actualN - 1;
                threshold[f1, f2] = values[idx];
            }
        }

        return (threshold, surrogateBicos);
    }

    /// <summary>
    /// 从阈值矩阵生成显著性掩模
    /// </summary>
    /// <param name="bicoherence">实际的 bicoherence 矩阵</param>
    /// <param name="threshold">surrogate 95% 分位数阈值矩阵</param>
    /// <returns>布尔掩模: true = 统计显著 (p&lt;0.05)</returns>
    public static bool[,] GetSignificanceMask(double[,] bicoherence, double[,] threshold)
    {
        int nFreq = bicoherence.GetLength(0);
        var mask = new bool[nFreq, nFreq];
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                mask[f1, f2] = bicoherence[f1, f2] > threshold[f1, f2];
            }
        return mask;
    }

    /// <summary>
    /// 统计显著性像素占比
    /// </summary>
    public static double SignificantFraction(bool[,] mask)
    {
        int total = 0, significant = 0;
        int n = mask.GetLength(0);
        for (int f1 = 0; f1 < n; f1++)
            for (int f2 = 0; f2 < n; f2++)
            {
                if (f1 + f2 >= n) break;
                total++;
                if (mask[f1, f2]) significant++;
            }
        return total > 0 ? (double)significant / total : 0;
    }

    /// <summary>
    /// 保存显著性掩模为 PNG（白色=显著, 黑色=不显著）
    /// </summary>
    public static void SaveSignificanceMask(bool[,] mask, double[] frequencies,
        string outputPath, string title, double maxFreqHz)
    {
        int nFreq = frequencies.Length;
        double freqStep = nFreq > 1 ? frequencies[1] - frequencies[0] : 0.5;
        int displayMax = (int)(maxFreqHz / freqStep);
        displayMax = Math.Min(displayMax, nFreq);

        int width = 800, height = 800;
        var plt = new Plot(width, height);

        // 收集显著性像素坐标
        var sigXs = new List<double>();
        var sigYs = new List<double>();
        var bgXs = new List<double>();
        var bgYs = new List<double>();

        for (int f1 = 0; f1 < displayMax && f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < displayMax && f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) continue;
                if (mask[f1, f2])
                {
                    sigXs.Add(frequencies[f1]);
                    sigYs.Add(frequencies[f2]);
                }
                else
                {
                    bgXs.Add(frequencies[f1]);
                    bgYs.Add(frequencies[f2]);
                }
            }
        }

        // 背景：黑色点
        if (bgXs.Count > 0)
            plt.AddScatter(bgXs.ToArray(), bgYs.ToArray(),
                color: System.Drawing.Color.Black, markerSize: 2, markerShape: ScottPlot.MarkerShape.filledSquare);

        // 显著像素：白色点
        if (sigXs.Count > 0)
            plt.AddScatter(sigXs.ToArray(), sigYs.ToArray(),
                color: System.Drawing.Color.White, markerSize: 2, markerShape: ScottPlot.MarkerShape.filledSquare);

        plt.Title(title);
        plt.XLabel("f1 (Hz)");
        plt.YLabel("f2 (Hz)");
        plt.SetAxisLimits(0, maxFreqHz, 0, maxFreqHz);

        double sigPct = SignificantFraction(mask) * 100;
        // 添加图例文本
        plt.AddAnnotation($"Significant: {sigPct:F1}%",
            alignment: ScottPlot.Alignment.UpperRight);

        plt.SaveFig(outputPath);
        Console.WriteLine($"  [Significance] {sigPct:F1}% 像素显著 → {outputPath}");
    }

    private static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
