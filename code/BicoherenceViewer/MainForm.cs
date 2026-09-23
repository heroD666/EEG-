using System.Drawing;
using System.Windows.Forms;
using BicoherenceAnalyzer;
using ScottPlot;
using ScottPlot.Drawing;
using SeeSharpTools.JY.File;
using System.Numerics;
using Microsoft.Web.WebView2.WinForms;
using WebView2Plots;
using Label = System.Windows.Forms.Label;
using FontStyle = System.Drawing.FontStyle;
using Font = System.Drawing.Font;

namespace BicoherenceViewer;

/// <summary>状态时间段（本地副本，避免跨项目 record 引用问题）</summary>
public record StateInfo(string State, double StartTimeSec, double EndTimeSec, double DurationSec);

public partial class MainForm : Form
{
    // ── 数据 ──
    private BdfReader? _bdfReader;
    private List<StateInfo> _stateSegments = new();
    private List<EventReader.PreStimulusSegment> _preStimSegments = new();
    private BispectrumCalculator? _calculator;
    private readonly Dictionary<string, BispectrumResult> _resultCache = new();
    private string _dataRoot = ResolveDataRoot();

    /// <summary>
    /// 解析数据根目录：优先读 datadir.txt 配置文件，其次尝试默认路径，
    /// 都失败则弹出文件夹选择对话框。
    /// </summary>
    private static string ResolveDataRoot()
    {
        // 1) 优先：exe 同目录下的 datadir.txt 配置文件
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(exeDir, "datadir.txt");
        if (File.Exists(configPath))
        {
            string path = File.ReadAllText(configPath).Trim();
            if (Directory.Exists(path)) return path;
        }

        // 2) 尝试从程序目录向上探测项目根（开发环境：向上找含 sub-* 数据目录的目录）
        for (var dir = new DirectoryInfo(exeDir); dir != null; dir = dir.Parent)
        {
            try
            {
                if (Directory.GetDirectories(dir.FullName, "sub-*").Length > 0)
                    return dir.FullName;
            }
            catch { /* 忽略不可读目录，继续向上 */ }
        }

        // 3) 弹出文件夹选择对话框
        using var dlg = new FolderBrowserDialog
        {
            Description = "请选择数据根目录（包含 sub-xxx/ 文件夹和 participants.tsv）"
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(configPath, dlg.SelectedPath);
            return dlg.SelectedPath;
        }

        // 4) 最终回退
        return exeDir;
    }
    private Dictionary<string, int> _channelIndexMap = new();  // 通道名 → BDF 索引
    /// <summary>Q1 冥想深度值 → GUI 显示标签</summary>
    private static string Q1Label(int q1) => q1 switch
    {
        2 => "Q1=2 (浅度冥想)",
        4 => "Q1=4 (中度冥想)",
        8 => "Q1=8 (深度冥想)",
        _ => $"Q1={q1}"
    };

    // ── 当前选择 ──
    private string _subject = "sub-001";
    private string _session = "ses-01";
    private string _channel = "Pz";
    private string _analysisType = "Bicoherence"; // Bicoherence / Bispectrum / Biphase
    private string _analysisMode = "State";       // State(冥想前中后) / Stimulus(刺激前10s)
    private bool _initialized = false;              // 防止初始化期间触发数据加载
    private double _maxDisplayFreq = 30;
    private double _bicoherenceColorMax = 100;
    private double _bispectrumColorMin = -5, _bispectrumColorMax = 5;

    // ── UI 控件 ──
    private ComboBox _cbSubject = null!, _cbSession = null!, _cbChannel = null!, _cbType = null!;
    private TrackBar _tbMaxFreq = null!, _tbBicoMax = null!;
    private Label _lblFreq = null!, _lblBicoMax = null!;
    private Button _btnRefresh = null!, _btnExport = null!;
    private FormsPlot _plotBase = null!, _plotMed = null!, _plotRec = null!, _plotDiag = null!;
    private Label _lblInfo = null!;

    // ── 3D 视图 ──
    private TabControl _tabView = null!;
    private WebView2 _webView3D = null!;
    private ComboBox _cb3DState = null!;
    private EventHandler _cb3DStateHandler = null!; // 命名 handler，避免 lambda 泄漏

    public MainForm()
    {
        Text = "EEG Bicoherence 交互分析器";
        Size = new Size(1650, 1050);
        StartPosition = FormStartPosition.CenterScreen;
        InitializeUI();
        Load += async (_, _) => await InitializeDataAsync();
    }

    // ═════════════════════════════════════════════════════════
    //  UI 布局
    // ═════════════════════════════════════════════════════════

