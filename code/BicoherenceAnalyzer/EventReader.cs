using System.Globalization;

namespace BicoherenceAnalyzer;

/// <summary>
/// EEG冥想实验事件标记读取器
/// 解析 _events.tsv 文件，提取冥想实验中的事件时间线和不同状态的时间段
/// </summary>
public class EventReader
{
    /// <summary>
    /// 所有事件列表
    /// </summary>
    public List<EegEvent> Events { get; private set; } = new();

    /// <summary>
    /// 冥想状态的时间段定义
    /// </summary>
    public record StateSegment(string State, double StartTimeSec, double EndTimeSec, double DurationSec);

    /// <summary>
    /// Stimulus 前时间窗定义（用于刺激前10秒分析）
    /// 每个 stimulus（value=128）都是 Q1 冥想深度探头
    /// Q2/Q3 无独立 stimulus 标记，其响应由按键后自动触发
    /// </summary>
    public record PreStimulusSegment(
        int StimulusIndex,    // 第几个 stimulus epoch (0-based)
        double StimulusOnset,  // stimulus 发生时间 (s)
        int StimulusValue,     // stimulus value (固定 128 = Q1 探头)
        double StartTimeSec,   // 分析窗口起始 (s)
        double EndTimeSec,     // 分析窗口结束 (s) = stimulus onset
        double DurationSec,    // 窗口时长 (s)
        int Q1Response = 0,    // 该 stimulus 后第1个 response → Q1 冥想深度
        int Q2Response = 0,    // 该 stimulus 后第2个 response → Q2 走神程度
        int Q3Response = 0     // 该 stimulus 后第3个 response → Q3 疲劳程度
    );

    /// <summary>
    /// 构造函数：从TSV文件加载事件
    /// </summary>
    public EventReader(string eventsFilePath)
    {
        if (!File.Exists(eventsFilePath))
            throw new FileNotFoundException($"事件文件未找到: {eventsFilePath}");

        Events = ParseEvents(eventsFilePath);
        Console.WriteLine($"[事件] 加载了 {Events.Count} 个事件");
    }

    /// <summary>
    /// 构造函数：从预解析的事件列表构建（用于 BDF 状态通道等非 TSV 事件源）
    /// </summary>
    public EventReader(List<EegEvent> events)
    {
        Events = events;
        Console.WriteLine($"[事件] 直接加载了 {Events.Count} 个事件");
    }

    /// <summary>
    /// 从 BDF 状态通道提取的原始触发事件转换为 EegEvent 列表。
    /// 用于无 events.tsv 的数据集（如 ds003969）。
    /// </summary>
    /// <param name="triggerEvents">BDF 状态通道提取的事件 (onset_sec, value)</param>
    /// <returns>标准化的 EegEvent 列表（trial_type 按 value 推导）</returns>
    public static List<EegEvent> FromTriggerEvents(List<(double OnsetSec, int Value)> triggerEvents)
    {
        var result = new List<EegEvent>();
        foreach (var (onset, value) in triggerEvents)
        {
            // 根据 Biosemi 惯例推导事件类型：
            //   value 包含 bit 信息时，低 8 位用于刺激编码
            //   简化：将 trigger value 映射到事件类型
            string trialType = value switch
            {
                // 常见 trigger code 映射
                >= 1 and <= 8 => "task_marker",    // 任务标记（如 block 开始/结束）
                >= 128 => "stimulus",               // 高位 → stimulus 类
                _ => "event"
            };
            result.Add(new EegEvent
            {
                Onset = onset,
                Duration = 0,
                TrialType = trialType,
                Sample = 0,
                Value = value
            });
        }
        return result;
    }

