using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace BicoherenceAnalyzer;

/// <summary>
/// EEG双谱/双相干性分析 - 主程序
/// 
/// 分析模式：刺激前时间窗分析 (Pre-Stimulus Window)
/// 对每个 stimulus 出现前的 N 秒进行双谱/双相干性计算，
/// 按被试回答的冥想深度进行分组对比。
/// 
/// 数据来源：OpenNeuro数据集 ds001787 (EEG meditation study)
/// 参考论文：Changes in Electroencephalographic Bicoherence During Sevoflurane Anesthesia
/// </summary>
class Program
{
    // === 配置参数（可修改） ===

    /// <summary>数据集根目录</summary>
    static string DataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

    /// <summary>被试ID</summary>
    static string SubjectId = "sub-001";

    /// <summary>会话ID（留空则自动检测第一个可用的ses）</summary>
    static string SessionId = "ses-01";

    /// <summary>结果输出目录</summary>
    static string ResultsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results");

    /// <summary>目标分析通道（12通道全脑覆盖）</summary>
    static string[] TargetChannels = { "Fp1", "Fp2", "C3", "C4", "CP3", "CP4", "FCz", "Fz", "Cz", "Pz", "O1", "O2" };

    /// <summary>采样率 (Hz)</summary>
    static int SampleRate = 256;

    /// <summary>FFT分段长度（约2秒，频率分辨率0.5Hz）</summary>
    static int Nfft = 512;

    /// <summary>重叠点数（50%重叠）</summary>
    static int Noverlap = 256;

    /// <summary>最大分析频率 (Hz)</summary>
    static double MaxFrequency = 45.0;

    /// <summary>显示的最大频率 (Hz) — 聚焦 δ/θ/α/β 关键频段</summary>
    static double DisplayMaxFreq = 30.0;

    /// <summary>每个 stimulus 之前取多少秒进行分析</summary>
    static double PreStimulusSec = 10.0;

    /// <summary>块设计数据集 epoch 长度（秒），需足够长以保证可靠的双相干性估计（≥20 段）</summary>
    static double BlockEpochDurationSec = 20.0;

    /// <summary>分析模式: stimulus=单个被试, expert=专家全集epoch, ensemble=论文方法, surrogate=显著性检验</summary>
    static string AnalysisMode = "stimulus";

    /// <summary>是否启用 surrogate 显著性检验（ensemble 模式下可选）</summary>
    static bool EnableSurrogate = false;

    /// <summary>是否批量处理所有 expert 被试（stimulus 模式）</summary>
    static bool AllExpert = false;

    /// <summary>外部数据集根目录（ds003969 等）</summary>
    static string ExternalDataRoot = "";

    /// <summary>是否分析外部数据集</summary>
    static bool AnalyzeExternalDs = false;

    /// <summary>是否批量处理所有外部数据集被试</summary>
    static bool AllExternal = false;

    /// <summary>通用分析模式：是否为 generic 模式</summary>
    static bool GenericMode = false;

    /// <summary>直接指定 BDF 文件路径（通用模式）</summary>
    static string DirectBdfPath = "";

    /// <summary>是否批量处理所有被试（通用模式）</summary>
    static bool GenericAll = false;

    static async Task Main(string[] args)
    {
        try
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║     EEG 双谱/双相干性 分析程序 (Bicoherence)       ║");
            Console.WriteLine("║     Build: 2026-08-06 17:03                         ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // 解析命令行参数
            ParseCommandLine(args);

            // 交互模式：命令行中没有任何 - 开头的参数 → 显示菜单
            bool interactiveMode = !args.Any(a => a.StartsWith("-"));
            if (interactiveMode)
            {
                PrintHelp();
                Console.WriteLine();
                Console.WriteLine("═══ 快速操作 ═══");
                Console.WriteLine("  [V] 运行算法验证 (--mode validate)");
                Console.WriteLine("  [G] 通用 EEG 分析 (--mode generic --bdf <文件>)");
                Console.WriteLine("  [E] 达人批处理 (--all-expert, 需 BIDS 数据 + events.tsv)");
                Console.WriteLine("  [Q] 退出");
                Console.Write("请选择: ");
                var key = Console.ReadKey(true);
                Console.WriteLine();
                if (key.Key == ConsoleKey.V)
                {
                    AnalysisMode = "validate";
                    RunValidation();
                }
                else if (key.Key == ConsoleKey.G)
                {
                    Console.Write("请输入 BDF/EDF 文件路径: ");
                    DirectBdfPath = (Console.ReadLine() ?? "").Trim().Trim('"');  // 去掉引号和首尾空格
                    if (!string.IsNullOrEmpty(DirectBdfPath) && File.Exists(DirectBdfPath))
                    {
                        GenericMode = true;
                        await RunGenericAnalysis();
                    }
                    else
                        Console.WriteLine($"文件不存在: \"{DirectBdfPath}\"，已取消。");
                }
                else if (key.Key == ConsoleKey.E)
                {
                    Console.Write("请输入数据根目录 (含 participants.tsv 和 sub-xxx/ 子目录): ");
                    string datadir = (Console.ReadLine() ?? "").Trim().Trim('"');  // 去掉引号和首尾空格
                    if (!string.IsNullOrEmpty(datadir) && !Directory.Exists(datadir))
                    {
                        Console.WriteLine($"目录不存在: {datadir}，已取消。");
                    }
                    else if (!string.IsNullOrEmpty(datadir))
                    {
                        DataRoot = datadir;
                        ResultsRoot = Path.Combine(datadir, "results");
                        AllExpert = true;
                        Console.WriteLine($"数据目录: {DataRoot}");
                        Console.WriteLine($"输出目录: {ResultsRoot}");
                        await RunAllExpert();
                    }
                    else
                        Console.WriteLine("已取消。");
                }
                else
                {
                    Console.WriteLine("已退出。");
                }
                Console.WriteLine();
                Console.Write("按任意键关闭窗口...");
                Console.ReadKey(true);
                return;
            }

            var stopwatch = Stopwatch.StartNew();

        try
        {
            if (AnalyzeExternalDs && !string.IsNullOrEmpty(ExternalDataRoot))
            {
                if (AllExternal)
                    await RunAllExternalSubjects();
                else
                    await RunBlockEpochAnalysis(SubjectId);
            }
            else if (GenericMode)
            {
                if (GenericAll)
                    await RunGenericBatch();
                else
                    await RunGenericAnalysis();
            }
            else if (AnalysisMode == "ensemble")
                await RunEnsembleAveraging();
            else if (AnalysisMode == "validate")
                RunValidation();
            else if (AllExpert)
                await RunAllExpert();
            else
            {
                var rows = await RunAnalysis();
                WriteExpertEpochsCsv(rows);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[错误] 分析过程中发生异常:");
            Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  {ex.StackTrace}");
            return;
        }

        stopwatch.Stop();
        Console.WriteLine($"\n══════════════════════════════════════════════");
        Console.WriteLine($"  分析完成！总耗时: {stopwatch.Elapsed.TotalMinutes:F1} 分钟");
        Console.WriteLine($"  结果保存在: {ResultsRoot}");
        Console.WriteLine($"══════════════════════════════════════════════");
    }  // end outer try
    catch (Exception ex)
    {
        Console.WriteLine($"\n[严重错误] 程序启动失败:");
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"  {ex.StackTrace}");
        Console.WriteLine();
        Console.Write("按任意键关闭窗口...");
        Console.ReadKey(true);
    }
}

