using SeeSharpTools.JY.DSP.Utility.Filter1D;

namespace BicoherenceAnalyzer;

/// <summary>
/// EEG 信号带通滤波（Butterworth, 1-45 Hz, 4阶）
/// 使用 SeeSharpTools Filter1D 模块完成滤波器设计与执行
/// </summary>
public static class EegFilter
{
    /// <summary>低截止频率 (Hz)</summary>
    public const double LowCutoff = 1.0;
    /// <summary>高截止频率 (Hz)</summary>
    public const double HighCutoff = 45.0;
    /// <summary>滤波器阶数</summary>
    public const int Order = 4;

    private static readonly Lazy<(double[] B, double[] A)> _coefficients = new(() =>
    {
        var (b, a) = IIRDesign.Butter(Order, new double[] { LowCutoff, HighCutoff }, IIRBandType.Bandpass, 256.0);
        Console.WriteLine($"[滤波] Butterworth {Order}阶 带通 {LowCutoff}–{HighCutoff} Hz 滤波器已就绪");
        return (b, a);
    });

    /// <summary>滤波器系数 B（前向/分子）</summary>
    public static double[] B => _coefficients.Value.B;
    /// <summary>滤波器系数 A（反向/分母）</summary>
    public static double[] A => _coefficients.Value.A;

    /// <summary>
    /// 对 EEG 信号执行 1–45 Hz Butterworth 带通滤波
    /// </summary>
    public static double[] Filter(double[] signal)
    {
        if (signal.Length < 10) return signal; // 太短不滤波
        return IIRFiltering.IIRFilter(B, A, signal);
    }
}