    /// <summary>
    /// 块设计 epoch 提取：将连续记录按事件分块后，每块内滑窗提取固定长度 epoch。
    /// 用于 ds003969 等块设计数据集（meditation block vs thinking block）。
    /// </summary>
    /// <param name="blockEvents">按时间排序的块边界事件（value 标记块状态）</param>
    /// <param name="totalDurationSec">BDF 总时长 (s)</param>
    /// <param name="epochDurationSec">每个 epoch 的长度 (s)，默认 10s</param>
    /// <param name="slideStepSec">滑窗步长 (s)，默认 5s（50% 重叠）</param>
    /// <returns>每个 epoch 的信息列表（含任务标签）</returns>
    public static List<BlockEpoch> ExtractBlockEpochs(
        List<(double OnsetSec, int Value)> blockEvents,
        double totalDurationSec,
        double epochDurationSec = 10.0,
        double slideStepSec = 5.0)
    {
        var epochs = new List<BlockEpoch>();

        // 简单策略：取首尾事件定义感兴趣区间，区间内滑窗
        if (blockEvents.Count >= 2)
        {
            double blockStart = blockEvents[0].OnsetSec;
            // 任务标签 = 第一个事件的 value (ds003969 中块间 value 不同)
            int blockValue = blockEvents[0].Value;

            double blockEnd = blockEvents[blockEvents.Count - 1].OnsetSec;
            if (blockEnd < blockStart + epochDurationSec)
                blockEnd = Math.Min(totalDurationSec, blockStart + epochDurationSec);

            for (double t = blockStart; t + epochDurationSec <= blockEnd; t += slideStepSec)
            {
                epochs.Add(new BlockEpoch(
                    StartTimeSec: t,
                    EndTimeSec: t + epochDurationSec,
                    DurationSec: epochDurationSec,
                    Label: $"block_value={blockValue}",
                    BlockValue: blockValue,
                    EpochIndex: epochs.Count
                ));
            }
        // 如果 block 太短（span 不足录音的 10%），说明 trigger 只是初始化/状态标记，
        // 并非真正的 block 边界。此时直接对整段录音滑窗，用文件名推断条件。
        double blockSpan = blockEnd - blockStart;
        if (epochs.Count <= 2 || blockSpan < totalDurationSec * 0.1)
        {
            epochs.Clear();  // 丢弃不可靠的 block epoch
        }
    }

    // 如果事件不足或 block 不可靠，使用整段录音滑窗
        if (epochs.Count == 0)
        {
            for (double t = 0; t + epochDurationSec <= totalDurationSec; t += slideStepSec)
            {
                epochs.Add(new BlockEpoch(
                    StartTimeSec: t,
                    EndTimeSec: t + epochDurationSec,
                    DurationSec: epochDurationSec,
                    Label: "full_recording",
                    BlockValue: 0,
                    EpochIndex: epochs.Count
                ));
            }
        }

        return epochs;
    }