    static async Task<List<Dictionary<string, object>>> RunAnalysis()
    {
        // === 步骤1: 定位数据文件 ===
        Console.WriteLine("[步骤1] 定位数据文件...");
        string subjectDir = Path.Combine(DataRoot, SubjectId);

        if (!Directory.Exists(subjectDir))
            throw new DirectoryNotFoundException($"被试目录不存在: {subjectDir}");

        string[] sessions = Directory.GetDirectories(subjectDir, "ses-*");
        if (sessions.Length == 0)
            throw new DirectoryNotFoundException($"未找到session目录: {subjectDir}\\ses-*");

        string selectedSession = SessionId;
        if (!sessions.Any(s => Path.GetFileName(s) == selectedSession))
            selectedSession = Path.GetFileName(sessions[0]);

        string eegDir = Path.Combine(subjectDir, selectedSession, "eeg");
        if (!Directory.Exists(eegDir))
            throw new DirectoryNotFoundException($"EEG目录不存在: {eegDir}");

        string[] bdfFiles = Directory.GetFiles(eegDir, "*_eeg.bdf");
        if (bdfFiles.Length == 0)
            throw new FileNotFoundException($"未找到BDF文件: {eegDir}\\*_eeg.bdf");
        string bdfPath = bdfFiles[0];

        string[] tsvFiles = Directory.GetFiles(eegDir, "*_events.tsv");
        if (tsvFiles.Length == 0)
            throw new FileNotFoundException($"未找到事件文件: {eegDir}\\*_events.tsv");
        string eventsPath = tsvFiles[0];

        Console.WriteLine($"  被试: {SubjectId}, 会话: {selectedSession}");
        Console.WriteLine($"  BDF: {Path.GetFileName(bdfPath)}");
        Console.WriteLine($"  事件: {Path.GetFileName(eventsPath)}");

        // === 步骤2: 读取BDF + 通道映射 ===
        Console.WriteLine("\n[步骤2] 读取BDF和通道映射...");
        using var bdfReader = new BdfReader(bdfPath);
        string[] bdfLabels = bdfReader.GetChannelNames();
        Console.WriteLine($"  BDF通道数: {bdfLabels.Length}");

        string channelsTsvPath = Path.Combine(DataRoot, "task-meditation_channels.tsv");
        string[] standardNames = ReadChannelNamesFromTsv(channelsTsvPath);
        Console.WriteLine($"  TSV通道数: {standardNames.Length}");

        var channelIndices = new Dictionary<string, int>();
        for (int i = 0; i < standardNames.Length && i < bdfLabels.Length; i++)
        {
            string standardName = standardNames[i];
            if (TargetChannels.Contains(standardName, StringComparer.OrdinalIgnoreCase))
            {
                channelIndices[standardName] = i;
                Console.WriteLine($"  ✓ {standardName} → BDF[{i}]");
            }
        }
        foreach (string chName in TargetChannels)
            if (!channelIndices.ContainsKey(chName))
                Console.WriteLine($"  ✗ {chName} 未找到");

        if (channelIndices.Count == 0)
            throw new InvalidOperationException("没有找到任何目标通道");

        double totalDuration = bdfReader.NumDataRecords * bdfReader.RecordDuration;
        Console.WriteLine($"  总时长: {totalDuration:F0}s ({totalDuration / 60:F1}min)");

        // === 步骤3: 读取事件并提取 stimulus 前时间窗（含 Q1/Q2/Q3 响应匹配） ===
        Console.WriteLine($"\n[步骤3] 提取 stimulus 前 {PreStimulusSec}s 时间窗...");
        var eventReader = new EventReader(eventsPath);

        var stimEvents = eventReader.Events.Where(e => e.TrialType == "stimulus").OrderBy(e => e.Onset).ToList();
        var respEvents = eventReader.Events.Where(e => e.TrialType == "response").OrderBy(e => e.Onset).ToList();
        Console.WriteLine($"  stimulus: {stimEvents.Count}, response: {respEvents.Count}");

        // 提取 stimulus 前时间窗（segment 已内置 Q1/Q2/Q3 响应值，按 onset 窗口匹配）
        var preStimSegments = eventReader.ExtractPreStimulusSegments(PreStimulusSec);
        Console.WriteLine($"  有效 stimulus 段: {preStimSegments.Count}");

        // === 步骤4: 按 Q1 冥想深度响应值分组 ===
        // 每个 stimulus（value=128）都是一个 Q1 探头；
        // Q2/Q3 无独立 stimulus 标记，其响应由按键后自动触发音频
        Console.WriteLine("\n[步骤4] 匹配 stimulus → response 并按 Q1 值分组:");
        for (int i = 0; i < preStimSegments.Count; i++)
        {
            var seg = preStimSegments[i];
            Console.WriteLine($"  epoch #{i} @ {seg.StimulusOnset:F1}s → Q1={seg.Q1Response}, Q2={seg.Q2Response}, Q3={seg.Q3Response}");
        }

        // 按 Q1 值分组（2=浅度冥想, 4=中度冥想, 8=深度冥想）
        var q1Values = preStimSegments
            .Select(s => s.Q1Response)
            .Where(v => v > 0)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        var groupedSegments = q1Values.ToDictionary(
            q1v => q1v,
            q1v => preStimSegments.Where(s => s.Q1Response == q1v).ToList());

        Console.WriteLine("\n  分组统计（按 Q1 冥想深度）:");
        foreach (var kv in groupedSegments.OrderBy(kv2 => kv2.Key))
        {
            Console.WriteLine($"    {Q1DepthLabel(kv.Key)}: {kv.Value.Count} 个 epoch");
        }

        // === 步骤5: 创建结果目录 ===
        string resultDir = Path.Combine(ResultsRoot, SubjectId, "stimulus_analysis");
        Directory.CreateDirectory(resultDir);
        string epochPngDir = Path.Combine(ResultsRoot, "expert_epochs", $"{SubjectId}_{selectedSession}");
        Directory.CreateDirectory(epochPngDir);

        // === 步骤6: 初始化计算器（Stimulus 模式用 256 窗 / 128 步） ===
        var calculator = new BispectrumCalculator(SampleRate, Nfft, Noverlap, MaxFrequency);
        calculator.SetWindowParams(256, 128);  // 小窗长，更多段 = 更高统计可靠性
        var visualizer = new Visualizer();
        var extractor = new FeatureExtractor();
        var epochMetricsRows = new List<Dictionary<string, object>>();

        // === 步骤5: 对每个通道、每个 stimulus 段计算 bicoherence ===
        Console.WriteLine("\n[步骤5] 计算双谱/双相干性（stimulus × 10s 模式）...");
        Console.WriteLine(new string('═', 60));

        foreach (var kvCh in channelIndices)
        {
            var chName = kvCh.Key; var chIdx = kvCh.Value;
            Console.WriteLine($"\n  ▶ 通道: {chName}");

            // ── 计算每个 stimulus 段 ──
            var stimResults = new List<(EventReader.PreStimulusSegment Seg, BispectrumResult Result)>();

            foreach (var seg in preStimSegments)
            {
                Console.WriteLine($"    ▶ Stim#{seg.StimulusIndex} @ {seg.StimulusOnset:F1}s [{seg.StartTimeSec:F1}s–{seg.EndTimeSec:F1}s]");

                double[] eegData = bdfReader.ReadChannelDataByTime(chIdx, seg.StartTimeSec, seg.DurationSec, SampleRate);
                if (eegData.Length < calculator.Nfft)
                {
                    Console.WriteLine($"      ⚠ 数据不足 ({eegData.Length}点)，跳过");
                    continue;
                }

                // 去直流偏置，防止残留 DC 污染低频双相干 (peak_diag 恒为 100)
                {
                    double mean = eegData.Average();
                    for (int si = 0; si < eegData.Length; si++) eegData[si] -= mean;
                }

                // 带通滤波（1–45 Hz Butterworth, SeeSharpTools Filter1D）
                eegData = EegFilter.Filter(eegData);

                var result = calculator.Compute(eegData);
                Console.WriteLine($"      → {result.NumSegments} 段, 分辨率 {result.FreqResolution:F2}Hz");
                stimResults.Add((seg, result));

                // 保存逐 epoch 热力图（Jet colormap, 0-100% 范围，带坐标轴和 colorbar）
                string epochPngPath = Path.Combine(epochPngDir,
                    $"Epoch_{seg.StimulusIndex:D2}_Q1_{seg.Q1Response}_Q2_{seg.Q2Response}_CH_{chName}.png");
                string epochLabel = $"Epoch {seg.StimulusIndex} (Q1={seg.Q1Response}, Q2={seg.Q2Response})";
                visualizer.SaveBicoherenceHeatmap(result, epochPngPath, chName, epochLabel,
                    DisplayMaxFreq, colorMin: 0, colorMax: 100);

                // 提取该 epoch 的双相干指标（含跨频段耦合），写入 per-epoch CSV
                var epochMetrics = ComputeBandMetrics(result);
                var row = new Dictionary<string, object>
                {
                    ["subject"] = SubjectId,
                    ["session"] = selectedSession,
                    ["channel"] = chName,
                    ["epoch_id"] = seg.StimulusIndex,
                    ["Q1"] = seg.Q1Response,
                    ["Q2"] = seg.Q2Response,
                    ["Q3"] = seg.Q3Response,
                };
                foreach (var kvM in epochMetrics) row[kvM.Key] = kvM.Value;
                epochMetricsRows.Add(row);
            }

            if (stimResults.Count == 0)
            {
                Console.WriteLine($"  ⚠ 通道 {chName} 无有效数据，跳过");
                continue;
            }

            // ── 按 Q1 冥想深度分组并计算组平均 ──
            Console.WriteLine($"\n  ── 按 Q1 冥想深度分组平均 ──");
                        
            foreach (var kvG1 in groupedSegments.OrderBy(kv => kv.Key))
            {
                var q1Val = kvG1.Key; var segs = kvG1.Value;
                string qName = Q1DepthLabel(q1Val);
                var groupResults = stimResults
                    .Where(sr => sr.Seg.Q1Response == q1Val)
                    .Select(sr => sr.Result)
                    .ToList();
            
                if (groupResults.Count == 0)
                {
                    Console.WriteLine($"    {qName}: 无结果");
                    continue;
                }
            
                Console.WriteLine($"    {qName}: {groupResults.Count} 个有效段");
            
                // 计算组平均 Bicoherence
                var avgResult = AverageBicoherenceResults(groupResults, calculator);
            
                // 生成组平均热图
                string heatmapPath = Path.Combine(resultDir,
                    $"bicoherence_{SubjectId}_{chName}_{qName}.png");
                visualizer.SaveBicoherenceHeatmap(avgResult, heatmapPath, chName,
                    $"{qName} ({groupResults.Count} stimuli)", DisplayMaxFreq);
            
                // 生成组平均 Bispectrum 幅度
                string bispecPath = Path.Combine(resultDir,
                    $"bispectrum_{SubjectId}_{chName}_{qName}.png");
                visualizer.SaveBispectrumMagnitudeHeatmap(avgResult, bispecPath, chName,
                    qName, DisplayMaxFreq);
            
                // 对角线图
                string diagPath = Path.Combine(resultDir,
                    $"diagonal_{SubjectId}_{chName}_{qName}.png");
                visualizer.SaveBispectrumDiagonalPlot(avgResult, diagPath, chName,
                    qName, DisplayMaxFreq);
            
                // 特征提取（使用 SeeSharpTools Statistics）
                var (diagFreqs, diagValues) = avgResult.GetDiagonalBicoherence();
                var features = extractor.ExtractFeatures(diagFreqs, diagValues);
                FeatureExtractor.PrintFeatureSummary(chName, qName, features);
            
                var stats = FeatureExtractor.GetDiagonalStats(diagValues);
                Console.WriteLine($"    [Statistics] Mean={stats.Mean * 100:F2}%, Std={stats.Std * 100:F2}%, Max={stats.Max * 100:F2}%");
            }
            
            // ── 组间对比图 ──
            var groupResultsList = new List<(string Label, BispectrumResult Result)>();
            foreach (var kvG2 in groupedSegments.OrderBy(kv => kv.Key))
            {
                var q1Val = kvG2.Key;
                var groupResults = stimResults
                    .Where(sr => sr.Seg.Q1Response == q1Val)
                    .Select(sr => sr.Result)
                    .ToList();
            
                if (groupResults.Count > 0)
                {
                    var avg = AverageBicoherenceResults(groupResults, calculator);
                    groupResultsList.Add((Q1DepthLabel(q1Val), avg));
                }
            }

            if (groupResultsList.Count >= 2)
            {
                var groupCompareResults = groupResultsList.Select(g => g.Result).ToList();
                string comparePath = Path.Combine(resultDir,
                    $"bicoherence_groupCompare_{SubjectId}_{chName}.png");
                visualizer.SaveNGroupComparisonFigure(groupCompareResults,
                    groupResultsList.Select(g => g.Label).ToList(),
                    comparePath, chName, DisplayMaxFreq);
                Console.WriteLine($"  ✓ 组间对比图: {Path.GetFileName(comparePath)}");
            }

            // ── 使用 Bispecrtum 对照验证（首个 stimulus 段）──
            if (stimResults.Count > 0)
            {
                var firstSeg = preStimSegments[0];
                double[] firstEeg = bdfReader.ReadChannelDataByTime(chIdx, firstSeg.StartTimeSec, firstSeg.DurationSec, SampleRate);
                if (firstEeg.Length >= calculator.Nfft)
                {
                    { double mean = firstEeg.Average(); for (int si = 0; si < firstEeg.Length; si++) firstEeg[si] -= mean; }
                    firstEeg = EegFilter.Filter(firstEeg);
                    var (bispecBispecrtum, freqBins) = calculator.ComputeWithBispecrtum(firstEeg);
                    Console.WriteLine($"  [Bispecrtum对照] 首个刺激段: 双谱矩阵 {bispecBispecrtum.GetLength(0)}×{bispecBispecrtum.GetLength(1)}");
                }
            }
        }

        // === 步骤6: 最终摘要 ===
        Console.WriteLine("\n" + new string('═', 60));
        Console.WriteLine("  分析摘要");
        Console.WriteLine($"  被试: {SubjectId}, 会话: {selectedSession}");
        Console.WriteLine($"  Stimulus 前窗口: {PreStimulusSec}s (每个 Q1 探头前)");
        Console.WriteLine($"  有效 stimulus 段: {preStimSegments.Count}");
        Console.WriteLine($"  分组数: {groupedSegments.Count} (按 Q1 冥想深度: [{string.Join(", ", q1Values)}])");
        Console.WriteLine($"  分析通道: {string.Join(", ", channelIndices.Keys)}");
        Console.WriteLine($"  结果目录: {resultDir}");
        Console.WriteLine(new string('═', 60));

        return epochMetricsRows;
    }

