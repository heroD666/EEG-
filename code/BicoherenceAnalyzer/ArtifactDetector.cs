namespace BicoherenceAnalyzer;

/// <summary>
/// EEG 伪迹检测器
/// 
/// 基于阈值的方法检测眼电（EOG）和脑电（EEG）伪迹：
/// - EOG 通道：±100 μV 阈值（检测眨眼/眼动）
/// - EEG 通道：±150 μV 阈值（检测极端肌肉/电极伪迹）
/// 
/// 参考：EEGLAB 默认拒绝阈值；Delorme & Makeig (2004) J Neurosci Methods
/// </summary>
public static class ArtifactDetector
{
    /// <summary>EOG 通道的默认阈值（μV），超出此范围视为眨眼/眼动伪迹
    /// 注: BioSemi EXG 通道正常眨眼可达 200-500 μV, 故阈值设为 200</summary>
    public const double DefaultEogThreshold = 200.0;

    /// <summary>EEG 通道的默认阈值（μV），超出此范围视为极端伪迹</summary>
    public const double DefaultEegThreshold = 150.0;

    /// <summary>
    /// 检测 epoch 是否含有伪迹
    /// </summary>
    /// <param name="eogDataList">各 EOG 通道的 epoch 数据（可能为空数组）</param>
    /// <param name="eegDataList">各 EEG 通道的 epoch 数据（可能为空数组）</param>
    /// <param name="eogThreshold">EOG 阈值 (μV)，默认 ±100</param>
    /// <param name="eegThreshold">EEG 阈值 (μV)，默认 ±150</param>
    /// <returns>(是否干净, 拒绝原因). 干净时 reason 为 null</returns>
    public static (bool IsClean, string? Reason) CheckEpoch(
        IReadOnlyList<double[]> eogDataList,
        IReadOnlyList<double[]> eegDataList,
        double eogThreshold = DefaultEogThreshold,
        double eegThreshold = DefaultEegThreshold)
    {
        // 检查 EOG 通道
        for (int i = 0; i < eogDataList.Count; i++)
        {
            double[] data = eogDataList[i];
            if (data.Length == 0) continue;

            double maxAbs = MaxAbsolute(data);
            if (maxAbs > eogThreshold)
                return (false, $"EOG[{i}] 超阈值: max={maxAbs:F1}μV > {eogThreshold}μV");
        }

        // 检查 EEG 通道极端伪迹
        for (int i = 0; i < eegDataList.Count; i++)
        {
            double[] data = eegDataList[i];
            if (data.Length == 0) continue;

            double maxAbs = MaxAbsolute(data);
            if (maxAbs > eegThreshold)
                return (false, $"EEG[{i}] 极端伪迹: max={maxAbs:F1}μV > {eegThreshold}μV");
        }

        return (true, null);
    }

    /// <summary>
    /// 仅检测 EOG 通道（用于轻量级伪迹剔除）
    /// </summary>
    public static (bool IsClean, string? Reason) CheckEogOnly(
        IReadOnlyList<double[]> eogDataList,
        double eogThreshold = DefaultEogThreshold)
    {
        return CheckEpoch(eogDataList, Array.Empty<double[]>(), eogThreshold, double.MaxValue);
    }

    /// <summary>
    /// 检测当前 EEG 通道是否含极端伪迹（仅检查 EEG 自身幅度）
    /// </summary>
    public static (bool IsClean, string? Reason) CheckEegOnly(
        double[] eegData,
        double eegThreshold = DefaultEegThreshold)
    {
        if (eegData.Length == 0) return (true, null);
        double maxAbs = MaxAbsolute(eegData);
        if (maxAbs > eegThreshold)
            return (false, $"EEG 极端幅值: max={maxAbs:F1}μV > {eegThreshold}μV");
        return (true, null);
    }

    /// <summary>
    /// 综合检测: EOG 大幅眼动 + EEG 自身极端值
    /// 只要有一项不通过就拒绝
    /// </summary>
    public static (bool IsClean, string? Reason) CheckEpochCombined(
        IReadOnlyList<double[]> eogDataList,
        double[] eegData,
        double eogThreshold = DefaultEogThreshold,
        double eegThreshold = DefaultEegThreshold)
    {
        // 1. 检查 EEG 自身极端值
        if (eegData.Length > 0)
        {
            double maxAbsEeg = MaxAbsolute(eegData);
            if (maxAbsEeg > eegThreshold)
                return (false, $"EEG 极端幅值: max={maxAbsEeg:F1}μV > {eegThreshold}μV");
        }

        // 2. 检查 EOG 大幅眼动
        for (int i = 0; i < eogDataList.Count; i++)
        {
            double[] data = eogDataList[i];
            if (data.Length == 0) continue;
            double maxAbs = MaxAbsolute(data);
            if (maxAbs > eogThreshold)
                return (false, $"EOG[{i}] 大幅眼动: max={maxAbs:F1}μV > {eogThreshold}μV");
        }

        return (true, null);
    }

    /// <summary>
    /// 获取默认的 EOG 通道名称列表（BioSemi 64+8 布局中的 EXG1–EXG4 为眼电，EXG5-8 为肌电/呼吸/皮电等）
    /// </summary>
    public static string[] DefaultEogChannelNames { get; } =
        { "EXG1", "EXG2", "EXG3", "EXG4" };

    /// <summary>
    /// 从通道名称列表中找到 EOG 通道的索引（仅 EXG1-EXG4，排除 EXG5-8）
    /// </summary>
    /// <param name="standardChannelNames">按 BDF 顺序排列的标准通道名</param>
    /// <returns>EOG 通道索引列表</returns>
    public static List<int> FindEogChannelIndices(string[] standardChannelNames)
    {
        var indices = new List<int>();
        for (int i = 0; i < standardChannelNames.Length; i++)
        {
            var name = standardChannelNames[i];
            // 仅 EXG1-EXG4 为眼电通道
            if (name == "EXG1" || name == "EXG2" || name == "EXG3" || name == "EXG4")
                indices.Add(i);
        }
        return indices;
    }

    private static double MaxAbsolute(double[] data)
    {
        double max = 0;
        for (int i = 0; i < data.Length; i++)
        {
            double abs = Math.Abs(data[i]);
            if (abs > max) max = abs;
        }
        return max;
    }
}
