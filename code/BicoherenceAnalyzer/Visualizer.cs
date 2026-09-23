using System.Drawing;
using System.Drawing.Imaging;
using ScottPlot;
using ScottPlot.Drawing;

namespace BicoherenceAnalyzer;

/// <summary>
/// 双相干性可视化工具 — ScottPlot 4.1 版本
/// Bicoherence / Bispectrum 热图、对角线曲线
/// </summary>
public class Visualizer
{
    private const float TitleFontSize = 24;
    private const float AxisLabelFontSize = 20;
    private const float TickLabelFontSize = 16;
    private const float ColorbarLabelFontSize = 16;

    private static Plot CreateStyledPlot(string title, string xLabel, string yLabel,
        double plotMaxFreq, int width = 1600, int height = 1400)
    {
        var plt = new Plot(width, height);
        plt.Style(figureBackground: Color.White, dataBackground: Color.White);



        double margin = 0.6;
        double axisMargin = 5.0;
        plt.SetAxisLimits(-axisMargin, plotMaxFreq + margin, -axisMargin, plotMaxFreq + margin);

        return plt;
    }

    private static void SetCleanTicks(Plot plt, double maxFreq, int tickInterval = 5)
    {
        var xTicks = new List<double>();
        var xLabels = new List<string>();
        var yTicks = new List<double>();
        var yLabels = new List<string>();

        for (double f = 0; f <= maxFreq + 0.001; f += tickInterval)
        {
            xTicks.Add(f); xLabels.Add(f.ToString("F0"));
            yTicks.Add(f); yLabels.Add(f.ToString("F0"));
        }

        plt.XAxis.ManualTickPositions(xTicks.ToArray(), xLabels.ToArray());
        plt.YAxis.ManualTickPositions(yTicks.ToArray(), yLabels.ToArray());
    }

    /// <summary>构建三角形遮罩热图数据 (f1+f2 ≤ maxFreq 为有效, 其余 NaN)</summary>
    private static double[,] BuildTriangleData(BispectrumResult result, int nFreq,
        double cellSize, double actualMaxFreq, bool useLogMagnitude = false)
    {
        double[,] data = new double[nFreq, nFreq];
        for (int f2 = 0; f2 < nFreq; f2++)
        {
            for (int f1 = 0; f1 < nFreq; f1++)
            {
                int row = nFreq - 1 - f2; // flip Y
                if (f1 * cellSize + f2 * cellSize <= actualMaxFreq + cellSize * 0.5)
                {
                    data[row, f1] = useLogMagnitude
                        ? Math.Log10(Math.Max(result.BispectrumMagnitude[f1, f2], 1e-15))
                        : result.Bicoherence[f1, f2] * 100.0;
                }
                else
                    data[row, f1] = 0.0;
            }
        }
        return data;
    }

    private static (int nFreq, double actualMaxFreq) GetDataDimensions(
        BispectrumResult result, double displayMaxFreq)
    {
        double cellSize = result.FreqResolution;
        int maxIdx = Math.Min(result.MaxFreqIndex, (int)(displayMaxFreq / cellSize));
        int nFreq = maxIdx + 1;
        double actualMaxFreq = nFreq * cellSize;
        return (nFreq, actualMaxFreq);
    }

    // ═══════════════════════════════════════════════════════════
    //  Bicoherence 热图
    // ═══════════════════════════════════════════════════════════