    /// <summary>
    /// 批量处理所有 expert 被试，汇总 per-epoch 双相干指标到单一 CSV
    /// 用法: dotnet run -- --all-expert
    /// </summary>
    static async Task RunAllExpert()
    {
        string participantsPath = Path.Combine(DataRoot, "participants.tsv");
        if (!File.Exists(participantsPath))
            throw new FileNotFoundException($"participants.tsv 未找到: {participantsPath}");

        var experts = new List<string>();
        string[] pLines = File.ReadAllLines(participantsPath);
        for (int i = 1; i < pLines.Length; i++)
        {
            string[] parts = pLines[i].Split('\t');
            if (parts.Length >= 4 && parts[3].Trim() == "expert")
                experts.Add(parts[0].Trim());
        }
        Console.WriteLine($"\nExpert 被试 ({experts.Count}): {string.Join(", ", experts)}\n");

        var allRows = new List<Dictionary<string, object>>();
        string savedSubject = SubjectId;
        string savedSession = SessionId;
        var swTotal = Stopwatch.StartNew();

        foreach (string subId in experts)
        {
            SubjectId = subId;
            string subjectDir = Path.Combine(DataRoot, subId);
            if (!Directory.Exists(subjectDir))
            {
                Console.WriteLine($"  [SKIP] {subId}: 目录不存在");
                continue;
            }
            string[] sessions = Directory.GetDirectories(subjectDir, "ses-*");
            if (sessions.Length == 0)
            {
                Console.WriteLine($"  [SKIP] {subId}: 无 session");
                continue;
            }

            foreach (string sesDir in sessions)
            {
                SessionId = Path.GetFileName(sesDir);
                try
                {
                    var rows = await RunAnalysis();
                    allRows.AddRange(rows);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [ERROR] {subId}/{SessionId}: {ex.Message}");
                }
            }
        }

        SubjectId = savedSubject;
        SessionId = savedSession;
        swTotal.Stop();

        WriteExpertEpochsCsv(allRows);
        Console.WriteLine($"\n  全部专家批处理完成！总耗时: {swTotal.Elapsed.TotalMinutes:F1} 分钟");
    }