    /// <summary>
    /// 解析TSV格式的事件文件
    /// </summary>
    private static List<EegEvent> ParseEvents(string filePath)
    {
        var events = new List<EegEvent>();
        string[] lines = File.ReadAllLines(filePath);

        if (lines.Length < 2)
            return events;

        string[] headers = lines[0].Split('\t');
        int onsetIdx = Array.FindIndex(headers, h => h.Trim() == "onset");
        int durationIdx = Array.FindIndex(headers, h => h.Trim() == "duration");
        int trialTypeIdx = Array.FindIndex(headers, h => h.Trim() == "trial_type");
        int sampleIdx = Array.FindIndex(headers, h => h.Trim() == "sample");
        int valueIdx = Array.FindIndex(headers, h => h.Trim() == "value");

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var evt = new EegEvent
            {
                Onset = ParseDoubleSafe(parts[onsetIdx]),
                Duration = durationIdx >= 0 && parts[durationIdx] != "n/a"
                    ? ParseDoubleSafe(parts[durationIdx]) : 0,
                TrialType = trialTypeIdx >= 0 ? parts[trialTypeIdx].Trim() : "",
                Sample = sampleIdx >= 0 ? (int)ParseDoubleSafe(parts[sampleIdx]) : 0,
                Value = valueIdx >= 0 ? (int)ParseDoubleSafe(parts[valueIdx]) : 0
            };
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// 提取冥想实验的状态时间段
    /// 
    /// 策略说明：
    /// 1. 实验每约2分钟出现一个问题（stimulus），被试回答（response）后继续冥想
    /// 2. Baseline（冥想前）: 实验开始后的前5分钟 (0-300s)，此时被试刚开始进入冥想状态
    /// 3. Meditation（冥想中）: 录音中间段落的连续冥想期 - 选择实验中间最长的冥想时段
    /// 4. Recovery（冥想后）: 实验最后5分钟 (总时长-300 到 总时长)
    ///
    /// 更精确的策略：找到两个相邻 question onset 之间的最大间隔作为深度冥想期
    /// </summary>
    public List<StateSegment> ExtractStateSegments(double totalRecordingDurationSec, double segmentDurationSec = 300.0)
    {
        var segments = new List<StateSegment>();

        // === 策略1: Baseline = 前5分钟 ===
        double baselineEnd = Math.Min(segmentDurationSec, totalRecordingDurationSec * 0.15);
        segments.Add(new StateSegment("baseline", 0, baselineEnd, baselineEnd));

        // === 策略2: Meditation = 找到最长的无问题干扰期（中间段落） ===
        var stimulusEvents = Events
            .Where(e => e.TrialType == "stimulus")
            .OrderBy(e => e.Onset)
            .ToList();

        if (stimulusEvents.Count >= 2)
        {
            // 寻找相邻 stimulus 之间的最大间隔（被试在此期间冥想）
            double maxGap = 0;
            double maxGapStart = 0;
            double maxGapEnd = 0;

            for (int i = 1; i < stimulusEvents.Count; i++)
            {
                double gap = stimulusEvents[i].Onset - stimulusEvents[i - 1].Onset;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    // 冥想期从上一个问题回答后开始（如果存在回答）
                    // 简化：使用前一个stimulus之后20秒（给被试回答问题的时间）
                    maxGapStart = stimulusEvents[i - 1].Onset + 20;
                    maxGapEnd = stimulusEvents[i].Onset;
                }
            }

            if (maxGap > 60) // 至少1分钟的冥想期
            {
                // 限制冥想段长度不超过 segmentDurationSec
                if (maxGap > segmentDurationSec)
                {
                    double midPoint = (maxGapStart + maxGapEnd) / 2;
                    maxGapStart = midPoint - segmentDurationSec / 2;
                    maxGapEnd = midPoint + segmentDurationSec / 2;
                }
                segments.Add(new StateSegment("meditation", maxGapStart, maxGapEnd, maxGapEnd - maxGapStart));
            }

            // 如果事件法没找到合适冥想期，使用时间中点法
            if (!segments.Any(s => s.State == "meditation"))
            {
                double meditationStart = totalRecordingDurationSec * 0.3;
                double meditationEnd = meditationStart + segmentDurationSec;
                if (meditationEnd > totalRecordingDurationSec)
                    meditationEnd = totalRecordingDurationSec;
                segments.Add(new StateSegment("meditation", meditationStart, meditationEnd,
                    meditationEnd - meditationStart));
            }
        }
        else
        {
            // 无足够事件，使用时间中点法
            double meditationStart = totalRecordingDurationSec * 0.3;
            double meditationEnd = meditationStart + segmentDurationSec;
            if (meditationEnd > totalRecordingDurationSec)
                meditationEnd = totalRecordingDurationSec;
            segments.Add(new StateSegment("meditation", meditationStart, meditationEnd,
                meditationEnd - meditationStart));
        }

        // === 策略3: Recovery = 最后5分钟 ===
        double recoveryStart = Math.Max(0, totalRecordingDurationSec - segmentDurationSec);
        segments.Add(new StateSegment("recovery", recoveryStart, totalRecordingDurationSec,
            totalRecordingDurationSec - recoveryStart));

        return segments;
    }

    /// <summary>
    /// 获取从指定时间范围内的事件
    /// </summary>
    public List<EegEvent> GetEventsInRange(double startSec, double endSec)
    {
        return Events.Where(e => e.Onset >= startSec && e.Onset <= endSec).ToList();
    }

    /// <summary>
    /// 提取每个 stimulus 之前的固定时长窗口（用于刺激前分析）
    /// 每个 segment 附带 Q1/Q2/Q3 响应值，用于按冥想深度分组
    /// </summary>
    /// <param name="preDurationSec">每个 stimulus 之前取多少秒（默认 10.0s）</param>
    /// <returns>每个 stimulus 对应的分析时间段（含响应值）</returns>
    public List<PreStimulusSegment> ExtractPreStimulusSegments(double preDurationSec = 10.0)
    {
        return ExtractPreStimulusSegmentsInternal(preDurationSec).Segments;
    }