    public void SaveBicoherenceHeatmap(BispectrumResult result, string outputPath,
        string channelName, string stateName, double displayMaxFreq = 40.0,
        double? colorMin = null, double? colorMax = null)
    {
        double cellSize = result.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(result, displayMaxFreq);
        double[,] data = BuildTriangleData(result, nFreq, cellSize, actualMaxFreq, useLogMagnitude: false);

        string stateLabel = GetShortStateLabel(stateName);
        string title = $"{channelName} — {stateLabel}";

        var plt = CreateStyledPlot(title, "Frequency f₁ (Hz)", "Frequency f₂ (Hz)", actualMaxFreq);

        var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
        hm.OffsetX = 0; hm.OffsetY = 0;
        hm.CellWidth = cellSize; hm.CellHeight = cellSize;

        if (colorMin.HasValue && colorMax.HasValue)
        {
            hm.Update(data, colormap: Colormap.Jet, min: colorMin, max: colorMax);
        }

        var cb = plt.AddColorbar(hm);
        cb.Label = "Bicoherence (%)";

        SetCleanTicks(plt, actualMaxFreq);
        AddDiagonalLine(plt, actualMaxFreq);
        AddFrequencyBandLabels(plt, actualMaxFreq);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        plt.SaveFig(outputPath);
        Console.WriteLine($"[可视] Bicoherence: {Path.GetFileName(outputPath)}");
    }