    /// <summary>将 per-epoch 指标写入 expert_epochs 目录下的统一 CSV</summary>
    static void WriteExpertEpochsCsv(List<Dictionary<string, object>> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("  ⚠ 无 per-epoch 数据，跳过 CSV 输出");
            return;
        }
        string expertEpochsDir = Path.Combine(ResultsRoot, "expert_epochs");
        Directory.CreateDirectory(expertEpochsDir);
        string csvPath = Path.Combine(expertEpochsDir, "expert_epochs_metrics.csv");
        WriteMetricsCsv(csvPath, rows);
        Console.WriteLine($"\n  📊 Per-epoch 指标 CSV: {csvPath} ({rows.Count} 行)");
    }

    /// <summary>
    /// 解析命令行参数
    /// 用法: dotnet run -- [--subject sub-001] [--session ses-01] [--datadir path]
    /// </summary>
    static void ParseCommandLine(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--subject":
                case "-s":
                    if (i + 1 < args.Length) SubjectId = args[++i];
                    break;
                case "--session":
                case "-ses":
                    if (i + 1 < args.Length) SessionId = args[++i];
                    break;
                case "--datadir":
                case "-d":
                    if (i + 1 < args.Length)
                    {
                        DataRoot = args[++i];
                        ResultsRoot = Path.Combine(DataRoot, "results");
                    }
                    break;
                case "--mode":
                case "-m":
                    if (i + 1 < args.Length) { AnalysisMode = args[++i].ToLower(); if (AnalysisMode == "generic") GenericMode = true; }
                    break;
                case "--surrogate":
                    EnableSurrogate = true;
                    break;
                case "--all-expert":
                    AllExpert = true;
                    break;
                case "--external-ds":
                case "-e":
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        ExternalDataRoot = args[++i];
                        AnalyzeExternalDs = true;
                    }
                    else
                    {
                        AnalyzeExternalDs = true;  // 无参数时视为标记
                    }
                    break;
                case "--all-external":
                    AllExternal = true;
                    AnalyzeExternalDs = true;
                    break;
                case "--bdf":
                    if (i + 1 < args.Length) DirectBdfPath = args[++i];
                    break;
                case "--all":
                    GenericAll = true;
                    GenericMode = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    break;
            }
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("用法: dotnet run -- [选项]");
        Console.WriteLine("选项:");
        Console.WriteLine("  --subject, -s  <ID>     被试ID (默认: sub-001)");
        Console.WriteLine("  --session, -ses <ID>    会话ID (默认: 自动检测)");
        Console.WriteLine("  --datadir, -d  <path>   数据根目录 (默认: e:\\数据集)");
        Console.WriteLine("  --mode, -m  <mode>      分析模式: stimulus(默认) | ensemble | validate | generic(通用EEG)");
        Console.WriteLine("  --surrogate              启用 surrogate 显著性检验");
        Console.WriteLine("  --all-expert             批量处理所有 expert 被试（per-epoch CSV）");
        Console.WriteLine("  --external-ds, -e <path> 外部数据集根目录（如 ds003969 块设计数据）");
        Console.WriteLine("  --all-external           批量处理外部数据集全部被试");
        Console.WriteLine("  --help, -h               显示帮助");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  dotnet run -- --subject sub-002");
        Console.WriteLine("  dotnet run -- --subject sub-001 --session ses-02");
        Console.WriteLine("  dotnet run -- --mode ensemble        # Ensemble Averaging（论文方法）");
        Console.WriteLine("  dotnet run -- --all-expert           # 批量处理所有 expert 被试");
        Console.WriteLine("  dotnet run -- -e external_data/ds003969 --subject sub-001  # 外部块设计数据");
        Console.WriteLine("  dotnet run -- --mode generic --bdf D:\\data\\recording.bdf   # 通用模式，指定文件");
        Console.WriteLine("  dotnet run -- --mode generic --datadir D:\\data --all        # 通用模式，批处理");
    }

    /// <summary>
    /// 从_channels.tsv文件中读取标准通道名称列表（使用 CsvHandler）
    /// </summary>
    static string[] ReadChannelNamesFromTsv(string tsvPath)
    {
        if (!File.Exists(tsvPath))
            throw new FileNotFoundException($"通道TSV文件未找到: {tsvPath}");

        var names = new List<string>();
        string[] lines = File.ReadAllLines(tsvPath);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            string[] parts = line.Split('\t');
            if (parts.Length > 0) names.Add(parts[0].Trim());
        }
        return names.ToArray();
    }

    /// <summary>
    /// 将多个 stimulus 段的 BispectrumResult 求平均
    /// 对 Bicoherence 矩阵逐元素取平均（比 averaging raw bispectrum 更稳定）
    /// </summary>
    static BispectrumResult AverageBicoherenceResults(
        List<BispectrumResult> results, BispectrumCalculator calculator)
    {
        if (results.Count == 1) return results[0];

        int nFreq = results[0].Bicoherence.GetLength(0);

        Complex[,] avgBispectrum = new Complex[nFreq, nFreq];
        double[,] avgBicoherence = new double[nFreq, nFreq];
        double[,] avgMagnitude = new double[nFreq, nFreq];

        int count = results.Count;
        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) continue;

                double bicoSum = 0, magSum = 0;
                Complex bispecSum = Complex.Zero;

                foreach (var r in results)
                {
                    bicoSum += r.Bicoherence[f1, f2];
                    magSum += r.BispectrumMagnitude[f1, f2];
                    bispecSum += r.Bispectrum[f1, f2];
                }

                avgBicoherence[f1, f2] = bicoSum / count;
                avgMagnitude[f1, f2] = magSum / count;
                avgBispectrum[f1, f2] = bispecSum / count;
            }
        }

        return new BispectrumResult
        {
            Bispectrum = avgBispectrum,
            BispectrumMagnitude = avgMagnitude,
            Bicoherence = avgBicoherence,
            Frequencies = results[0].Frequencies,
            FreqResolution = results[0].FreqResolution,
            MaxFreqIndex = results[0].MaxFreqIndex,
            NumSegments = results.Sum(r => r.NumSegments)
        };
    }

    /// <summary>Q1 冥想深度值 → 可读标签</summary>
    static string Q1DepthLabel(int q1) => q1 switch
    {
        2 => "Q1=2 (浅度冥想)",
        4 => "Q1=4 (中度冥想)",
        8 => "Q1=8 (深度冥想)",
        _ => $"Q1={q1}"
    };

    static string GetStateDisplayName(string state) => state;

    // ═══════════════════════════════════════════════════════════
    //  验证模式：算法正确性检查
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 运行算法验证（合成信号 + Bispecrtum 对比 + shuffle 零假设）
    /// 用法: dotnet run -- --mode validate
    /// </summary>
    static void RunValidation()
    {
        ValidationRunner.RunAll(SampleRate);
    }

    // ═══════════════════════════════════════════════════════════
    //  Ensemble Averaging：跨 epoch 平均双谱后计算 bicoherence
    //  论文依据: Tacchino et al. (2020)
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Ensemble Averaging 模式：按 Q1 分组，将同条件下所有 epoch 的
    /// 复数双谱累加后统一计算 bicoherence。
    /// 
    /// 与 per-epoch 模式的关键区别：
    /// - Per-epoch: 每个 epoch 独立计算 bicoherence（17 段），然后平均 b²
    /// - Ensemble:  所有 epoch 的复数 bispectrum 先求和，再算 b²（有效段数 = N×17）
    /// 
    /// 输出：每 Q1 组 × 通道生成 ensemble bicoherence 热图 + 指标 CSV
    /// </summary>
    static async Task RunEnsembleAveraging()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Ensemble Averaging 双相干性分析 (论文方法)        ║");
        Console.WriteLine("║   跨 epoch 平均双谱 → 计算 bicoherence              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");

        // 1. 读取被试列表
        string participantsPath = Path.Combine(DataRoot, "participants.tsv");
        if (!File.Exists(participantsPath))
            throw new FileNotFoundException($"participants.tsv 未找到: {participantsPath}");

        var experts = new List<string>();
        string[] pLines = File.ReadAllLines(participantsPath);
        for (int i = 1; i < pLines.Length; i++)
        {
            string[] parts = pLines[i].Split('\t');
            if (parts.Length >= 4 && parts[3].Trim() == "expert")
                experts.Add(parts[0].Trim());
        }
        Console.WriteLine($"Expert 被试 ({experts.Count}): {string.Join(", ", experts)}\n");

        string ensembleRoot = Path.Combine(ResultsRoot, "ensemble_averaging");
        var visualizer = new Visualizer();
        var allMetricsRows = new List<Dictionary<string, object>>();
        var swTotal = Stopwatch.StartNew();

        // 2. 逐被试处理
        foreach (string subId in experts)
        {
            string subjectDir = Path.Combine(DataRoot, subId);
            if (!Directory.Exists(subjectDir)) continue;

            string[] sessions = Directory.GetDirectories(subjectDir, "ses-*");
            foreach (string sesDir in sessions)
            {
                string sesId = Path.GetFileName(sesDir);
                string eegDir = Path.Combine(sesDir, "eeg");
                if (!Directory.Exists(eegDir)) continue;

                string[] bdfFiles = Directory.GetFiles(eegDir, "*_eeg.bdf");
                string[] tsvFiles = Directory.GetFiles(eegDir, "*_events.tsv");
                if (bdfFiles.Length == 0 || tsvFiles.Length == 0) continue;

                Console.WriteLine($"\n{'═',60}");
                Console.WriteLine($"  {subId} / {sesId}");
                Console.WriteLine($"{'═',60}");

                using var bdfReader = new BdfReader(bdfFiles[0]);
                var eventReader = new EventReader(tsvFiles[0]);
                var epochs = eventReader.ExtractQ1EpochsWithResponses(PreStimulusSec);
                Console.WriteLine($"  Q1 epochs: {epochs.Count}");

                // 通道映射
                string channelsTsvPath = Path.Combine(DataRoot, "task-meditation_channels.tsv");
                string[] standardNames = ReadChannelNamesFromTsv(channelsTsvPath);
                var chMap = new Dictionary<string, int>();
                string[] bdfLabels = bdfReader.GetChannelNames();
                for (int c = 0; c < standardNames.Length && c < bdfLabels.Length; c++)
                    if (TargetChannels.Contains(standardNames[c], StringComparer.OrdinalIgnoreCase))
                        chMap[standardNames[c]] = c;

                // EOG 通道用于伪迹检测
                var eogIndices = ArtifactDetector.FindEogChannelIndices(standardNames);

                // 初始化计算器（256 窗 / 128 步）
                var calculator = new BispectrumCalculator(SampleRate, 256, 128, MaxFrequency);
                var psdCalc = new PowerSpectrumCalculator(SampleRate, 256, 128);

                string subjectOutDir = Path.Combine(ensembleRoot, $"{subId}_{sesId}");
                Directory.CreateDirectory(subjectOutDir);

                var swSubj = Stopwatch.StartNew();
                var sessionRows = new List<Dictionary<string, object>>();

                foreach (var chKv in chMap)
                {
                    var chName = chKv.Key; var chIdx = chKv.Value;

                    // 按 Q1 分组累加器
                    var q1Accumulators = new Dictionary<int, BispectrumAccumulator>();
                    var q1PsdAccum = new Dictionary<int, (double[] freqs, List<double[]> psdList)>();
                    var q1EpochCounts = new Dictionary<int, int>();
                    var q1FirstEeg = new Dictionary<int, double[]>(); // 用于 surrogate 检验
                    int rejected = 0, total = 0;

                    foreach (var (seg, q1Val, q2Val) in epochs)
                    {
                        total++;
                        double[] eegData = bdfReader.ReadChannelDataByTime(
                            chIdx, seg.StartTimeSec, seg.DurationSec, SampleRate);
                        if (eegData.Length < 256) continue;

                        // 去 DC
                        double eegMean = eegData.Average();
                        for (int i = 0; i < eegData.Length; i++) eegData[i] -= eegMean;
                        eegData = EegFilter.Filter(eegData);

                        // 伪迹检测
                        if (eogIndices.Count > 0)
                        {
                            var eogDataList = new List<double[]>();
                            foreach (int eogIdx in eogIndices)
                            {
                                double[] eogData = bdfReader.ReadChannelDataByTime(
                                    eogIdx, seg.StartTimeSec, seg.DurationSec, SampleRate);
                                if (eogData.Length > 0)
                                {
                                    double em = eogData.Average();
                                    for (int i = 0; i < eogData.Length; i++) eogData[i] -= em;
                                    eogData = EegFilter.Filter(eogData);
                                }
                                eogDataList.Add(eogData.Length > 0 ? eogData : Array.Empty<double>());
                            }
                            var (isClean, _) = ArtifactDetector.CheckEpochCombined(eogDataList, eegData);
                            if (!isClean) { rejected++; continue; }
                        }
                        else
                        {
                            var (isClean, _) = ArtifactDetector.CheckEegOnly(eegData);
                            if (!isClean) { rejected++; continue; }
                        }

                        // 累加双谱
                        try
                        {
                            var accum = calculator.ComputeAccumulators(eegData);
                            if (!q1Accumulators.ContainsKey(q1Val))
                            {
                                q1Accumulators[q1Val] = accum;
                                q1FirstEeg[q1Val] = (double[])eegData.Clone(); // 保存首个 epoch 用于 surrogate
                            }
                            else
                                q1Accumulators[q1Val].Merge(accum);

                            // 累加 PSD（用于后续 SNR 评估）
                            var (psdF, psdV) = psdCalc.Compute(eegData);
                            if (!q1PsdAccum.ContainsKey(q1Val))
                                q1PsdAccum[q1Val] = (psdF, new List<double[]> { psdV });
                            else
                                q1PsdAccum[q1Val].psdList.Add(psdV);

                            q1EpochCounts[q1Val] = q1EpochCounts.TryGetValue(q1Val, out int c) ? c + 1 : 1;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    ⚠ 计算失败 epoch#{seg.StimulusIndex}: {ex.Message}");
                        }
                    }

                    double rejectPct = total > 0 ? 100.0 * rejected / total : 0;
                    Console.WriteLine($"  [{chName}] {q1EpochCounts.Values.Sum()} 个 clean epoch ({rejectPct:F1}% 剔除)");

                    // 对每个 Q1 组生成 ensemble bicoherence
                    foreach (var kvQ1 in q1Accumulators.OrderBy(kv => kv.Key))
                    {
                        int q1Val = kvQ1.Key;
                        var accum = kvQ1.Value;
                        int effectiveSegs = accum.AddedSegments;

                        var result = accum.ToBicoherenceResult();
                        Console.WriteLine($"    Q1={q1Val}: {q1EpochCounts[q1Val]} epochs × ~17 段 = {effectiveSegs} 有效段");

                        // 提取指标
                        var metrics = ComputeBandMetrics(result);
                        var row = new Dictionary<string, object>
                        {
                            ["subject"] = subId,
                            ["session"] = sesId,
                            ["Q1"] = q1Val,
                            ["channel"] = chName,
                            ["n_epochs"] = q1EpochCounts[q1Val],
                            ["n_segments"] = effectiveSegs,
                        };
                        foreach (var kvM in metrics) row[kvM.Key] = kvM.Value;

                        // PSD / SNR（跨 epoch 平均）
                        if (q1PsdAccum.TryGetValue(q1Val, out var psdAcc) && psdAcc.psdList.Count > 0)
                        {
                            double[] avgPsd = new double[psdAcc.freqs.Length];
                            foreach (var p in psdAcc.psdList)
                                for (int i = 0; i < avgPsd.Length && i < p.Length; i++)
                                    avgPsd[i] += p[i];
                            for (int i = 0; i < avgPsd.Length; i++)
                                avgPsd[i] /= psdAcc.psdList.Count;

                            var bandPowers = PowerSpectrumCalculator.ExtractAllBandPowers(psdAcc.freqs, avgPsd);
                            var bandSnrs = PowerSpectrumCalculator.EstimateAllBandSnrs(psdAcc.freqs, avgPsd);
                            foreach (var kvP in bandPowers) row[kvP.Key] = kvP.Value;
                            foreach (var kvS in bandSnrs) row[kvS.Key] = kvS.Value;
                        }

                        sessionRows.Add(row);

                        // 保存热图
                        string qLabel = q1Val switch { 2 => "shallow", 4 => "medium", 8 => "deep", _ => $"q{q1Val}" };
                        string heatmapPath = Path.Combine(subjectOutDir,
                            $"ensemble_bicoherence_{chName}_Q1{qLabel}.png");
                        string title = $"{subId} {sesId} {chName} Q1={q1Val} (ensemble, {effectiveSegs} seg)";
                        visualizer.SaveBicoherenceGrayscale(result, heatmapPath, title, DisplayMaxFreq);

                        // ── Surrogate 显著性检验（可选）──
                        if (EnableSurrogate && q1FirstEeg.TryGetValue(q1Val, out var firstEeg) && firstEeg.Length >= 256)
                        {
                            try
                            {
                                var surrogateTest = new SurrogateTest(calculator);
                                var (threshold, _) = surrogateTest.ComputeSurrogateThreshold(firstEeg,
                                    numSurrogates: 50); // 50 次足够，平衡速度
                                var mask = SurrogateTest.GetSignificanceMask(result.Bicoherence, threshold);
                                double sigPct = SurrogateTest.SignificantFraction(mask);

                                string sigPath = Path.Combine(subjectOutDir,
                                    $"ensemble_significance_{chName}_Q1{qLabel}.png");
                                SurrogateTest.SaveSignificanceMask(mask, result.Frequencies, sigPath,
                                    $"{subId} {sesId} {chName} Q1={q1Val} (sig={sigPct * 100:F1}%)", DisplayMaxFreq);

                                row["sig_fraction"] = Math.Round(sigPct, 4);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"    ⚠ Surrogate 检验失败: {ex.Message}");
                            }
                        }
                    }
                }

                // 写 session CSV
                if (sessionRows.Count > 0)
                {
                    string csvPath = Path.Combine(subjectOutDir, $"{subId}_{sesId}_ensemble_metrics.csv");
                    WriteMetricsCsv(csvPath, sessionRows);
                    allMetricsRows.AddRange(sessionRows);
                }

                swSubj.Stop();
                Console.WriteLine($"  ✓ 完成 {sessionRows.Count} 行指标, 耗时 {swSubj.Elapsed.TotalSeconds:F0}s");
            }
        }

        // 写全局汇总 CSV
        if (allMetricsRows.Count > 0)
        {
            string globalCsvPath = Path.Combine(ensembleRoot, "ensemble_metrics.csv");
            WriteMetricsCsv(globalCsvPath, allMetricsRows);
            Console.WriteLine($"\n  📊 全局指标 CSV: {globalCsvPath} ({allMetricsRows.Count} 行)");
        }

        swTotal.Stop();
        Console.WriteLine($"\n{'═',60}");
        Console.WriteLine($"  全部完成！总耗时: {swTotal.Elapsed.TotalMinutes:F1} 分钟");
        Console.WriteLine($"  输出目录: {ensembleRoot}");
        Console.WriteLine($"{'═',60}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Expert Epoch 分析：专家全集 Q1-only bicoherence 图谱
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    //  外部块设计数据集分析（ds003969: Meditation vs Thinking）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 批量处理外部数据集所有被试。
    /// </summary>
    static async Task RunAllExternalSubjects()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║   外部数据集批量处理 (Batch External Analysis)     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");
        Console.WriteLine($"  数据根目录: {ExternalDataRoot}");

        string[] subjectDirs = Directory.GetDirectories(ExternalDataRoot, "sub-*");
        var subjectIds = subjectDirs
            .Select(d => Path.GetFileName(d))
            .OrderBy(s => int.Parse(s.Replace("sub-", "")))
            .ToList();

        Console.WriteLine($"  发现 {subjectIds.Count} 个被试: {string.Join(", ", subjectIds)}");
        Console.WriteLine();

        var swAll = Stopwatch.StartNew();
        int success = 0, failed = 0;

        for (int i = 0; i < subjectIds.Count; i++)
        {
            string sid = subjectIds[i];
            Console.WriteLine($"\n{'═',60}");
            Console.WriteLine($"  [{i + 1}/{subjectIds.Count}] 处理 {sid}...");
            Console.WriteLine($"{'═',60}");

            try
            {
                var swSub = Stopwatch.StartNew();
                await RunBlockEpochAnalysis(sid);
                swSub.Stop();
                Console.WriteLine($"  ✓ {sid} 完成 ({swSub.Elapsed.TotalSeconds:F0}s)");
                success++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {sid} 失败: {ex.Message}");
                failed++;
            }
        }

        swAll.Stop();
        Console.WriteLine($"\n{'═',60}");
        Console.WriteLine($"  批量处理完成！");
        Console.WriteLine($"  成功: {success}, 失败: {failed}");
        Console.WriteLine($"  总耗时: {swAll.Elapsed.TotalMinutes:F1} 分钟");
        Console.WriteLine($"  输出: {Path.Combine(ResultsRoot, "ds003969_block_analysis")}");
        Console.WriteLine($"{'═',60}");
    }

    /// <summary>
    /// 运行块设计外部数据集分析（如 ds003969）。
    /// 每个任务 BDF 是一个完整块（meditation 或 thinking），块内滑窗提取 20s epoch，
    /// 按任务条件分组计算双相干性。
    /// </summary>
    static async Task RunBlockEpochAnalysis(string subjectId)
    {
        // 批量模式下简化 banner
        if (!AllExternal)
        {
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║   块设计外部数据集分析 (Block Design Analysis)      ║");
            Console.WriteLine("║   块内滑窗 → 按条件分组 → 双相干性对比              ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");
            Console.WriteLine($"  数据根目录: {ExternalDataRoot}");
            Console.WriteLine($"  被试: {subjectId}");
            Console.WriteLine();
        }

        string subjectDir = Path.Combine(ExternalDataRoot, subjectId);
        if (!Directory.Exists(subjectDir))
            throw new DirectoryNotFoundException($"被试目录不存在: {subjectDir}");

        string eegDir = Path.Combine(subjectDir, "eeg");
        if (!Directory.Exists(eegDir))
            throw new DirectoryNotFoundException($"EEG目录不存在: {eegDir}");

        // 发现所有 BDF 文件
        string[] bdfFiles = Directory.GetFiles(eegDir, "*_eeg.bdf");
        Console.WriteLine($"  找到 {bdfFiles.Length} 个 BDF 文件:");
        foreach (string f in bdfFiles)
            Console.WriteLine($"    {Path.GetFileName(f)}");

        if (bdfFiles.Length == 0)
            throw new FileNotFoundException($"未找到BDF文件: {eegDir}\\*_eeg.bdf");

        // 结果目录
        string blockResultDir = Path.Combine(ResultsRoot, "ds003969_block_analysis", subjectId);
        Directory.CreateDirectory(blockResultDir);

        // 通道映射 — 从第一个 BDF 的 channels.tsv 侧车文件推断
        string firstTask = Path.GetFileNameWithoutExtension(bdfFiles[0]).Replace("_eeg", "");
        string channelsTsvCandidate = Path.Combine(eegDir, firstTask + "_channels.tsv");
        string[] standardNames = File.Exists(channelsTsvCandidate)
            ? ReadChannelNamesFromTsv(channelsTsvCandidate)
            : Array.Empty<string>();
        Console.WriteLine($"  标准通道数: {standardNames.Length}");

        var calculator = new BispectrumCalculator(SampleRate, Nfft, Noverlap, MaxFrequency);
        var allMetricsRows = new List<Dictionary<string, object>>();
        var swTotal = Stopwatch.StartNew();

        foreach (string bdfPath in bdfFiles)
        {
            string fileName = Path.GetFileNameWithoutExtension(bdfPath);
            string taskName = fileName.Replace("_eeg", "");
            // 从任务名推断条件：med* → meditation, think* → thinking
            string condition = taskName.ToUpperInvariant().Contains("MED")
                ? "meditation" : "thinking";

            Console.WriteLine($"\n{'─',50}");
            Console.WriteLine($"  ▶ {taskName} ({condition})");

            using var bdfReader = new BdfReader(bdfPath);
            int bdfSampleRate = (int)bdfReader.Channels[0].SamplesPerRecord;
            double totalDur = bdfReader.RecordDuration * bdfReader.NumDataRecords;
            Console.WriteLine($"    采样率: {bdfSampleRate}Hz, 时长: {totalDur:F0}s");
            Console.WriteLine($"    通道: {bdfReader.Channels.Length}");

            // 通道映射
            var chMap = new Dictionary<string, int>();
            string[] bdfLabels = bdfReader.GetChannelNames();
            if (standardNames.Length > 0)
            {
                for (int c = 0; c < standardNames.Length && c < bdfLabels.Length; c++)
                    if (TargetChannels.Contains(standardNames[c], StringComparer.OrdinalIgnoreCase))
                        chMap[standardNames[c]] = c;
            }
            else
            {
                for (int c = 0; c < bdfLabels.Length && c < 64; c++)
                {
                    string lbl = bdfLabels[c];
                    if (TargetChannels.Contains(lbl, StringComparer.OrdinalIgnoreCase))
                        chMap[lbl] = c;
                }
            }
            Console.WriteLine($"    目标通道: {chMap.Count} ({string.Join(", ", chMap.Keys)})");

            // 提取触发事件
            var triggerEvents = bdfReader.ExtractTriggerEvents(bdfSampleRate);
            Console.WriteLine($"    触发事件: {triggerEvents.Count}");
            foreach (var te in triggerEvents.Take(10))
                Console.WriteLine($"      @ {te.OnsetSec:F1}s, value={te.Value}");
            if (triggerEvents.Count > 10)
                Console.WriteLine($"      ... (+{triggerEvents.Count - 10} more)");

            // 块内滑窗提取 epoch
            var blockEpochs = EventReader.ExtractBlockEpochs(triggerEvents, totalDur,
                epochDurationSec: BlockEpochDurationSec, slideStepSec: BlockEpochDurationSec / 2);
            Console.WriteLine($"    滑窗生成 {blockEpochs.Count} 个 {BlockEpochDurationSec}s epoch");

            if (blockEpochs.Count == 0)
            {
                Console.WriteLine($"    ⚠ 无有效 epoch，跳过");
                continue;
            }

            // 对每个通道计算
            foreach (var chKv in chMap)
            {
                string chName = chKv.Key;
                int chIdx = chKv.Value;
                Console.WriteLine($"    [{chName}] {blockEpochs.Count} epochs...");

                foreach (var ep in blockEpochs)
                {
                    double[] eegData = bdfReader.ReadChannelDataByTime(
                        chIdx, ep.StartTimeSec, ep.DurationSec, bdfSampleRate);
                    if (eegData.Length < Nfft) continue;

                    // 如果采样率与管线默认不一致（如 1024 Hz），降采样到 256 Hz
                    if (bdfSampleRate != SampleRate)
                    {
                        int factor = bdfSampleRate / SampleRate;
                        if (factor > 1)
                        {
                            var ds = new double[eegData.Length / factor];
                            for (int di = 0; di < ds.Length; di++)
                                ds[di] = eegData[di * factor];
                            eegData = ds;
                        }
                    }

                    // 去直流偏置
                    {
                        double mean = eegData.Average();
                        for (int si = 0; si < eegData.Length; si++) eegData[si] -= mean;
                    }

                    eegData = EegFilter.Filter(eegData);
                    var result = calculator.Compute(eegData);
                    var metrics = ComputeBandMetrics(result);

                    var row = new Dictionary<string, object>
                    {
                        ["subject"] = subjectId,
                        ["task"] = taskName,
                        ["condition"] = condition,
                        ["channel"] = chName,
                        ["epoch_id"] = ep.EpochIndex,
                        ["start_sec"] = ep.StartTimeSec,
                        ["sample_rate"] = SampleRate,
                        ["n_segments"] = result.NumSegments,
                    };
                    foreach (var kvM in metrics) row[kvM.Key] = kvM.Value;
                    allMetricsRows.Add(row);
                }
            }
        }

        swTotal.Stop();

        // 写 CSV
        if (allMetricsRows.Count > 0)
        {
            string csvPath = Path.Combine(blockResultDir, $"{subjectId}_block_bicoherence.csv");
            WriteMetricsCsv(csvPath, allMetricsRows);
            Console.WriteLine($"\n  📊 CSV: {csvPath} ({allMetricsRows.Count} 行)");
        }

        Console.WriteLine($"\n  总耗时: {swTotal.Elapsed.TotalMinutes:F1} 分钟");
        Console.WriteLine($"  输出: {blockResultDir}");
    }

    /// <summary>
    /// 从双相干性矩阵提取频段级指标（精确数值，非像素反推）
    /// </summary>
    static Dictionary<string, double> ComputeBandMetrics(BispectrumResult result)
    {
        var metrics = new Dictionary<string, double>();
        double[,] bico = result.Bicoherence;
        double[] freqs = result.Frequencies;
        int nFreq = bico.GetLength(0);

        // 频段定义 (Hz)
        var bands = new (string name, double lo, double hi)[]
        {
            ("delta", 1, 4),
            ("theta", 4, 7),
            ("alpha", 8, 13),
            ("beta",  13, 25),
            ("gamma", 25, 40),
        };

        // --- 对角线频段均值 ---
        double diagSum = 0;
        int diagCount = 0;
        foreach (var (bName, lo, hi) in bands)
        {
            double sum = 0; int cnt = 0;
            for (int i = 0; i < nFreq; i++)
            {
                if (freqs[i] >= lo && freqs[i] <= hi && i < nFreq)
                {
                    double v = bico[i, i];
                    if (!double.IsNaN(v)) { sum += v; cnt++; }
                }
            }
            metrics[$"mean_diag_{bName}"] = cnt > 0 ? Math.Round(sum / cnt * 100, 4) : double.NaN;
            diagSum += sum; diagCount += cnt;
        }
        metrics["mean_diag"] = diagCount > 0 ? Math.Round(diagSum / diagCount * 100, 4) : double.NaN;

        // --- 三角形区域全局均值 ---
        double triSum = 0; int triCnt = 0;
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                double v = bico[f1, f2];
                if (!double.IsNaN(v)) { triSum += v; triCnt++; }
            }
        metrics["mean_all"] = triCnt > 0 ? Math.Round(triSum / triCnt * 100, 4) : double.NaN;

        // --- 非对角线均值 ---
        double offSum = 0; int offCnt = 0;
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                if (f1 == f2) continue;
                double v = bico[f1, f2];
                if (!double.IsNaN(v)) { offSum += v; offCnt++; }
            }
        metrics["mean_offdiag"] = offCnt > 0 ? Math.Round(offSum / offCnt * 100, 4) : double.NaN;

        // --- θ-α 耦合: f1∈θ 且 f2∈α ---
        double taSum = 0; int taCnt = 0;
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                if (freqs[f1] < 4 || freqs[f1] > 7) continue;
                if (freqs[f2] < 8 || freqs[f2] > 12) continue;
                double v = bico[f1, f2];
                if (!double.IsNaN(v)) { taSum += v; taCnt++; }
            }
        metrics["theta_alpha_coupling"] = taCnt > 0 ? Math.Round(taSum / taCnt * 100, 4) : double.NaN;

        // --- α-β 耦合: f1∈α 且 f2∈β ---
        double abSum = 0; int abCnt = 0;
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) break;
                if (freqs[f1] < 8 || freqs[f1] > 13) continue;
                if (freqs[f2] < 13 || freqs[f2] > 25) continue;
                double v = bico[f1, f2];
                if (!double.IsNaN(v)) { abSum += v; abCnt++; }
            }
        metrics["alpha_beta_coupling"] = abCnt > 0 ? Math.Round(abSum / abCnt * 100, 4) : double.NaN;

        // --- 对角线峰值及对应频率（跳过 DC，从 1Hz 开始）---
        double peakVal = 0; double peakFreq = 0;
        for (int i = 0; i < nFreq; i++)
        {
            if (freqs[i] < 1.0) continue;  // 跳过 DC 分量（f=0Hz 双相干性恒为 1.0）
            double v = bico[i, i];
            if (!double.IsNaN(v) && v > peakVal) { peakVal = v; peakFreq = freqs[i]; }
        }
        metrics["peak_diag"] = Math.Round(peakVal * 100, 4);
        metrics["peak_diag_freq"] = Math.Round(peakFreq, 2);

        return metrics;
    }

    // ═══════════════════════════════════════════════════════════
    //  通用 EEG 分析模式：任意 BDF/EDF，无需事件文件
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 自动从 BDF 通道标签中识别 EEG 通道（排除 Status/Trigger/EOG/EMG 等非脑电通道）。
    /// </summary>
    static List<(string Label, int Index)> AutoDetectEegChannels(BdfReader reader)
    {
        string[] labels = reader.GetChannelNames();
        string[] excludePatterns = { "STATUS", "TRIG", "EXG", "EOG", "EMG", "ECG", "EKG",
                                     "GSR", "RESP", "ACCEL", "PHOTO", "PULSE", "TEMP", "PACKET",
                                     "REF", "GND", "AUX", "DIG" };
        var channels = new List<(string Label, int Index)>();
        for (int i = 0; i < labels.Length; i++)
        {
            string upper = labels[i].ToUpperInvariant();
            if (!excludePatterns.Any(p => upper.Contains(p)))
                channels.Add((labels[i].Trim(), i));
        }
        return channels;
    }

    /// <summary>
    /// 通用分析模式：滑窗计算双相干性，适用于任意 BDF/EDF 文件。
    /// 用法: dotnet run -- --mode generic --bdf "file.bdf" 或 --datadir "dir" --subject sub-001
    /// </summary>
    static async Task RunGenericAnalysis()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║   通用 EEG 双相干性分析 (Generic Bicoherence)       ║");
        Console.WriteLine("║   滑窗计算 → 频段指标 → 热力图 + CSV                ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");

        // 1. 定位 BDF 文件
        string bdfPath;
        if (!string.IsNullOrEmpty(DirectBdfPath))
        {
            bdfPath = DirectBdfPath;
            if (!File.Exists(bdfPath))
                throw new FileNotFoundException($"BDF 文件未找到: {bdfPath}");
            Console.WriteLine($"  BDF 文件: {bdfPath}");
        }
        else
        {
            string subjectDir = Path.Combine(DataRoot, SubjectId);
            if (!Directory.Exists(subjectDir))
                throw new DirectoryNotFoundException($"被试目录不存在: {subjectDir}");
            string[] sessions = Directory.GetDirectories(subjectDir, "ses-*");
            string sesDir = sessions.FirstOrDefault(d => Path.GetFileName(d) == SessionId) ?? sessions.First();
            string eegDir = Path.Combine(sesDir, "eeg");
            if (!Directory.Exists(eegDir))
                throw new DirectoryNotFoundException($"EEG 目录不存在: {eegDir}");
            string[] bdfFiles = Directory.GetFiles(eegDir, "*.bdf");
            if (bdfFiles.Length == 0) bdfFiles = Directory.GetFiles(eegDir, "*.edf");
            if (bdfFiles.Length == 0)
                throw new FileNotFoundException($"未找到 BDF/EDF 文件: {eegDir}");
            bdfPath = bdfFiles[0];
            Console.WriteLine($"  被试: {SubjectId}, 会话: {Path.GetFileName(sesDir)}");
            Console.WriteLine($"  BDF: {Path.GetFileName(bdfPath)}");
        }

        // 2. 读取 BDF，自动识别通道
        using var bdfReader = new BdfReader(bdfPath);
        var eegChannels = AutoDetectEegChannels(bdfReader);
        Console.WriteLine($"  BDF 总通道: {bdfReader.Channels.Length}, EEG 通道: {eegChannels.Count}");
        foreach (var ch in eegChannels)
            Console.WriteLine($"    [{ch.Index}] {ch.Label}");

        // 3. 滑窗参数
        double totalDur = bdfReader.NumDataRecords * bdfReader.RecordDuration;
        double windowSec = 10.0, stepSec = 5.0;
        int totalWindows = (int)((totalDur - windowSec) / stepSec) + 1;
        if (totalWindows < 1) totalWindows = 1;
        Console.WriteLine($"  总时长: {totalDur:F0}s → {totalWindows} 个 {windowSec}s 窗口 (步长 {stepSec}s)");

        // 4. 结果目录
        string resultDir = Path.Combine(ResultsRoot, "generic_analysis",
            string.IsNullOrEmpty(DirectBdfPath)
                ? SubjectId
                : Path.GetFileNameWithoutExtension(bdfPath));
        Directory.CreateDirectory(resultDir);

        // 5. 初始化计算器
        var calculator = new BispectrumCalculator(SampleRate, Nfft, Noverlap, MaxFrequency);
        var visualizer = new Visualizer();
        var allRows = new List<Dictionary<string, object>>();

        // 6. 逐通道分析
        int actualSampleRate = (int)bdfReader.Channels[0].SamplesPerRecord;
        Console.WriteLine($"\n  (采样率: {actualSampleRate}Hz, 计算用: {SampleRate}Hz)");

        foreach (var (chLabel, chIdx) in eegChannels)
        {
            Console.WriteLine($"\n  ── 通道: {chLabel} [{chIdx}] ──");
            var chResults = new List<BispectrumResult>();

            for (int w = 0; w < totalWindows; w++)
            {
                double tStart = w * stepSec;
                double tEnd = Math.Min(tStart + windowSec, totalDur);
                double dur = tEnd - tStart;
                if (dur < 2.0) continue; // 太短跳过

                int sampleCount = (int)(dur * actualSampleRate);
                double[] eegData = new double[sampleCount];
                try
                {
                    eegData = bdfReader.ReadChannelDataByTime(chIdx, tStart, dur, actualSampleRate);
                }
                catch { continue; }
                if (eegData.Length < Nfft) continue;

                // 降采样到管线采样率（如 1024→256 Hz）
                if (actualSampleRate != SampleRate)
                    eegData = ResampleSignal(eegData, actualSampleRate, SampleRate);

                double eegMean = eegData.Average();
                for (int s = 0; s < eegData.Length; s++) eegData[s] -= eegMean;
                eegData = EegFilter.Filter(eegData);

                var result = calculator.Compute(eegData);
                chResults.Add(result);

                if (w == 0 || w == totalWindows - 1 || (w + 1) % 10 == 0)
                    Console.WriteLine($"    Win #{w + 1}/{totalWindows} @ {tStart:F0}s–{tEnd:F0}s, {result.NumSegments} 段");
            }

            if (chResults.Count == 0) continue;

            // ── 平均所有窗口 ──
            var avgResult = AverageBicoherenceResults(chResults, calculator);
            Console.WriteLine($"    {chResults.Count} 个有效窗口 → 平均 {avgResult.NumSegments} 段");

            // 热力图
            string heatmapPath = Path.Combine(resultDir, $"bicoherence_{SanitizeFileName(chLabel)}.png");
            visualizer.SaveBicoherenceHeatmap(avgResult, heatmapPath, chLabel, chLabel, DisplayMaxFreq,
                colorMin: 0, colorMax: 100);
            Console.WriteLine($"    ✓ 热力图: {Path.GetFileName(heatmapPath)}");

            // 提取指标
            var metrics = ComputeBandMetrics(avgResult);
            var row = new Dictionary<string, object>
            {
                ["subject"] = string.IsNullOrEmpty(DirectBdfPath) ? SubjectId : Path.GetFileNameWithoutExtension(bdfPath),
                ["channel"] = chLabel,
                ["n_windows"] = chResults.Count,
                ["n_segments"] = avgResult.NumSegments,
                ["total_duration_s"] = Math.Round(totalDur, 0),
            };
            foreach (var kvM in metrics) row[kvM.Key] = kvM.Value;
            allRows.Add(row);
        }

        // 7. 写 CSV
        if (allRows.Count > 0)
        {
            string csvPath = Path.Combine(resultDir, "generic_metrics.csv");
            WriteMetricsCsv(csvPath, allRows);
            Console.WriteLine($"\n  📊 指标 CSV: {csvPath} ({allRows.Count} 行)");
        }
        Console.WriteLine($"\n  ✓ 完成。输出: {resultDir}");
    }

    /// <summary>
    /// 通用模式批量处理：遍历 datadir 下所有 sub-* 目录
    /// </summary>
    static async Task RunGenericBatch()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║   通用 EEG 批量分析 (Generic Batch)                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");
        Console.WriteLine($"  数据根目录: {DataRoot}");

        string[] subjectDirs = Directory.GetDirectories(DataRoot, "sub-*");
        if (subjectDirs.Length == 0)
            throw new DirectoryNotFoundException($"未找到 sub-* 目录: {DataRoot}");

        var subjectIds = subjectDirs
            .Select(d => Path.GetFileName(d))
            .OrderBy(s => int.Parse(s.Replace("sub-", "")))
            .ToList();
        Console.WriteLine($"  发现 {subjectIds.Count} 个被试: {string.Join(", ", subjectIds)}");

        string savedSubject = SubjectId;
        string savedSession = SessionId;
        int success = 0, failed = 0;
        var swAll = Stopwatch.StartNew();

        foreach (string sid in subjectIds)
        {
            SubjectId = sid;
            try
            {
                var swSub = Stopwatch.StartNew();
                await RunGenericAnalysis();
                swSub.Stop();
                Console.WriteLine($"  ✓ {sid} 完成 ({swSub.Elapsed.TotalSeconds:F0}s)");
                success++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {sid} 失败: {ex.Message}");
                failed++;
            }
        }

        SubjectId = savedSubject;
        SessionId = savedSession;
        swAll.Stop();
        Console.WriteLine($"\n  批量完成: 成功 {success}, 失败 {failed}, 耗时 {swAll.Elapsed.TotalMinutes:F1}min");
    }

    /// <summary>将文件名中的非法字符替换为下划线</summary>
    static string SanitizeFileName(string name) => string.Join("_",
        name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    /// <summary>简单降采样（取平均）：srcRate → dstRate，仅支持整数倍速率</summary>
    static double[] ResampleSignal(double[] signal, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return signal;
        if (srcRate % dstRate != 0) return signal; // 不支持非整数倍 → 原样返回
        int factor = srcRate / dstRate;
        var result = new double[signal.Length / factor];
        for (int i = 0; i < result.Length; i++)
        {
            double sum = 0;
            for (int j = 0; j < factor; j++)
                sum += signal[i * factor + j];
            result[i] = sum / factor;
        }
        return result;
    }

    /// <summary>将指标行列表写入 CSV 文件</summary>
    static void WriteMetricsCsv(string path, List<Dictionary<string, object>> rows)
    {
        if (rows.Count == 0) return;
        var ci = CultureInfo.InvariantCulture;

        // 收集所有列名（按首次出现顺序）
        var columnOrder = new List<string>();
        var columnSet = new HashSet<string>();
        foreach (var row in rows)
            foreach (var key in row.Keys)
                if (columnSet.Add(key))
                    columnOrder.Add(key);

        using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        // header
        sw.WriteLine(string.Join(",", columnOrder));
        // data
        foreach (var row in rows)
        {
            var values = columnOrder.Select(col =>
                row.TryGetValue(col, out var val) && val != null
                    ? (val is double d ? d.ToString("F4", ci) : val.ToString()!)
                    : ""
            );
            sw.WriteLine(string.Join(",", values));
        }
    }
}