    /// <summary>
    /// 提取每个 stimulus 前的时间窗，并附带 Q1/Q2 被试应答值（忽略 Q3）。
    /// 所有 stimulus（value=128）都是 Q1 探头，无需按 %3 筛选。
    /// </summary>
    /// <param name="preDurationSec">每个 stimulus 之前取多少秒（默认 10.0s）</param>
    /// <returns>
    /// 元组列表：(PreStimulusSegment 段信息, q1Response 冥想深度, q2Response 走神程度)
    /// </returns>
    public List<(PreStimulusSegment Segment, int Q1Response, int Q2Response)> ExtractQ1EpochsWithResponses(double preDurationSec = 10.0)
    {
        var (segments, _) = ExtractPreStimulusSegmentsInternal(preDurationSec);
        return segments.Select(s => (s, s.Q1Response, s.Q2Response)).ToList();
    }

    /// <summary>
    /// 内部实现：提取 stimulus 前时间窗，并按 onset 匹配 Q1/Q2/Q3 响应值
    /// 响应匹配规则：取 stimulus onset 之后、下一个 stimulus onset 之前的 response 事件
    /// 第1个 response = Q1 冥想深度，第2个 = Q2 走神程度，第3个 = Q3 疲劳程度
    /// </summary>
    private (List<PreStimulusSegment> Segments, Dictionary<int, int> GlobalStimIndices)
        ExtractPreStimulusSegmentsInternal(double preDurationSec)
    {
        var segments = new List<PreStimulusSegment>();
        var globalStimIndices = new Dictionary<int, int>(); // epochIndex → global stim index

        var stimEvents = Events
            .Where(e => e.TrialType == "stimulus")
            .OrderBy(e => e.Onset)
            .ToList();

        var respEvents = Events
            .Where(e => e.TrialType == "response")
            .OrderBy(e => e.Onset)
            .ToList();

        int epochIdx = 0;
        for (int i = 0; i < stimEvents.Count; i++)
        {
            var stim = stimEvents[i];
            double startTime = stim.Onset - preDurationSec;

            if (startTime < 0)
            {
                Console.WriteLine($"  ⚠ stimulus #{i} @ {stim.Onset:F1}s: 前面不足 {preDurationSec}s，跳过");
                continue;
            }

            // 匹配该 stimulus 后的响应：取 stim[i].onset ~ stim[i+1].onset 之间的 response
            double nextOnset = (i + 1 < stimEvents.Count) ? stimEvents[i + 1].Onset : double.MaxValue;
            var myResp = respEvents
                .Where(r => r.Onset > stim.Onset && r.Onset < nextOnset)
                .OrderBy(r => r.Onset)
                .ToList();

            // 没应答 = 入迷/没听见/睡着，不是有效 Q1 评分，跳过该 stimulus
            if (myResp.Count == 0)
                continue;

            int q1 = myResp[0].Value;
            int q2 = myResp.Count > 1 ? myResp[1].Value : 0;
            int q3 = myResp.Count > 2 ? myResp[2].Value : 0;

            segments.Add(new PreStimulusSegment(
                StimulusIndex: epochIdx,
                StimulusOnset: stim.Onset,
                StimulusValue: stim.Value,
                StartTimeSec: startTime,
                EndTimeSec: stim.Onset,
                DurationSec: preDurationSec,
                Q1Response: q1,
                Q2Response: q2,
                Q3Response: q3
            ));
            globalStimIndices[epochIdx] = i;
            epochIdx++;
        }

        return (segments, globalStimIndices);
    }

    private static double ParseDoubleSafe(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "n/a")
            return 0;
        return double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// EEG事件数据结构
/// </summary>
public class EegEvent
{
    /// <summary>事件发生的起始时间（秒）</summary>
    public double Onset { get; set; }

    /// <summary>事件持续时间（秒），n/a表示瞬时事件</summary>
    public double Duration { get; set; }

    /// <summary>事件类型：stimulus（问题出现），response（被试回答）</summary>
    public string TrialType { get; set; } = "";

    /// <summary>事件对应的采样点编号（0-based）</summary>
    public int Sample { get; set; }

    /// <summary>事件数值标记：2/4/8=回答，128=问题开始</summary>
    public int Value { get; set; }

    public override string ToString()
    {
        return $"onset={Onset:F3}s, type={TrialType}, value={Value}, sample={Sample}";
    }
}

/// <summary>
/// 块设计 epoch 定义（用于 ds003969 等无离散 stimulus 的数据集）
/// </summary>
public record BlockEpoch(
    double StartTimeSec,
    double EndTimeSec,
    double DurationSec,
    string Label,
    int BlockValue,
    int EpochIndex
);