    public void SaveBicoherenceGrayscale(BispectrumResult result, string outputPath,
        string title, double displayMaxFreq = 40.0)
    {
        double cellSize = result.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(result, displayMaxFreq);
        double[,] data = BuildTriangleData(result, nFreq, cellSize, actualMaxFreq, useLogMagnitude: false);

        var plt = CreateStyledPlot(title, "Frequency f₁ (Hz)", "Frequency f₂ (Hz)", actualMaxFreq, 800, 700);

        var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
        hm.OffsetX = 0; hm.OffsetY = 0;
        hm.CellWidth = cellSize; hm.CellHeight = cellSize;
        hm.Update(data, colormap: Colormap.Jet, min: 0, max: 100);

        SetCleanTicks(plt, actualMaxFreq);
        AddDiagonalLine(plt, actualMaxFreq);
        AddFrequencyBandLabels(plt, actualMaxFreq);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        plt.SaveFig(outputPath);
        Console.WriteLine($"[可视] 灰度Bicoherence: {Path.GetFileName(outputPath)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  双谱幅度热图
    // ═══════════════════════════════════════════════════════════

    public void SaveBispectrumMagnitudeHeatmap(BispectrumResult result, string outputPath,
        string channelName, string stateName, double displayMaxFreq = 40.0,
        double? colorMin = null, double? colorMax = null)
    {
        double cellSize = result.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(result, displayMaxFreq);
        double[,] data = BuildTriangleData(result, nFreq, cellSize, actualMaxFreq, useLogMagnitude: true);

        string stateLabel = GetShortStateLabel(stateName);
        string title = $"{channelName} — {stateLabel}";

        var plt = CreateStyledPlot(title, "Frequency f₁ (Hz)", "Frequency f₂ (Hz)", actualMaxFreq);

        var hm = plt.AddHeatmap(data, colormap: Colormap.Inferno, lockScales: false);
        hm.OffsetX = 0; hm.OffsetY = 0;
        hm.CellWidth = cellSize; hm.CellHeight = cellSize;
        if (colorMin.HasValue && colorMax.HasValue) { hm.Update(data, colormap: Colormap.Inferno, min: colorMin, max: colorMax); }

        var cb = plt.AddColorbar(hm);
        cb.Label = "log₁₀|B(f₁,f₂)|";

        SetCleanTicks(plt, actualMaxFreq);
        AddDiagonalLine(plt, actualMaxFreq);
        AddFrequencyBandLabels(plt, actualMaxFreq);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        plt.SaveFig(outputPath);
        Console.WriteLine($"[可视] Bispectrum: {Path.GetFileName(outputPath)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  三状态对比图 — 分别保存 + 合成
    // ═══════════════════════════════════════════════════════════

    public void SaveComparisonFigure(
        BispectrumResult baselineResult,
        BispectrumResult meditationResult,
        BispectrumResult recoveryResult,
        string outputPath, string channelName, double displayMaxFreq = 40.0)
    {
        double cellSize = baselineResult.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(baselineResult, displayMaxFreq);

        BispectrumResult[] results = { baselineResult, meditationResult, recoveryResult };
        string[] labels = { "冥想前", "冥想中", "冥想后" };
        string[] suffixes = { "baseline", "meditation", "recovery" };

        // 全局颜色范围
        double globalMax = 0;
        foreach (var res in results)
            for (int f1 = 0; f1 < nFreq; f1++)
                for (int f2 = 0; f2 < nFreq; f2++)
                    if (f1 * cellSize + f2 * cellSize <= actualMaxFreq + cellSize * 0.5)
                        globalMax = Math.Max(globalMax, res.Bicoherence[f1, f2] * 100.0);
        if (globalMax < 5) globalMax = 5;
        globalMax = Math.Ceiling(globalMax / 5) * 5;
        if (globalMax > 100) globalMax = 100;

        // 分别保存三个单图
        for (int i = 0; i < 3; i++)
        {
            double[,] data = BuildTriangleData(results[i], nFreq, cellSize, actualMaxFreq);
            var plt = CreateStyledPlot($"{channelName} — {labels[i]}", "f₁ (Hz)", "f₂ (Hz)", actualMaxFreq, 800, 700);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Jet, min: 0, max: globalMax);

            var cb = plt.AddColorbar(hm); cb.Label = "Bicoherence (%)";
            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);

            string dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);
            string fname = Path.GetFileNameWithoutExtension(outputPath);
            string ext = Path.GetExtension(outputPath);
            plt.SaveFig(Path.Combine(dir, $"{fname}_{suffixes[i]}{ext}"));
            Console.WriteLine($"[可视] 对比子图: {fname}_{suffixes[i]}{ext}");
        }

        // 三合一水平大图（System.Drawing 合成）
        ComposeHorizontal(results, labels, nFreq, cellSize, actualMaxFreq, globalMax, outputPath, channelName, 2800, 1000);
    }

    /// <summary>保存 Biphase 热图</summary>
    public void SaveBispectrumPhaseHeatmap(BispectrumResult result, string outputPath,
        string channelName, string stateName, double displayMaxFreq = 40.0,
        double? colorMin = null, double? colorMax = null)
    {
        double cellSize = result.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(result, displayMaxFreq);

        double[,] phaseData = new double[nFreq, nFreq];
        for (int f2 = 0; f2 < nFreq; f2++)
        {
            for (int f1 = 0; f1 < nFreq; f1++)
            {
                int row = nFreq - 1 - f2;
                if (f1 * cellSize + f2 * cellSize <= actualMaxFreq + cellSize * 0.5)
                    phaseData[row, f1] = result.Bispectrum[f1, f2].Phase * 180.0 / Math.PI;
                else
                    phaseData[row, f1] = double.NaN;
            }
        }

        string stateLabel = GetShortStateLabel(stateName);
        var plt = CreateStyledPlot($"{channelName} — {stateLabel}", "Frequency f₁ (Hz)", "Frequency f₂ (Hz)", actualMaxFreq);

        var hm = plt.AddHeatmap(phaseData, colormap: Colormap.Turbo, lockScales: false);
        hm.OffsetX = 0; hm.OffsetY = 0;
        hm.CellWidth = cellSize; hm.CellHeight = cellSize;

        if (colorMin.HasValue && colorMax.HasValue) { hm.Update(phaseData, colormap: Colormap.Turbo, min: colorMin, max: colorMax); }
        else { hm.Update(phaseData, colormap: Colormap.Turbo, min: -180, max: 180); }

        var cb = plt.AddColorbar(hm); cb.Label = "Biphase (°)";
        SetCleanTicks(plt, actualMaxFreq);
        AddDiagonalLine(plt, actualMaxFreq);
        AddFrequencyBandLabels(plt, actualMaxFreq);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        plt.SaveFig(outputPath);
        Console.WriteLine($"[可视] Biphase: {Path.GetFileName(outputPath)}");
    }

    /// <summary>对角线曲线图 (f1=f2, Bicoherence + Bispectrum)</summary>
    public void SaveBispectrumDiagonalPlot(BispectrumResult result, string outputPath,
        string channelName, string stateName, double maxDiagonalFreq = 40.0)
    {
        double cellSize = result.FreqResolution;
        int maxIdx = Math.Min(result.MaxFreqIndex, (int)(maxDiagonalFreq / cellSize));
        int n = maxIdx + 1;

        double[] freqs = new double[n];
        double[] bico = new double[n];
        double[] bispecMag = new double[n];
        for (int i = 0; i < n; i++)
        {
            freqs[i] = i * cellSize;
            bico[i] = result.Bicoherence[i, i] * 100.0;
            bispecMag[i] = Math.Log10(Math.Max(result.BispectrumMagnitude[i, i], 1e-15));
        }

        string stateLabel = GetShortStateLabel(stateName);
        int plotW = 800, plotH = 600;

        // 图1: Bicoherence 对角线
        var pltBic = new Plot(plotW, plotH);
        pltBic.Style(figureBackground: Color.White, dataBackground: Color.White);
        pltBic.Grid(enable: true, color: Color.LightGray);
        pltBic.Title($"{channelName} — {stateLabel}  (Diagonal Bicoherence)", size: 18, bold: true);
        pltBic.XLabel("Frequency (Hz)");
        pltBic.YLabel("Bicoherence (%)");
        pltBic.XAxis.TickLabelStyle(fontSize: 14);
        pltBic.YAxis.TickLabelStyle(fontSize: 14);

        var bicLine = pltBic.AddScatter(freqs, bico, color: Color.Crimson, lineWidth: 2.5f);
        bicLine.MarkerSize = 0;

        AddFrequencyBandShading(pltBic, 1, 4, Color.LightBlue, maxDiagonalFreq);
        AddFrequencyBandShading(pltBic, 4, 8, Color.LightGreen, maxDiagonalFreq);
        AddFrequencyBandShading(pltBic, 8, 13, Color.LightPink, maxDiagonalFreq);
        pltBic.SetAxisLimits(0, maxDiagonalFreq, 0, bico.Max() * 1.15);

        // 图2: Bispectrum 对角线
        var pltBispec = new Plot(plotW, plotH);
        pltBispec.Style(figureBackground: Color.White, dataBackground: Color.White);
        pltBispec.Grid(enable: true, color: Color.LightGray);
        pltBispec.Title($"{channelName} — {stateLabel}  (Diagonal Bispectrum)", size: 18, bold: true);
        pltBispec.XLabel("Frequency (Hz)");
        pltBispec.YLabel("log₁₀|B(f,f)|");
        pltBispec.XAxis.TickLabelStyle(fontSize: 14);
        pltBispec.YAxis.TickLabelStyle(fontSize: 14);

        var bisLine = pltBispec.AddScatter(freqs, bispecMag, color: Color.DarkBlue, lineWidth: 2.5f);
        bisLine.MarkerSize = 0;

        AddFrequencyBandShading(pltBispec, 1, 4, Color.LightBlue, maxDiagonalFreq);
        AddFrequencyBandShading(pltBispec, 4, 8, Color.LightGreen, maxDiagonalFreq);
        AddFrequencyBandShading(pltBispec, 8, 13, Color.LightPink, maxDiagonalFreq);
        pltBispec.SetAxisLimits(0, maxDiagonalFreq, bispecMag.Min() - 0.5, bispecMag.Max() * 1.15);

        // 垂直合成
        ComposeVertical(new[] { pltBic, pltBispec }, outputPath, 1600, 1200);
        Console.WriteLine($"[可视] 对角线: {Path.GetFileName(outputPath)}");
    }

    /// <summary>Bispectrum 三状态对比</summary>
    public void SaveBispectrumComparisonFigure(
        BispectrumResult baselineResult, BispectrumResult meditationResult, BispectrumResult recoveryResult,
        string outputPath, string channelName, double displayMaxFreq = 40.0)
    {
        double cellSize = baselineResult.FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(baselineResult, displayMaxFreq);

        BispectrumResult[] results = { baselineResult, meditationResult, recoveryResult };
        string[] labels = { "冥想前", "冥想中", "冥想后" };
        string[] suffixes = { "baseline", "meditation", "recovery" };

        double globalMin = double.MaxValue, globalMax = double.MinValue;
        foreach (var res in results)
            for (int f1 = 0; f1 < nFreq; f1++)
                for (int f2 = 0; f2 < nFreq; f2++)
                    if (f1 * cellSize + f2 * cellSize <= actualMaxFreq + cellSize * 0.5)
                    {
                        double lm = Math.Log10(Math.Max(res.BispectrumMagnitude[f1, f2], 1e-15));
                        globalMin = Math.Min(globalMin, lm); globalMax = Math.Max(globalMax, lm);
                    }
        if (double.IsInfinity(globalMin)) { globalMin = 0; globalMax = 10; }

        for (int i = 0; i < 3; i++)
        {
            double[,] data = BuildTriangleData(results[i], nFreq, cellSize, actualMaxFreq, useLogMagnitude: true);
            var plt = CreateStyledPlot($"{channelName} — {labels[i]}", "f₁ (Hz)", "f₂ (Hz)", actualMaxFreq, 800, 700);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Inferno, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Inferno, min: globalMin, max: globalMax);

            var cb = plt.AddColorbar(hm); cb.Label = "log₁₀|B(f₁,f₂)|";
            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);

            string dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);
            string fname = Path.GetFileNameWithoutExtension(outputPath);
            string ext = Path.GetExtension(outputPath);
            plt.SaveFig(Path.Combine(dir, $"{fname}_{suffixes[i]}{ext}"));
        }

        // 三合一（log scale heatmap 也合成）
        ComposeHorizontalLog(results, labels, nFreq, cellSize, actualMaxFreq, globalMin, globalMax, outputPath, channelName, 2800, 1000);
    }

    /// <summary>N 组对比图</summary>
    public void SaveNGroupComparisonFigure(
        List<BispectrumResult> results, List<string> labels,
        string outputPath, string channelName, double displayMaxFreq = 40.0)
    {
        if (results.Count < 2 || labels.Count < results.Count) return;

        double cellSize = results[0].FreqResolution;
        var (nFreq, actualMaxFreq) = GetDataDimensions(results[0], displayMaxFreq);

        double globalMax = 0;
        foreach (var res in results)
            for (int f1 = 0; f1 < nFreq; f1++)
                for (int f2 = 0; f2 < nFreq; f2++)
                    if (f1 * cellSize + f2 * cellSize <= actualMaxFreq + cellSize * 0.5)
                        globalMax = Math.Max(globalMax, res.Bicoherence[f1, f2] * 100.0);
        if (globalMax < 5) globalMax = 5;
        globalMax = Math.Ceiling(globalMax / 5) * 5;
        if (globalMax > 100) globalMax = 100;

        // 保存单图
        for (int i = 0; i < results.Count; i++)
        {
            double[,] data = BuildTriangleData(results[i], nFreq, cellSize, actualMaxFreq);
            var plt = CreateStyledPlot($"{channelName} — {labels[i]}", "f₁ (Hz)", "f₂ (Hz)", actualMaxFreq, 800, 700);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Jet, min: 0, max: globalMax);

            var cb = plt.AddColorbar(hm); cb.Label = "Bicoherence (%)";
            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);

            string dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);
            string fname = Path.GetFileNameWithoutExtension(outputPath);
            string ext = Path.GetExtension(outputPath);
            plt.SaveFig(Path.Combine(dir, $"{fname}_{i}{ext}"));
        }

        // N合一大图
        var subPlots = new List<Plot>();
        foreach (var res in results)
        {
            double[,] data = BuildTriangleData(res, nFreq, cellSize, actualMaxFreq);
            var plt = new Plot(800, 700);
            plt.Style(figureBackground: Color.White, dataBackground: Color.White);
            plt.Grid(enable: false);
            plt.SetAxisLimits(-0.3, actualMaxFreq + 0.3, -0.3, actualMaxFreq + 0.3);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Jet, min: 0, max: globalMax);

            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);
            subPlots.Add(plt);
        }

        int totalW = Math.Min(results.Count, 3) * 900 + 200;
        ComposeHorizontalSimple(subPlots, labels, outputPath, totalW, 800);
        Console.WriteLine($"[可视] {results.Count}组对比图: {Path.GetFileName(outputPath)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════

    private static void AddDiagonalLine(Plot plt, double maxFreq)
    {
        var line = plt.AddScatter(new double[] { 0, maxFreq }, new double[] { 0, maxFreq },
            color: Color.Black, lineWidth: 1.8f, lineStyle: LineStyle.Dash);
        line.MarkerSize = 0;
    }

    private static void AddFrequencyBandLabels(Plot plt, double maxFreq)
    {
        var bands = new (string Name, double Low, double High, Color RectColor)[]
        {
            ("δ", 1, 4, Color.FromArgb(90, Color.DodgerBlue)),
            ("θ", 4, 8, Color.FromArgb(90, Color.Green)),
            ("α", 8, 13, Color.FromArgb(90, Color.Crimson)),
            ("β", 13, 30, Color.FromArgb(90, Color.DarkOrange)),
        };

        foreach (var (name, low, high, rectColor) in bands)
        {
            double mid = (low + high) / 2;

            var xRect = plt.AddRectangle(low, high, -3.8, -1.0);
            xRect.Color = rectColor; xRect.BorderColor = Color.Transparent;

            var xLabel = plt.AddText(name, mid, -2.4, size: 16, color: Color.White);
            xLabel.Alignment = Alignment.MiddleCenter;
            xLabel.FontName = "Microsoft YaHei";

            var yRect = plt.AddRectangle(-3.8, -1.0, low, high);
            yRect.Color = rectColor; yRect.BorderColor = Color.Transparent;

            var yLabel = plt.AddText(name, -2.4, mid, size: 16, color: Color.White);
            yLabel.Alignment = Alignment.MiddleCenter;
            yLabel.FontName = "Microsoft YaHei";
        }
    }

    private static void AddFrequencyBandShading(Plot plt, double lowHz, double highHz,
        Color fillColor, double yTop)
    {
        var rect = plt.AddRectangle(lowHz, highHz, 0, yTop);
        rect.Color = Color.FromArgb(76, fillColor);
        rect.BorderColor = Color.Transparent;
    }

    private static string GetShortStateLabel(string state) => state switch
    {
        "baseline" => "冥想前 (Baseline)",
        "meditation" => "冥想中 (Meditation)",
        "recovery" => "冥想后 (Recovery)",
        _ => state
    };

    public static string GetStateDisplayName(string state) => state switch
    {
        "baseline" => "冥想前 (Baseline)",
        "meditation" => "冥想中 (Meditation)",
        "recovery" => "冥想后 (Recovery)",
        _ => state
    };

    // ═══════════════════════════════════════════════════════════
    //  多图合成 (ScottPlot 4 无 Multiplot，用 System.Drawing)
    // ═══════════════════════════════════════════════════════════

    private static void ComposeHorizontal(BispectrumResult[] results, string[] labels,
        int nFreq, double cellSize, double actualMaxFreq, double globalMax,
        string outputPath, string channelName, int totalW, int totalH)
    {
        int n = results.Length;
        int subW = (totalW - 40 * (n + 1)) / n;
        int subH = totalH - 80;

        using var bmp = new Bitmap(totalW, totalH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);

        for (int i = 0; i < n; i++)
        {
            double[,] data = BuildTriangleData(results[i], nFreq, cellSize, actualMaxFreq);
            var plt = CreateStyledPlot(labels[i], "f₁ (Hz)", i == 0 ? "f₂ (Hz)" : "", actualMaxFreq, subW, subH);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Jet, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Jet, min: 0, max: globalMax);

            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);

            using var subBmp = plt.Render();
            int x = 40 + i * (subW + 40);
            g.DrawImage(subBmp, x, 50, subW, subH);
        }

        using var titleFont = new System.Drawing.Font("Microsoft YaHei", 18, FontStyle.Bold);
        g.DrawString($"{channelName} — Bicoherence Comparison", titleFont, Brushes.Black, totalW / 2 - 200, 10);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        bmp.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"[可视] 对比大图: {Path.GetFileName(outputPath)}");
    }

    private static void ComposeHorizontalLog(BispectrumResult[] results, string[] labels,
        int nFreq, double cellSize, double actualMaxFreq, double globalMin, double globalMax,
        string outputPath, string channelName, int totalW, int totalH)
    {
        int n = results.Length;
        int subW = (totalW - 40 * (n + 1)) / n;
        int subH = totalH - 80;

        using var bmp = new Bitmap(totalW, totalH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);

        for (int i = 0; i < n; i++)
        {
            double[,] data = BuildTriangleData(results[i], nFreq, cellSize, actualMaxFreq, useLogMagnitude: true);
            var plt = CreateStyledPlot(labels[i], "f₁ (Hz)", i == 0 ? "f₂ (Hz)" : "", actualMaxFreq, subW, subH);

            var hm = plt.AddHeatmap(data, colormap: Colormap.Inferno, lockScales: false);
            hm.OffsetX = 0; hm.OffsetY = 0;
            hm.CellWidth = cellSize; hm.CellHeight = cellSize;
            hm.Update(data, colormap: Colormap.Inferno, min: globalMin, max: globalMax);

            SetCleanTicks(plt, actualMaxFreq);
            AddDiagonalLine(plt, actualMaxFreq);

            using var subBmp = plt.Render();
            int x = 40 + i * (subW + 40);
            g.DrawImage(subBmp, x, 50, subW, subH);
        }

        using var titleFont = new System.Drawing.Font("Microsoft YaHei", 18, FontStyle.Bold);
        g.DrawString($"{channelName} — Bispectrum Comparison", titleFont, Brushes.Black, totalW / 2 - 200, 10);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        bmp.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"[可视] Bispectrum对比大图: {Path.GetFileName(outputPath)}");
    }

    private static void ComposeHorizontalSimple(List<Plot> plots, List<string> labels,
        string outputPath, int totalW, int totalH)
    {
        int n = plots.Count;
        int subW = (totalW - 40 * (n + 1)) / n;
        int subH = totalH - 60;

        using var bmp = new Bitmap(totalW, totalH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);

        for (int i = 0; i < n; i++)
        {
            using var rendered = plots[i].Render();
            using var subBmp = new Bitmap(rendered, subW, subH);
            int x = 40 + i * (subW + 40);
            g.DrawImage(subBmp, x, 20, subW, subH);

            using var lblFont = new System.Drawing.Font("Microsoft YaHei", 14, FontStyle.Bold);
            var sz = g.MeasureString(labels[i], lblFont);
            g.DrawString(labels[i], lblFont, Brushes.Black, x + subW / 2 - sz.Width / 2, 0);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        bmp.Save(outputPath, ImageFormat.Png);
    }

    private static void ComposeVertical(Plot[] plots, string outputPath, int totalW, int totalH)
    {
        int n = plots.Length;
        int subW = totalW - 40;
        int subH = (totalH - 40 * (n + 1)) / n;

        using var bmp = new Bitmap(totalW, totalH);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);

        for (int i = 0; i < n; i++)
        {
            using var rendered = plots[i].Render();
            using var subBmp = new Bitmap(rendered, subW, subH);
            int y = 20 + i * (subH + 40);
            g.DrawImage(subBmp, 20, y, subW, subH);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        bmp.Save(outputPath, ImageFormat.Png);
    }
}