    private void InitializeUI()
    {
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(8)
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(mainLayout);

        // ── Row 0: 控制栏 ──
        var ctrlPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 11, RowCount = 2, Padding = new Padding(4)
        };
        for (int i = 0; i < 11; i++)
            ctrlPanel.ColumnStyles.Add(new ColumnStyle(i >= 8 ? SizeType.Absolute : SizeType.AutoSize, i >= 8 ? 100 : 0));
        ctrlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        ctrlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        mainLayout.Controls.Add(ctrlPanel, 0, 0);
        mainLayout.SetColumnSpan(ctrlPanel, 3);

        // Row 0
        AddLabel(ctrlPanel, "被试:", 0, 0);
        _cbSubject = AddCombo(ctrlPanel, 1, 0, 100);
        AddLabel(ctrlPanel, "会话:", 2, 0);
        _cbSession = AddCombo(ctrlPanel, 3, 0, 100);
        AddLabel(ctrlPanel, "通道:", 4, 0);
        _cbChannel = AddCombo(ctrlPanel, 5, 0, 80, new[] { "Fp1", "Fp2", "C3", "C4", "CP3", "CP4", "FCz", "Fz", "Cz", "Pz", "O1", "O2" });
        _cbChannel.SelectedIndex = 9; // 默认 Pz
        AddLabel(ctrlPanel, "类型:", 6, 0);
        _cbType = AddCombo(ctrlPanel, 7, 0, 100, new[] { "Bicoherence", "Bispectrum", "Biphase" });
        _cbType.SelectedIndex = 0; // 默认 Bicoherence
        var _cbMode = AddCombo(ctrlPanel, 8, 0, 100, new[] { "State", "Stimulus" });
        _cbMode.SelectedIndexChanged += (_, _) =>
        {
            if (_cbMode.SelectedItem is string m) { _analysisMode = m; if (_initialized) ReloadAndRefresh(); }
        };
        // 注意：_cbMode.SelectedIndex 在 _lblInfo 创建之后才设置（避免 NRE）
        _btnRefresh = new Button { Text = "刷新 (F5)", Dock = DockStyle.Fill, Margin = new Padding(4, 2, 4, 2) };
        _btnRefresh.Click += (_, _) => RefreshPlots();
        ctrlPanel.Controls.Add(_btnRefresh, 9, 0);
        ctrlPanel.SetRowSpan(_btnRefresh, 2);
        _btnExport = new Button { Text = "导出 PNG", Dock = DockStyle.Fill, Margin = new Padding(4, 2, 4, 2) };
        _btnExport.Click += (_, _) => ExportCurrentView();
        ctrlPanel.Controls.Add(_btnExport, 10, 0);
        ctrlPanel.SetRowSpan(_btnExport, 2);

        // Row 1: 滑块
        AddLabel(ctrlPanel, "最大频率:", 0, 1);
        _tbMaxFreq = new TrackBar
        {
            Minimum = 10, Maximum = 45, Value = (int)_maxDisplayFreq,
            TickFrequency = 5, Dock = DockStyle.Fill, Margin = new Padding(2)
        };
        _tbMaxFreq.Scroll += (_, _) => { _maxDisplayFreq = _tbMaxFreq.Value; UpdateSliderLabels(); RefreshPlots(); };
        ctrlPanel.Controls.Add(_tbMaxFreq, 1, 1);
        ctrlPanel.SetColumnSpan(_tbMaxFreq, 2);
        _lblFreq = AddLabel(ctrlPanel, $"{_maxDisplayFreq} Hz", 3, 1);

        AddLabel(ctrlPanel, "Bicoherence 上限:", 4, 1);
        _tbBicoMax = new TrackBar
        {
            Minimum = 5, Maximum = 100, Value = (int)_bicoherenceColorMax,
            TickFrequency = 5, Dock = DockStyle.Fill, Margin = new Padding(2)
        };
        _tbBicoMax.Scroll += (_, _) => { _bicoherenceColorMax = _tbBicoMax.Value; UpdateSliderLabels(); RefreshPlots(); };
        ctrlPanel.Controls.Add(_tbBicoMax, 5, 1);
        ctrlPanel.SetColumnSpan(_tbBicoMax, 2);
        _lblBicoMax = AddLabel(ctrlPanel, $"{_bicoherenceColorMax}%", 7, 1);

        // ── Row 1+2: TabControl（2D 热力图 / 3D 曲面图）──
        _tabView = new TabControl { Dock = DockStyle.Fill };
        _tabView.SelectedIndexChanged += (_, _) => RefreshPlots();

        // Tab 0: 2D 热力图（保持原有布局）
        var panel2D = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        panel2D.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        panel2D.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        panel2D.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        panel2D.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        panel2D.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        _plotBase = CreatePlotPanel("Baseline (冥想前)");
        _plotMed = CreatePlotPanel("Meditation (冥想中)");
        _plotRec = CreatePlotPanel("Recovery (冥想后)");
        panel2D.Controls.Add(_plotBase, 0, 0);
        panel2D.Controls.Add(_plotMed, 1, 0);
        panel2D.Controls.Add(_plotRec, 2, 0);

