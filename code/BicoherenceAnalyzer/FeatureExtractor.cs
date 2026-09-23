namespace BicoherenceAnalyzer;

using SeeSharpTools.JY.Statistics;

/// <summary>
/// 双相干性特征提取器
/// 从对角线Bicoherence曲线中提取 α峰（8-13Hz）、δ/θ峰（1-8Hz）等特征
/// </summary>
public class FeatureExtractor
{
    /// <summary>Delta频段: 1-4 Hz</summary>
    public (double Low, double High) DeltaBand { get; set; } = (1.0, 4.0);

    /// <summary>Theta频段: 4-8 Hz</summary>
    public (double Low, double High) ThetaBand { get; set; } = (4.0, 8.0);

    /// <summary>Alpha频段: 8-13 Hz</summary>
    public (double Low, double High) AlphaBand { get; set; } = (8.0, 13.0);

    /// <summary>Beta频段: 13-30 Hz</summary>
    public (double Low, double High) BetaBand { get; set; } = (13.0, 30.0);

    /// <summary>
    /// 从对角线双相干性曲线提取特征
    /// </summary>
    /// <param name="frequencies">频率轴 (Hz)</param>
    /// <param name="bicoherenceDiagonal">对角线 b²(f, f) 值</param>
    /// <returns>提取的特征集合</returns>
    public Dictionary<string, BicoherencePeak> ExtractFeatures(double[] frequencies, double[] bicoherenceDiagonal)
    {
        var features = new Dictionary<string, BicoherencePeak>();

        // 提取各频段峰值
        features["delta"] = FindPeakInBand(frequencies, bicoherenceDiagonal, DeltaBand.Low, DeltaBand.High, "δ/θ");
        features["theta"] = FindPeakInBand(frequencies, bicoherenceDiagonal, ThetaBand.Low, ThetaBand.High, "θ");
        features["alpha"] = FindPeakInBand(frequencies, bicoherenceDiagonal, AlphaBand.Low, AlphaBand.High, "α");
        features["beta"] = FindPeakInBand(frequencies, bicoherenceDiagonal, BetaBand.Low, BetaBand.High, "β");

        // 组合δ+θ频段 (1-8 Hz)
        features["delta_theta"] = FindPeakInBand(frequencies, bicoherenceDiagonal, 1.0, 8.0, "δ/θ");

        return features;
    }

    /// <summary>
    /// 在指定频段内寻找Bicoherence峰值
    /// </summary>
    private static BicoherencePeak FindPeakInBand(double[] frequencies, double[] bicoherenceDiagonal,
        double lowFreq, double highFreq, string bandName)
    {
        double peakFreq = 0;
        double peakValue = 0;

        for (int i = 0; i < frequencies.Length; i++)
        {
            if (frequencies[i] >= lowFreq && frequencies[i] <= highFreq)
            {
                if (bicoherenceDiagonal[i] > peakValue)
                {
                    peakValue = bicoherenceDiagonal[i];
                    peakFreq = frequencies[i];
                }
            }
        }

        return new BicoherencePeak
        {
            BandName = bandName,
            BandRange = (lowFreq, highFreq),
            PeakFrequency = peakFreq,
            PeakBicoherence = peakValue,
            PeakBicoherencePercent = peakValue * 100.0
        };
    }

    /// <summary>
    /// 打印特征摘要到控制台
    /// </summary>
    public static void PrintFeatureSummary(string channel, string state, Dictionary<string, BicoherencePeak> features)
    {
        Console.WriteLine($"\n  ┌─ 通道: {channel} | 状态: {state}");
        Console.WriteLine(string.Format("  ├── {0,-12} {1,-14} {2,-12} {3,-14}", "频段", "频率范围", "峰值频率", "Bicoherence"));
        Console.WriteLine(string.Format("  ├── {0,-12} {1,-14} {2,-12} {3,-14}", new string('─', 12), new string('─', 14), new string('─', 12), new string('─', 14)));

        foreach (var kvp in features)
        {
            var peak = kvp.Value;
            Console.WriteLine($"  ├── {peak.BandName,-12} {peak.BandRange.Low:F1}-{peak.BandRange.High:F1} Hz     {peak.PeakFrequency,6:F2} Hz     {peak.PeakBicoherencePercent,6:F2} %");
        }

        Console.WriteLine($"  └──");
    }

    /// <summary>
    /// 统计 Bicoherence 对角线的整体特征（使用 SeeSharpTools Statistics）
    /// </summary>
    public static (double Mean, double Std, double Max, double Min) GetDiagonalStats(
        double[] bicoherenceDiagonal)
    {
        double mean = Statistics.Mean(bicoherenceDiagonal);
        double std = Statistics.StandardDeviation(bicoherenceDiagonal);
        double max = bicoherenceDiagonal.Max();
        double min = bicoherenceDiagonal.Min();
        return (mean, std, max, min);
    }
}

/// <summary>
/// Bicoherence峰值信息
/// </summary>
public class BicoherencePeak
{
    /// <summary>频段名称（如 "α", "δ", "θ"）</summary>
    public string BandName { get; init; } = "";

    /// <summary>频段范围 (Low, High) Hz</summary>
    public (double Low, double High) BandRange { get; init; }

    /// <summary>峰值对应的频率 (Hz)</summary>
    public double PeakFrequency { get; init; }

    /// <summary>峰值Bicoherence值（0-1范围）</summary>
    public double PeakBicoherence { get; init; }

    /// <summary>峰值Bicoherence百分比（0%-100%）</summary>
    public double PeakBicoherencePercent { get; init; }

    public override string ToString()
    {
        return $"{BandName}峰: {PeakFrequency:F2} Hz, Bicoherence = {PeakBicoherencePercent:F2}%";
    }
}