        _plotDiag = CreatePlotPanel("Diagonal Bicoherence (f₁=f₂)");
        panel2D.Controls.Add(_plotDiag, 0, 1);
        panel2D.SetColumnSpan(_plotDiag, 3);

        var tab2D = new TabPage("2D 热力图");
        tab2D.Controls.Add(panel2D);
        _tabView.TabPages.Add(tab2D);

        // Tab 1: 3D 曲面图
        var panel3D = new Panel { Dock = DockStyle.Fill };
        var topBar3D = new Panel { Dock = DockStyle.Top, Height = 32 };
        var lbl3D = new Label { Text = "选择状态:", Left = 8, Top = 6, AutoSize = true };
        _cb3DState = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Left = 80, Top = 4 };
        _cb3DStateHandler = async (_, _) => await Render3DView();
        _cb3DState.SelectedIndexChanged += _cb3DStateHandler;
        topBar3D.Controls.Add(lbl3D);
        topBar3D.Controls.Add(_cb3DState);
        _webView3D = new WebView2 { Dock = DockStyle.Fill };
        // CreationProperties 指定 userDataFolder，由 EnsureCoreWebView2Async() 标准 API 使用
        // 这是 .NET Framework 4.8 下避免 RPC_E_CHANGED_MODE 的关键配置
        _webView3D.CreationProperties = new Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BicoherenceViewer")
        };
        panel3D.Controls.Add(_webView3D);
        panel3D.Controls.Add(topBar3D);

        var tab3D = new TabPage("3D 曲面图");
        tab3D.Controls.Add(panel3D);
        _tabView.TabPages.Add(tab3D);

        mainLayout.Controls.Add(_tabView, 0, 1);
        mainLayout.SetColumnSpan(_tabView, 3);
        mainLayout.SetRowSpan(_tabView, 2);

        // ── Row 3: 状态栏 ──
        _lblInfo = new Label { Text = "就绪", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        mainLayout.Controls.Add(_lblInfo, 0, 3);
        mainLayout.SetColumnSpan(_lblInfo, 3);

        // _lblInfo 已就绪，现在可以安全触发模式切换事件
        _cbMode.SelectedIndex = 0;

        // 事件绑定
        _cbSubject.SelectedIndexChanged += async (_, _) => await OnSubjectChanged();
        _cbSession.SelectedIndexChanged += async (_, _) => await OnSessionChanged();
        _cbChannel.SelectedIndexChanged += (_, _) => OnParamChanged();
        _cbType.SelectedIndexChanged += (_, _) => OnParamChanged();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F5) RefreshPlots(); };
    }

    private static Label AddLabel(TableLayoutPanel panel, string text, int col, int row)
    {
        var lbl = new Label
        {
            Text = text, TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true, Margin = new Padding(4, 0, 2, 0)
        };
        panel.Controls.Add(lbl, col, row);
        return lbl;
    }

    private static ComboBox AddCombo(TableLayoutPanel panel, int col, int row, int width, string[]? items = null)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = width, Margin = new Padding(2)
        };
        if (items != null) cb.Items.AddRange(items);
        panel.Controls.Add(cb, col, row);
        return cb;
    }

    private static FormsPlot CreatePlotPanel(string title)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4) };
        var lbl = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 22,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Microsoft YaHei", 10, FontStyle.Bold)
        };
        var plot = new FormsPlot { Dock = DockStyle.Fill };
        panel.Controls.Add(plot);
        panel.Controls.Add(lbl);
        return plot;
    }

    private void UpdateSliderLabels()
    {
        _lblFreq.Text = $"{_maxDisplayFreq} Hz";
        _lblBicoMax.Text = $"{_bicoherenceColorMax}%";
    }

    // ═════════════════════════════════════════════════════════
    //  数据加载
    // ═════════════════════════════════════════════════════════

    private async Task InitializeDataAsync()
    {
        _lblInfo.Text = "正在扫描被试列表...";
        var subs = Directory.GetDirectories(_dataRoot, "sub-*")
            .Select(Path.GetFileName).Where(d => d != null).Cast<string>()
            .OrderBy(s => s).ToArray();
        foreach (var s in subs) _cbSubject.Items.Add(s);
        _cbSubject.SelectedItem = _subject;
        _initialized = true; // 初始化完成，此后 _cbMode 切换可以触发数据加载
    }

    private async Task OnSubjectChanged()
    {
        if (_cbSubject.SelectedItem is not string s) return;
        _subject = s;
        _cbSession.Items.Clear();
        var subDir = Path.Combine(_dataRoot, _subject);
        if (!Directory.Exists(subDir)) return;

        var sessions = Directory.GetDirectories(subDir, "ses-*")
            .Select(Path.GetFileName).Where(d => d != null).Cast<string>()
            .OrderBy(x => x).ToArray();
        foreach (var ses in sessions) _cbSession.Items.Add(ses);
        if (sessions.Length > 0) _cbSession.SelectedIndex = 0;
    }

    private async Task OnSessionChanged()
    {
        if (_cbSession.SelectedItem is not string ses) return;
        _session = ses;
        await LoadBdfAndEvents();
        OnParamChanged();
    }

    private async Task LoadBdfAndEvents()
    {
        _lblInfo.Text = $"加载 {_subject}/{_session}...";
        _bdfReader?.Dispose();
        _resultCache.Clear();

        string subDir = Path.Combine(_dataRoot, _subject, _session, "eeg");
        if (!Directory.Exists(subDir)) { _lblInfo.Text = "数据目录不存在"; return; }

        var bdfFiles = Directory.GetFiles(subDir, "*_eeg.bdf");
        var tsvFiles = Directory.GetFiles(subDir, "*_events.tsv");
        if (bdfFiles.Length == 0 || tsvFiles.Length == 0) { _lblInfo.Text = "找不到 BDF/TSV"; return; }

        _bdfReader = new BdfReader(bdfFiles[0]);

        // 从 channels.tsv 建立通道名 → BDF 索引映射
        _channelIndexMap.Clear();
        string channelsTsv = Path.Combine(_dataRoot, "task-meditation_channels.tsv");
        if (File.Exists(channelsTsv))
        {
            string[] tsvNames = File.ReadAllLines(channelsTsv)
                .Skip(1).Select(l => l.Split('\t')[0].Trim()).ToArray();
            for (int c = 0; c < tsvNames.Length; c++)
                _channelIndexMap[tsvNames[c]] = c;
        }

        var eventReader = new EventReader(tsvFiles[0]);
        double totalDuration = _bdfReader.NumDataRecords * _bdfReader.RecordDuration;

        if (_analysisMode == "Stimulus")
        {
            // Stimulus 模式：每个 stimulus（value=128）都是 Q1 探头
            // 按被试 Q1 冥想深度响应值分组
            _preStimSegments = eventReader.ExtractPreStimulusSegments(10.0);
            var q1Values = _preStimSegments
                .Select(s => s.Q1Response)
                .Where(v => v > 0)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            _stateSegments = q1Values.Select(v =>
                new StateInfo(Q1Label(v), 0, 0, 10.0)
            ).ToList();
        }
        else
        {
            var segs = eventReader.ExtractStateSegments(totalDuration, 300);
            _stateSegments = segs.Select(s => new StateInfo(s.State, s.StartTimeSec, s.EndTimeSec, s.DurationSec)).ToList();
        }

        _calculator = new BispectrumCalculator(256, 512, 256, 45);
        _lblInfo.Text = $"已加载: {_bdfReader.NumDataRecords} 记录, {_stateSegments.Count} 个时间窗";
    }

    private async void ReloadAndRefresh()
    {
        await LoadBdfAndEvents();
        OnParamChanged();
    }

    // ═════════════════════════════════════════════════════════
    //  参数变化处理
    // ═════════════════════════════════════════════════════════

    private void OnParamChanged()
    {
        if (_cbChannel.SelectedItem is string ch) _channel = ch;
        if (_cbType.SelectedItem is string t) _analysisType = t;
        ComputeCurrentChannel();
        RefreshPlots();
    }

    private void ComputeCurrentChannel()
    {
        if (_bdfReader == null || _stateSegments.Count == 0 || _calculator == null) return;

        string channelKey = $"{_subject}/{_session}/{_channel}";
        if (_resultCache.ContainsKey(channelKey)) return;

        _lblInfo.Text = $"正在计算 {_channel}...";
        int chIdx = FindChannelIndex(_bdfReader, _channel);
        if (chIdx < 0) { _lblInfo.Text = $"通道 {_channel} 未找到"; return; }

        if (_analysisMode == "Stimulus" && _preStimSegments.Count > 0)
        {
            // Stimulus 模式：256 窗 / 128 步，牺牲频率分辨率换取统计可靠性
            _calculator!.SetWindowParams(256, 128);

            // 逐个计算每个 stimulus 段，按 Q1 冥想深度值分组平均
            var qResults = new Dictionary<int, List<BispectrumResult>>();

            foreach (var seg in _preStimSegments)
            {
                if (seg.Q1Response == 0) continue;
                double[] data = _bdfReader.ReadChannelDataByTime(chIdx, seg.StartTimeSec, seg.DurationSec, 256);
                if (data.Length < _calculator.Nfft) continue;
                data = EegFilter.Filter(data);
                var result = _calculator.Compute(data);
                if (!qResults.ContainsKey(seg.Q1Response))
                    qResults[seg.Q1Response] = new List<BispectrumResult>();
                qResults[seg.Q1Response].Add(result);
            }

            // 对每个 Q1 值组求平均
            foreach (var kv in qResults)
            {
                int q1Val = kv.Key;
                var results = kv.Value;
                if (results.Count == 0) continue;
                string stateKey = $"{channelKey}/{Q1Label(q1Val)}";
                if (results.Count == 1)
                    _resultCache[stateKey] = results[0];
                else
                    _resultCache[stateKey] = AverageResults(results);
            }
        }
        else
        {
            // State 模式：512 窗 / 256 步，高频率分辨率
            _calculator!.SetWindowParams(512, 256);

            foreach (var seg in _stateSegments)
            {
                double[] data = _bdfReader.ReadChannelDataByTime(chIdx, seg.StartTimeSec, seg.DurationSec, 256);
                if (data.Length < _calculator.Nfft) continue;
                data = EegFilter.Filter(data);
                var result = _calculator.Compute(data);
                _resultCache[$"{channelKey}/{seg.State}"] = result;
            }
        }
        _lblInfo.Text = $"{_channel} 计算完成 ({_stateSegments.Count} 个状态)";
    }

    /// <summary>对多个 BispectrumResult 的 Bicoherence 逐元素求平均</summary>
    private static BispectrumResult AverageResults(List<BispectrumResult> results)
    {
        int nFreq = results[0].Bicoherence.GetLength(0);
        Complex[,] avgBispec = new Complex[nFreq, nFreq];
        double[,] avgBico = new double[nFreq, nFreq];
        double[,] avgMag = new double[nFreq, nFreq];
        int count = results.Count;
        for (int f1 = 0; f1 < nFreq; f1++)
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                if (f1 + f2 >= nFreq) continue;
                double bSum = 0, mSum = 0;
                Complex bsSum = Complex.Zero;
                foreach (var r in results)
                {
                    bSum += r.Bicoherence[f1, f2];
                    mSum += r.BispectrumMagnitude[f1, f2];
                    bsSum += r.Bispectrum[f1, f2];
                }
                avgBico[f1, f2] = bSum / count;
                avgMag[f1, f2] = mSum / count;
                avgBispec[f1, f2] = bsSum / count;
            }
        return new BispectrumResult
        {
            Bicoherence = avgBico,
            BispectrumMagnitude = avgMag,
            Bispectrum = avgBispec,
            Frequencies = results[0].Frequencies,
            FreqResolution = results[0].FreqResolution,
            MaxFreqIndex = results[0].MaxFreqIndex,
            NumSegments = results.Sum(r => r.NumSegments)
        };
    }

    private int FindChannelIndex(BdfReader reader, string name)
    {
        // 用 channels.tsv 建立的标准名称→BDF索引映射表
        return _channelIndexMap.TryGetValue(name, out int idx) ? idx : -1;
    }

    // ═════════════════════════════════════════════════════════
    //  绘图核心
    // ═════════════════════════════════════════════════════════

    private async void RefreshPlots()
    {
        if (_resultCache.Count == 0) return;

        string channelKey = $"{_subject}/{_session}/{_channel}";
        var states = _stateSegments.Select(s => s.State).Distinct().ToList();
        if (states.Count == 0) return;

        // ── 3D 标签页：更新状态下拉框并渲染曲面 ──
        if (_tabView.SelectedIndex == 1)
        {
            // 更新 3D 状态下拉框（仅在选项变化时更新，避免死循环）
            var currentItems = _cb3DState.Items.Cast<string>().ToList();
            if (!currentItems.SequenceEqual(states))
            {
                _cb3DState.SelectedIndexChanged -= _cb3DStateHandler;
                _cb3DState.Items.Clear();
                foreach (var st in states) _cb3DState.Items.Add(st);
                if (_cb3DState.Items.Count > 0) _cb3DState.SelectedIndex = 0;
                _cb3DState.SelectedIndexChanged += _cb3DStateHandler;
            }
            await Render3DView();
            return;
        }

        // ── 2D 标签页：原有的热力图渲染逻辑 ──
        string[] stateLabels = states.Take(3).ToArray();

        // 获取全局颜色范围
        double globalMax = _analysisType == "Bicoherence" ? _bicoherenceColorMax : 100;
        double logMin = _bispectrumColorMin, logMax = _bispectrumColorMax;
        if (_analysisType == "Bispectrum")
        {
            logMin = double.MaxValue; logMax = double.MinValue;
            foreach (var st in states.Take(3))
            {
                if (!_resultCache.TryGetValue($"{channelKey}/{st}", out var res)) continue;
                int nFreq = Math.Min(res.MaxFreqIndex, (int)(_maxDisplayFreq / res.FreqResolution)) + 1;
                double cell = res.FreqResolution;
                for (int f1 = 0; f1 < nFreq; f1++)
                    for (int f2 = 0; f2 < nFreq; f2++)
                        if (f1 * cell + f2 * cell <= nFreq * cell + cell * 0.5)
                        {
                            double mag = res.BispectrumMagnitude[f1, f2];
                            double lm = Math.Log10(Math.Max(mag, 1e-15));
                            logMin = Math.Min(logMin, lm);
                            logMax = Math.Max(logMax, lm);
                        }
            }
        }

        // 渲染热图
        FormsPlot[] plots = { _plotBase, _plotMed, _plotRec };
        for (int i = 0; i < Math.Min(3, states.Count); i++)
        {
            if (!_resultCache.TryGetValue($"{channelKey}/{states[i]}", out var result)) continue;
            string label = _analysisMode == "Stimulus" ? $"{_channel} — {states[i]}" : $"{_channel} — {StateLabel(states[i])}";
            RenderHeatmap(plots[i], result, label, globalMax, logMin, logMax);
            // 同步更新面板标题 Label
            string panelTitle = _analysisMode == "Stimulus" ? states[i] : StateLabel(states[i]);
            if (plots[i].Parent?.Controls.Count > 1 && plots[i].Parent.Controls[1] is Label lbl)
                lbl.Text = panelTitle;
        }

        // 清空未使用的面板并刷新（否则旧图像残留）
        for (int i = states.Count; i < 3; i++)
        {
            plots[i].Plot.Clear();
            plots[i].Refresh();
            // 同步更新面板标题 Label
            if (plots[i].Parent?.Controls.Count > 1 && plots[i].Parent.Controls[1] is Label lbl)
                lbl.Text = "无数据";
        }

        // 渲染对角线图
        if (_resultCache.TryGetValue($"{channelKey}/{states[0]}", out var baseResult))
            RenderDiagonal(_plotDiag, $"{_channel} — 对角线 Bicoherence");

        _lblInfo.Text = $"{_channel} | {_analysisType} | {_analysisMode} | 频率 0-{_maxDisplayFreq} Hz";
    }

    private static string StateLabel(string s) => s switch
    {
        "baseline" => "冥想前", "meditation" => "冥想中", "recovery" => "冥想后", _ => s
    };

    /// <summary>渲染 3D 曲面图（当前选中的状态）</summary>
    private async Task Render3DView()
    {
        if (_cb3DState.SelectedItem is not string state) return;
        string channelKey = $"{_subject}/{_session}/{_channel}";
        if (!_resultCache.TryGetValue($"{channelKey}/{state}", out var result)) return;

        string label = _analysisMode == "Stimulus" ? $"{_channel} — {state}" : $"{_channel} — {StateLabel(state)}";
        _lblInfo.Text = $"{_channel} | 3D曲面 | {state} | 频率 0-{_maxDisplayFreq} Hz";

        // 构建 bicoherence 矩阵 data[f1, f2]，非物理区域填 0
        double cellSize = result.FreqResolution;
        int maxIdx = Math.Min(result.MaxFreqIndex, (int)(_maxDisplayFreq / cellSize));
        int nFreq = maxIdx + 1;
        double actualMax = nFreq * cellSize;

        double[,] data = new double[nFreq, nFreq];
        for (int f1 = 0; f1 < nFreq; f1++)
        {
            for (int f2 = 0; f2 < nFreq; f2++)
            {
                double freq1 = f1 * cellSize;
                double freq2 = f2 * cellSize;
                if (freq1 + freq2 <= actualMax + cellSize * 0.5)
                    data[f1, f2] = result.Bicoherence[f1, f2] * 100.0;
                else
                    data[f1, f2] = 0.0;
            }
        }

        await WebView2ThreeDSurface.RenderAsync(_webView3D, data,
            0, cellSize, 0, cellSize,
            "f₁ (Hz)", "f₂ (Hz)", "Bicoherence (%)",
            ThreeDSurfaceType.Surface, label);
    }

    private void RenderHeatmap(FormsPlot fp, BispectrumResult result, string title,
        double bicoMax, double logMin, double logMax)
    {
        fp.Plot.Clear();
        var plt = fp.Plot;
        double cell = result.FreqResolution;
        int maxIdx = Math.Min(result.MaxFreqIndex, (int)(_maxDisplayFreq / cell));
        int nFreq = maxIdx + 1;
        double actualMax = nFreq * cell;

        // 构建数据
        double[,] data = new double[nFreq, nFreq];
        for (int f2 = 0; f2 < nFreq; f2++)
        {
            for (int f1 = 0; f1 < nFreq; f1++)
            {
                int row = nFreq - 1 - f2;
                if (f1 * cell + f2 * cell <= actualMax + cell * 0.5)
                {
                    data[row, f1] = _analysisType switch
                    {
                        "Bicoherence" => result.Bicoherence[f1, f2] * 100.0,
                        "Bispectrum" => Math.Log10(Math.Max(result.BispectrumMagnitude[f1, f2], 1e-15)),
                        "Biphase" => result.Bispectrum[f1, f2].Phase * 180.0 / Math.PI,
                        _ => 0
                    };
                }
                else
                    data[row, f1] = 0; // ScottPlot 4.1 不支持 NaN，非物理区域填 0
            }
        }

        // 热图
        var colormap = _analysisType switch
        {
            "Bicoherence" => Colormap.Jet,
            "Bispectrum" => Colormap.Inferno,
            "Biphase" => Colormap.Turbo,
            _ => Colormap.Jet
        };
        var hm = plt.AddHeatmap(data, colormap: colormap, lockScales: false);
        hm.OffsetX = 0; hm.OffsetY = 0;
        hm.CellWidth = cell; hm.CellHeight = cell;

        if (_analysisType == "Bicoherence")
        { hm.Update(data, colormap: colormap, min: 0, max: bicoMax); }
        else if (_analysisType == "Bispectrum")
        { hm.Update(data, colormap: colormap, min: logMin, max: logMax); }
        else
        { hm.Update(data, colormap: colormap, min: -180, max: 180); }

        // 颜色条
        var cb = plt.AddColorbar(hm);
        cb.Label = _analysisType switch
        {
            "Bicoherence" => "Bicoherence (%)",
            "Bispectrum" => "log₁₀|B|",
            "Biphase" => "Phase (°)",
            _ => ""
        };

        // 样式
        plt.Style(figureBackground: Color.White, dataBackground: Color.White);
        plt.Title(title, size: 16, bold: true, color: Color.Black);
        plt.XLabel("f₁ (Hz)");
        plt.YLabel("f₂ (Hz)");
        plt.XAxis.TickLabelStyle(fontSize: 11, color: Color.Black);
        plt.YAxis.TickLabelStyle(fontSize: 11, color: Color.Black);
        plt.Grid(enable: false);
        plt.SetAxisLimits(-4, actualMax + 0.6, -4, actualMax + 0.6);

        // 对角线
        var diag = plt.AddScatter(new double[] { 0, actualMax }, new double[] { 0, actualMax },
            color: Color.Black, lineWidth: 1.5f, lineStyle: LineStyle.Dash);
        diag.MarkerSize = 0;

        // 频段标签（简化版，只标注 X 轴）
        AddSimpleBandLabels(plt, actualMax);

        fp.Refresh();
    }

    private void RenderDiagonal(FormsPlot fp, string title)
    {
        fp.Plot.Clear();
        var plt = fp.Plot;
        string channelKey = $"{_subject}/{_session}/{_channel}";
        var states = _stateSegments.Select(s => s.State).Distinct().Take(3).ToList();
        if (states.Count == 0) return;

        var colors = new[] { Color.DodgerBlue, Color.Crimson, Color.DarkOrange };
        double yMax = 0;
        for (int i = 0; i < states.Count; i++)
        {
            if (!_resultCache.TryGetValue($"{channelKey}/{states[i]}", out var res)) continue;
            int n = Math.Min(res.Frequencies.Length, (int)(_maxDisplayFreq / res.FreqResolution) + 1);
            double[] freqs = new double[n];
            double[] vals = new double[n];
            for (int f = 0; f < n; f++)
            {
                freqs[f] = f * res.FreqResolution;
                vals[f] = res.Bicoherence[f, f] * 100.0;
                if (vals[f] > yMax) yMax = vals[f];
            }
            string legend = _analysisMode == "Stimulus" ? states[i] : StateLabel(states[i]);
            var line = plt.AddScatter(freqs, vals, color: colors[i], lineWidth: 2f);
            line.Label = legend;
            line.MarkerSize = 0;
        }

        plt.Style(figureBackground: Color.White, dataBackground: Color.White);
        plt.Title(title, size: 16, bold: true, color: Color.Black);
        plt.XLabel("Frequency (Hz)");
        plt.YLabel("Bicoherence (%)");
        plt.SetAxisLimits(0, _maxDisplayFreq, 0, Math.Max(yMax * 1.15, 5));
        plt.Legend();
        fp.Refresh();
    }

    private static void AddSimpleBandLabels(Plot plt, double maxFreq)
    {
        var bands = new (string name, double low, double high, Color color)[]
        {
            ("δ", 1, 4, Color.FromArgb(76, Color.DodgerBlue)),
            ("θ", 4, 8, Color.FromArgb(76, Color.Green)),
            ("α", 8, 13, Color.FromArgb(76, Color.Crimson)),
            ("β", 13, 30, Color.FromArgb(76, Color.DarkOrange)),
        };
        foreach (var (name, low, high, color) in bands)
        {
            if (low > maxFreq) break;
            double h = Math.Min(high, maxFreq);
            var rect = plt.AddRectangle(low, h, -3.5, -1.0);
            rect.Color = color;
            rect.BorderColor = Color.Transparent;
            var txt = plt.AddText(name, (low + h) / 2, -2.25, size: 13, color: Color.White);
            txt.Alignment = Alignment.MiddleCenter;
        }
    }

    // ═════════════════════════════════════════════════════════
    //  导出
    // ═════════════════════════════════════════════════════════

    private async void ExportCurrentView()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "导出当前视图",
            Filter = "PNG图片|*.png",
            FileName = $"bicoherence_{_subject}_{_channel}_{_analysisType}.png"
        };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        string? dir = Path.GetDirectoryName(sfd.FileName);
        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show("无法解析导出路径，请选择其他位置。", "导出失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string baseName = Path.GetFileNameWithoutExtension(sfd.FileName);
        string ext = Path.GetExtension(sfd.FileName);

        // 检查是否有可导出的数据
        string channelKey = $"{_subject}/{_session}/{_channel}";
        var states = _stateSegments.Select(s => s.State).Distinct().ToList();
        if (states.Count == 0 || _resultCache.Count == 0)
        {
            MessageBox.Show("没有可导出的数据，请先选择被试并等待计算完成。", "导出失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnExport.Enabled = false;
        _lblInfo.Text = "正在导出 PNG...";

        // 在后台线程执行 ScottPlot 渲染，避免阻塞 UI
        try
        {
            var vis = new Visualizer();
            string[] labels = states.Select(StateLabel).ToArray();
            // 从缓存中提取需要的结果（在 UI 线程上安全读取）
            var exportTasks = new List<(string path, BispectrumResult result, string label)>();
            for (int i = 0; i < states.Count; i++)
            {
                if (!_resultCache.TryGetValue($"{channelKey}/{states[i]}", out var res)) continue;
                exportTasks.Add((Path.Combine(dir, $"{baseName}_{states[i]}{ext}"), res, labels[i]));
            }

            int exported = await Task.Run(() =>
            {
                int count = 0;
                foreach (var (path, result, label) in exportTasks)
                {
                    if (_analysisType == "Bicoherence")
                        vis.SaveBicoherenceHeatmap(result, path, _channel, label, _maxDisplayFreq, 0, _bicoherenceColorMax);
                    else if (_analysisType == "Bispectrum")
                        vis.SaveBispectrumMagnitudeHeatmap(result, path, _channel, label, _maxDisplayFreq, _bispectrumColorMin, _bispectrumColorMax);
                    else
                        vis.SaveBispectrumPhaseHeatmap(result, path, _channel, label, _maxDisplayFreq);
                    count++;
                }
                return count;
            });

            _lblInfo.Text = "已导出";
            MessageBox.Show($"已导出 {exported} 张图片到:\n{dir}", "导出成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _lblInfo.Text = "导出失败";
            MessageBox.Show($"导出 PNG 时发生错误:\n{ex.Message}", "导出失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnExport.Enabled = true;
        }
    }

    /// <summary>使用 SeeSharpTools CsvHandler 导出对角线 bicoherence 数据为 CSV</summary>
    private void ExportDiagonalCsv()
    {
        try
        {
            string channelKey = $"{_subject}/{_session}/{_channel}";
            var states = _stateSegments.Select(s => s.State).Distinct().ToList();
            if (states.Count == 0) return;

            // 收集数据：第1列=频率，后续每列=一个状态
            var allFreqs = new List<double[]>();
            var allVals = new List<(string label, double[] values)>();
            int maxLen = 0;
            foreach (var st in states)
            {
                if (!_resultCache.TryGetValue($"{channelKey}/{st}", out var res)) continue;
                int n = Math.Min(res.Frequencies.Length, (int)(_maxDisplayFreq / res.FreqResolution) + 1);
                var freqs = res.Frequencies.Take(n).ToArray();
                var vals = new double[n];
                for (int f = 0; f < n; f++) vals[f] = res.Bicoherence[f, f] * 100.0;
                allFreqs.Add(freqs);
                allVals.Add((_analysisMode == "Stimulus" ? st : StateLabel(st), vals));
                if (n > maxLen) maxLen = n;
            }
            if (allVals.Count == 0) return;

            // 构建 string[,]（CsvHandler.WriteData 需要）
            int rows = maxLen + 1; // +1 for header
            int cols = 1 + allVals.Count;
            string[,] csvData = new string[rows, cols];
            csvData[0, 0] = "Frequency (Hz)";
            for (int c = 0; c < allVals.Count; c++)
                csvData[0, c + 1] = allVals[c].label + " Bicoherence(%)";

            // 使用第一个状态的频率作为 X 轴（所有状态的分辨率相同）
            double[] refFreqs = allFreqs[0];
            for (int r = 0; r < maxLen; r++)
            {
                csvData[r + 1, 0] = r < refFreqs.Length ? refFreqs[r].ToString("F3") : "";
                for (int c = 0; c < allVals.Count; c++)
                    csvData[r + 1, c + 1] = r < allVals[c].values.Length
                        ? allVals[c].values[r].ToString("F4") : "";
            }

            CsvHandler.WriteData(csvData); // 弹出保存对话框
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出 CSV 时发生错误:\n{ex.Message}", "CSV 导出失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
