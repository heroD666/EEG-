using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using BicoherenceAnalyzer;

namespace BicoherenceViewer;

/// <summary>
/// 批量双相干性分析 GUI — 公司交付用
/// 支持: ds001787 (BIDS 探针范式) / ds003969 (Block 设计) / 自定义 BDF
/// </summary>
public partial class BatchAnalyzerForm : Form
{
    // ── 分析管线 ──
    private readonly List<Dictionary<string, object>> _results = new();
    private CancellationTokenSource? _cts;

    // ── 扫描结果 ──
    private List<string> _subjects = new();
    private Dictionary<string, int> _channelMap = new();

    // ── 数据集预设（路径基于项目根动态解析，避免硬编码绝对路径）──
    private static readonly (string label, string path, string desc, int[] validModes)[] DatasetPresets = BuildDatasetPresets();

    /// <summary>从程序目录向上探测项目根（含 sub-* 数据目录）</summary>
    private static string ResolveProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            try
            {
                if (Directory.GetDirectories(dir.FullName, "sub-*").Length > 0)
                    return dir.FullName;
            }
            catch { /* 忽略不可读目录，继续向上 */ }
            dir = dir.Parent;
        }
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static (string label, string path, string desc, int[] validModes)[] BuildDatasetPresets()
    {
        string root = ResolveProjectRoot();
        return new[]
        {
            ("ds001787 (冥想探针, BIDS)", root,
             "结构: sub-*/ses-*/eeg/*_eeg.bdf + *_events.tsv\n"
             + "可用模式: 刺激前10s Q1分组（每2分钟探头询问冥想深度）",
             new[] { 0 }),
            ("ds003969 (冥想Block, 外部数据)", Path.Combine(root, "external_data", "ds003969"),
             "结构: sub-*/eeg/*_eeg.bdf（每被试4个BDF: med1/med2/think1/think2）\n"
             + "可用模式: 全录音20s滑窗 Med/Think分组 | 不分条件",
             new[] { 1, 2 }),
            ("自定义路径", "",
             "手动输入路径，支持任意 BDF 文件\n"
             + "可用模式: 全录音20s滑窗（不分条件）",
             new[] { 2 }),
        };
    }

    // ── 常量 ──
    private static readonly string[] Standard12Channels = { "Fp1", "Fp2", "C3", "C4", "CP3", "CP4", "FCz", "Fz", "Cz", "Pz", "O1", "O2" };
    private const int SampleRate = 256;

    // ── 三个分析模式 ──
    private static readonly string[] ModeDescriptions =
    {
        "刺激前10s — Q1冥想深度分组\n"
        + "  ⚠ 仅 ds001787：每个 stimulus (value=128) 是 Q1 探头，取探头前 10s EEG\n"
        + "  按被试回答的 Q1 深度 (2/4/8) 分组，输出含 Q1 列的 CSV",

        "全录音20s滑窗 — 按条件分组 Med/Think\n"
        + "  ⚠ 仅 ds003969：每个 BDF 文件名含 med 或 think，整段20s滑窗\n"
        + "  输出含 condition 列 (meditation/thinking) 的 CSV",

        "全录音20s滑窗 — 不分条件\n"
        + "  ✅ 所有数据集通用：对每个 BDF 整段录音做 20s/10s 滑窗\n"
        + "  输出不含分组列，适合快速验证或单文件分析"
    };

    // ── UI 控件 ──
    private ComboBox _cbDataset = null!;
    private TextBox _txtDataDir = null!;
    private ComboBox _cbSubject = null!, _cbSession = null!;
    private CheckedListBox _clbChannels = null!;
    private NumericUpDown _numNfft = null!, _numNoverlap = null!, _numMaxFreq = null!;
    private ComboBox _cbMode = null!;
    private Label _lblModeHint = null!;
    private Button _btnScan = null!, _btnStart = null!, _btnStop = null!, _btnExport = null!;
    private ProgressBar _progressBar = null!;
    private Label _lblStatus = null!, _lblProgress = null!;
    private DataGridView _dgvResults = null!;

    // ── 新增：窗长/步长参数（模式2/3）──
    private NumericUpDown _numWindowSec = null!;
    private NumericUpDown _numStepSec = null!;
    private Label _lblWindow = null!, _lblStep = null!;

    // ── 新增：输出选项 ──
    private CheckBox _chkGenHeatmap = null!;
    private CheckBox _chkAutoCsv = null!;

    // ── 新增：算法验证按钮 ──
    private Button _btnValidate = null!;

    // ── 新增：输出目录 ──
    private static readonly string ResultsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results");

    // ── 新增：热力图累加器 ──
    private class BicoherenceAccumulator
    {
        public double[,] Sum = null!;
        public int Count;
        public double[] Frequencies = null!;
        public double FreqResolution;
        public int NumSegments;
    }
    private Dictionary<string, BicoherenceAccumulator>? _heatmapAccum;

    public BatchAnalyzerForm()
    {
        Text = "EEG 双相干性批量分析器";
        Size = new Size(1200, 900);
        StartPosition = FormStartPosition.CenterScreen;
        InitializeUI();
        Load += (_, _) => ApplyDatasetPreset(0); // 默认 ds001787
    }

    // ═════════════════════════════════════════════════════════
    //  UI 布局
    // ═════════════════════════════════════════════════════════

    private void InitializeUI()
    {
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4,
            SplitterDistance = 420
        };
        Controls.Add(mainSplit);

        var leftPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        mainSplit.Panel1.Controls.Add(leftPanel);

        int y = 8;

        var lblTitle = new Label { Text = "参数设置", Left = 8, Top = y, AutoSize = true, Font = new Font("Microsoft YaHei", 12, FontStyle.Bold) };
        leftPanel.Controls.Add(lblTitle);
        y += 30;

        // ── 数据集预设 ──
        leftPanel.Controls.Add(MakeLabel("数据集:", 8, y));
        y += 22;
        _cbDataset = new ComboBox
        {
            Left = 8, Top = y, Width = 395, DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        foreach (var (label, _, _, _) in DatasetPresets) _cbDataset.Items.Add(label);
        _cbDataset.SelectedIndexChanged += (_, _) => ApplyDatasetPreset(_cbDataset.SelectedIndex);
        leftPanel.Controls.Add(_cbDataset);
        y += 28;

        // ── 数据目录 ──
        leftPanel.Controls.Add(MakeLabel("数据目录:", 8, y));
        y += 22;
        _txtDataDir = new TextBox { Left = 8, Top = y, Width = 320, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        leftPanel.Controls.Add(_txtDataDir);
        var btnBrowse = new Button { Text = "浏览...", Left = 336, Top = y, Width = 70, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnBrowse.Click += (_, _) => BrowseDataDir();
        leftPanel.Controls.Add(btnBrowse);
        y += 28;

        _btnScan = new Button { Text = "扫描被试", Left = 8, Top = y, Width = 100 };
        _btnScan.Click += async (_, _) => await ScanSubjects();
        leftPanel.Controls.Add(_btnScan);
        y += 30;

        // ── 被试 / 会话 ──
        leftPanel.Controls.Add(MakeLabel("被试:", 8, y));
        _cbSubject = new ComboBox { Left = 60, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _cbSubject.SelectedIndexChanged += async (_, _) => await OnSubjectChanged();
        leftPanel.Controls.Add(_cbSubject);
        y += 26;

        leftPanel.Controls.Add(MakeLabel("会话:", 8, y));
        _cbSession = new ComboBox { Left = 60, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _cbSession.SelectedIndexChanged += async (_, _) => await OnSessionChanged();
        leftPanel.Controls.Add(_cbSession);
        y += 30;

        // ── 分析模式 ──
        leftPanel.Controls.Add(MakeLabel("分析模式:", 8, y));
        y += 22;
        _cbMode = new ComboBox
        {
            Left = 8, Top = y, Width = 395, DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        _cbMode.Items.AddRange(new object[] { ModeDescriptions[0], ModeDescriptions[1], ModeDescriptions[2] });
        _cbMode.SelectedIndexChanged += (_, _) => OnModeChanged();
        leftPanel.Controls.Add(_cbMode);
        y += 26;
        // 模式提示
        _lblModeHint = new Label { Left = 8, Top = y, AutoSize = true, ForeColor = Color.DarkGreen, Font = new Font("Microsoft YaHei", 8) };
        leftPanel.Controls.Add(_lblModeHint);
        y += 20;

        // ── 窗长/步长（仅模式2/3可见）──
        _lblWindow = MakeLabel("窗长 (秒):", 8, y);
        leftPanel.Controls.Add(_lblWindow);
        _numWindowSec = new NumericUpDown { Left = 140, Top = y, Width = 80, Minimum = 5, Maximum = 60, Value = 20, Increment = 5, DecimalPlaces = 0 };
        leftPanel.Controls.Add(_numWindowSec);
        y += 26;
        _lblStep = MakeLabel("步长 (秒):", 8, y);
        leftPanel.Controls.Add(_lblStep);
        _numStepSec = new NumericUpDown { Left = 140, Top = y, Width = 80, Minimum = 1, Maximum = 30, Value = 10, Increment = 1, DecimalPlaces = 0 };
        leftPanel.Controls.Add(_numStepSec);
        y += 30;

        // ── 通道选择 ──
        leftPanel.Controls.Add(MakeLabel("分析通道 (可多选):", 8, y));
        y += 22;
        _clbChannels = new CheckedListBox
        {
            Left = 8, Top = y, Width = 395, Height = 130,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, CheckOnClick = true
        };
        foreach (string ch in Standard12Channels) _clbChannels.Items.Add(ch, true);
        leftPanel.Controls.Add(_clbChannels);
        var btnAll = new Button { Text = "全选", Left = 8, Top = y + 134, Width = 55, Height = 22 };
        btnAll.Click += (_, _) => { for (int i = 0; i < _clbChannels.Items.Count; i++) _clbChannels.SetItemChecked(i, true); };
        leftPanel.Controls.Add(btnAll);
        var btnNone = new Button { Text = "全不选", Left = 66, Top = y + 134, Width = 55, Height = 22 };
        btnNone.Click += (_, _) => { for (int i = 0; i < _clbChannels.Items.Count; i++) _clbChannels.SetItemChecked(i, false); };
        leftPanel.Controls.Add(btnNone);
        y += 160;

        // ── 参数 ──
        leftPanel.Controls.Add(MakeLabel("FFT 点数 (Nfft):", 8, y));
        _numNfft = new NumericUpDown { Left = 140, Top = y, Width = 80, Minimum = 64, Maximum = 4096, Value = 512, Increment = 64 };
        leftPanel.Controls.Add(_numNfft);
        y += 26;
        leftPanel.Controls.Add(MakeLabel("重叠点数 (Noverlap):", 8, y));
        _numNoverlap = new NumericUpDown { Left = 140, Top = y, Width = 80, Minimum = 0, Maximum = 2048, Value = 256, Increment = 64 };
        leftPanel.Controls.Add(_numNoverlap);
        y += 26;
        leftPanel.Controls.Add(MakeLabel("最大频率 (Hz):", 8, y));
        _numMaxFreq = new NumericUpDown { Left = 140, Top = y, Width = 80, Minimum = 10, Maximum = 100, Value = 45, Increment = 5 };
        leftPanel.Controls.Add(_numMaxFreq);
        y += 30;

        // ── 输出选项 ──
        _chkGenHeatmap = new CheckBox { Text = "生成热力图 PNG", Left = 8, Top = y, AutoSize = true, Checked = false };
        leftPanel.Controls.Add(_chkGenHeatmap);
        _chkAutoCsv = new CheckBox { Text = "自动导出 CSV", Left = 180, Top = y, AutoSize = true, Checked = false };
        leftPanel.Controls.Add(_chkAutoCsv);
        y += 26;

        // ── 按钮 ──
        _btnStart = new Button
        {
            Text = "▶ 开始分析", Left = 8, Top = y, Width = 110, Height = 36,
            BackColor = Color.FromArgb(46, 139, 87), ForeColor = Color.White,
            Font = new Font("Microsoft YaHei", 10, FontStyle.Bold)
        };
        _btnStart.Click += async (_, _) => await StartAnalysis();
        leftPanel.Controls.Add(_btnStart);
        _btnStop = new Button
        {
            Text = "■ 停止", Left = 124, Top = y, Width = 70, Height = 36,
            BackColor = Color.FromArgb(220, 53, 69), ForeColor = Color.White,
            Enabled = false, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
        };
        _btnStop.Click += (_, _) => _cts?.Cancel();
        leftPanel.Controls.Add(_btnStop);
        _btnExport = new Button { Text = "导出 CSV", Left = 200, Top = y, Width = 85, Height = 36, Enabled = false };
        _btnExport.Click += (_, _) => ExportCsv();
        leftPanel.Controls.Add(_btnExport);
        _btnValidate = new Button { Text = "算法验证", Left = 292, Top = y, Width = 85, Height = 36 };
        _btnValidate.Click += (_, _) => RunValidation();
        leftPanel.Controls.Add(_btnValidate);
        y += 44;

        // ── 进度 ──
        _progressBar = new ProgressBar
        {
            Left = 8, Top = y, Width = 396, Height = 22, Style = ProgressBarStyle.Continuous,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };
        leftPanel.Controls.Add(_progressBar);
        y += 26;
        _lblProgress = new Label { Left = 8, Top = y, AutoSize = true, Text = "就绪", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
        leftPanel.Controls.Add(_lblProgress);

        // ── 右面板：结果区 ──
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        mainSplit.Panel2.Controls.Add(rightPanel);
        var lblResultTitle = new Label { Text = "分析结果", Left = 8, Top = 6, AutoSize = true, Font = new Font("Microsoft YaHei", 12, FontStyle.Bold) };
        rightPanel.Controls.Add(lblResultTitle);
        _lblStatus = new Label { Left = 120, Top = 10, AutoSize = true, ForeColor = Color.Gray, Text = "请先选择数据集，扫描被试目录" };
        rightPanel.Controls.Add(_lblStatus);
        _dgvResults = new DataGridView
        {
            Left = 8, Top = 36, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        rightPanel.Controls.Add(_dgvResults);
        rightPanel.Resize += (_, _) => { _dgvResults.Width = rightPanel.Width - 16; _dgvResults.Height = rightPanel.Height - 44; };

        OnModeChanged();
    }

    private static Label MakeLabel(string text, int x, int y)
        => new() { Text = text, Left = x, Top = y + 2, AutoSize = true };

    // ═════════════════════════════════════════════════════════
    //  数据集切换
    // ═════════════════════════════════════════════════════════

    private void ApplyDatasetPreset(int idx)
    {
        if (idx < 0 || idx >= DatasetPresets.Length) return;
        var (_, path, desc, validModes) = DatasetPresets[idx];
        _txtDataDir.Text = path;
        _lblStatus.Text = desc.Replace("\n", " | ");

        // 强制选第一个有效模式
        int firstValid = validModes.Length > 0 ? validModes[0] : 2;
        _cbMode.SelectedIndex = firstValid;
        OnModeChanged();

        // 根据数据集类型决定是否显示 "会话" 选择
        bool hasSessions = idx == 0; // ds001787 有 ses-*
        _cbSession.Visible = hasSessions;
        var sesLabel = _cbSession.Parent?.Controls.OfType<Label>()
            .FirstOrDefault(l => l.Text == "会话:");
        if (sesLabel != null) sesLabel.Visible = hasSessions;
    }

    private void OnModeChanged()
    {
        int mode = _cbMode.SelectedIndex;
        if (mode >= 0 && mode < ModeDescriptions.Length)
            _lblModeHint.Text = ModeDescriptions[mode];
        else
            _lblModeHint.Text = "";

        // 窗长/步长仅模式2/3（滑窗）可见
        bool showSlideParams = mode == 1 || mode == 2;
        _lblWindow.Visible = showSlideParams;
        _numWindowSec.Visible = showSlideParams;
        _lblStep.Visible = showSlideParams;
        _numStepSec.Visible = showSlideParams;
    }

    // ═════════════════════════════════════════════════════════
    //  数据扫描
    // ═════════════════════════════════════════════════════════

    private void BrowseDataDir()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择数据根目录" };
        if (dlg.ShowDialog() == DialogResult.OK)
            _txtDataDir.Text = dlg.SelectedPath;
    }

    private async Task ScanSubjects()
    {
        string root = _txtDataDir.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show("数据目录不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _subjects = Directory.GetDirectories(root, "sub-*")
            .Select(Path.GetFileName).Where(d => d != null).Cast<string>()
            .OrderBy(s => int.Parse(s.Replace("sub-", ""))).ToList();

        _cbSubject.Items.Clear();
        foreach (var s in _subjects) _cbSubject.Items.Add(s);
        if (_subjects.Count > 0) _cbSubject.SelectedIndex = 0;
        _lblStatus.Text = $"已扫描: {_subjects.Count} 个被试";
    }

    private async Task OnSubjectChanged()
    {
        if (_cbSubject.SelectedItem is not string sub) return;
        string subDir = Path.Combine(_txtDataDir.Text.Trim(), sub);
        _cbSession.Items.Clear();

        if (!Directory.Exists(subDir)) return;

        // ds001787: 有 ses-* 子目录
        var sessions = Directory.GetDirectories(subDir, "ses-*")
            .Select(Path.GetFileName).Where(d => d != null).Cast<string>()
            .OrderBy(x => x).ToList();
        foreach (var s in sessions) _cbSession.Items.Add(s);

        // ds003969 或自定义: 没有 ses-*, eeg 直接在 subDir 下
        if (sessions.Count == 0 && Directory.Exists(Path.Combine(subDir, "eeg")))
            _cbSession.Items.Add("(无会话层级)");

        if (_cbSession.Items.Count > 0) _cbSession.SelectedIndex = 0;
    }

    private async Task OnSessionChanged()
    {
        await ScanChannels();
    }

    private async Task ScanChannels()
    {
        _channelMap.Clear();
        if (_cbSubject.SelectedItem is not string sub) return;

        string eegDir = GetEegDirectory(sub);
        if (!Directory.Exists(eegDir)) return;

        var bdfFiles = Directory.GetFiles(eegDir, "*_eeg.bdf");
        if (bdfFiles.Length == 0) return;

        try
        {
            using var reader = new BdfReader(bdfFiles[0]);
            string[] labels = reader.GetChannelNames();

            // 尝试 channels.tsv 映射
            string channelsTsv = Path.Combine(_txtDataDir.Text.Trim(), "task-meditation_channels.tsv");
            if (!File.Exists(channelsTsv))
                channelsTsv = Path.Combine(Path.GetDirectoryName(_txtDataDir.Text.Trim()) ?? "", "task-meditation_channels.tsv");

            if (File.Exists(channelsTsv))
            {
                string[] standardNames = File.ReadAllLines(channelsTsv).Skip(1)
                    .Select(l => l.Split('\t')[0].Trim()).ToArray();
                for (int i = 0; i < standardNames.Length && i < labels.Length; i++)
                    _channelMap[standardNames[i]] = i;
            }
            else
            {
                for (int i = 0; i < labels.Length; i++)
                    _channelMap[labels[i]] = i;
            }
            _lblStatus.Text = $"BDF 通道: {labels.Length}, 可分析: {_channelMap.Count} 通道";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"扫描通道失败: {ex.Message}";
        }
    }

    /// <summary>根据被试 ID 和当前数据集类型，定位 eeg 目录</summary>
    private string GetEegDirectory(string sub)
    {
        string subDir = Path.Combine(_txtDataDir.Text.Trim(), sub);
        string ses = _cbSession.SelectedItem as string ?? "";

        if (ses.StartsWith("ses-"))
            return Path.Combine(subDir, ses, "eeg");
        else
            return Path.Combine(subDir, "eeg");
    }

    // ═════════════════════════════════════════════════════════
    //  分析执行
    // ═════════════════════════════════════════════════════════

    /// <summary>启用/禁用分析期间控件</summary>
    private void SetControlsEnabled(bool enabled)
    {
        _cbDataset.Enabled = enabled;
        _txtDataDir.Enabled = enabled;
        _cbSubject.Enabled = enabled;
        _cbSession.Enabled = enabled;
        _cbMode.Enabled = enabled;
        _clbChannels.Enabled = enabled;
        _numNfft.Enabled = enabled;
        _numNoverlap.Enabled = enabled;
        _numMaxFreq.Enabled = enabled;
        _numWindowSec.Enabled = enabled;
        _numStepSec.Enabled = enabled;
        _chkGenHeatmap.Enabled = enabled;
        _chkAutoCsv.Enabled = enabled;
        _btnScan.Enabled = enabled;
        _btnStart.Enabled = enabled;
        _btnValidate.Enabled = enabled;
        _btnExport.Enabled = !enabled && _results.Count > 0;
    }

    private async Task StartAnalysis()
    {
        if (_cbSubject.SelectedItem is not string sub)
        {
            MessageBox.Show("请先扫描并选择被试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selectedChannels = _clbChannels.CheckedItems.Cast<string>().ToList();
        if (selectedChannels.Count == 0)
        {
            MessageBox.Show("请至少选择一个分析通道。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int mode = _cbMode.SelectedIndex;
        int presetIdx = _cbDataset.SelectedIndex;
        var validModes = presetIdx >= 0 && presetIdx < DatasetPresets.Length
            ? DatasetPresets[presetIdx].validModes : new[] { 2 };

        if (!validModes.Contains(mode))
        {
            string validNames = string.Join(" / ", validModes.Select(m => $"模式{m + 1}"));
            MessageBox.Show($"当前数据集不支持此分析模式。\n\n可用模式: {validNames}", "模式不兼容", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string eegDir = GetEegDirectory(sub);
        if (!Directory.Exists(eegDir))
        {
            MessageBox.Show($"EEG 目录不存在:\n{eegDir}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var bdfFiles = Directory.GetFiles(eegDir, "*_eeg.bdf").OrderBy(f => f).ToArray();
        if (bdfFiles.Length == 0)
        {
            MessageBox.Show("未找到 BDF 文件。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 模式0 (Q1分组) 必须有 events.tsv
        var tsvFiles = Directory.GetFiles(eegDir, "*_events.tsv");
        if (mode == 0 && tsvFiles.Length == 0)
        {
            MessageBox.Show("模式 'Q1分组' 需要 events.tsv 文件。\n请确认数据集是 ds001787 探针范式。", "缺少事件文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _results.Clear();
        _dgvResults.DataSource = null;
        _heatmapAccum = _chkGenHeatmap.Checked ? new Dictionary<string, BicoherenceAccumulator>() : null;
        SetControlsEnabled(false);
        _btnStop.Enabled = true;
        _cts = new CancellationTokenSource();

        int nfft = (int)_numNfft.Value;
        int noverlap = (int)_numNoverlap.Value;
        double maxFreq = (double)_numMaxFreq.Value;
        double windowSec = (double)_numWindowSec.Value;
        double stepSec = (double)_numStepSec.Value;

        try
        {
            // ── 模式0: ds001787 刺激前10s Q1分组 ──
            if (mode == 0)
            {
                await RunStimulusMode(sub, bdfFiles[0], tsvFiles[0], selectedChannels, nfft, noverlap, maxFreq);
            }
            // ── 模式1 & 2: 全录音滑窗 ──
            else
            {
                await RunSlidingWindowMode(sub, bdfFiles, selectedChannels, mode, nfft, noverlap, maxFreq, windowSec, stepSec);
            }

            // 生成热力图
            if (_heatmapAccum != null && _heatmapAccum.Count > 0)
            {
                _lblStatus.Text = "正在生成热力图...";
                await Task.Run(() => GenerateHeatmaps(sub));
            }

            _lblStatus.Text = _cts.IsCancellationRequested
                ? $"已停止 — {_results.Count} 个 epoch"
                : $"完成 — {_results.Count} 个 epoch, {selectedChannels.Count} 通道";

            ShowResults();

            // 自动导出 CSV
            if (_chkAutoCsv.Checked && _results.Count > 0)
                AutoExportCsv(sub);
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "分析已取消";
            ShowResults();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"错误: {ex.Message}";
            MessageBox.Show($"分析失败:\n\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetControlsEnabled(true);
            _btnStop.Enabled = false;
            _progressBar.Value = 0;
            _lblProgress.Text = "就绪";
            _btnExport.Enabled = _results.Count > 0;
        }
    }

    // ── 模式0: 刺激前10s Q1分组 ──
    private async Task RunStimulusMode(string sub, string bdfPath, string eventsPath,
        List<string> selectedChannels, int nfft, int noverlap, double maxFreq)
    {
        using var bdfReader = new BdfReader(bdfPath);
        int bdfSampleRate = (int)bdfReader.Channels[0].SamplesPerRecord;
        string ses = _cbSession.SelectedItem as string ?? "ses-01";

        var chIndices = BuildChannelIndices(selectedChannels);
        if (chIndices.Count == 0) chIndices = BuildChannelIndicesFromLabels(bdfReader, selectedChannels);
        if (chIndices.Count == 0) { MessageBox.Show("未找到目标通道。\n\n请确认 BDF 通道标签与所选通道名匹配。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        var eventReader = new EventReader(eventsPath);
        var segments = eventReader.ExtractPreStimulusSegments(10.0);
        var epochs = segments.Select(s => (s.StartTimeSec, (double)s.DurationSec, $"Q1={s.Q1Response}", s.StimulusIndex, (int?)s.Q1Response)).ToList();
        _lblStatus.Text = $"[Q1分组] {epochs.Count} 个 10s epoch";
        if (epochs.Count == 0) { MessageBox.Show("未提取到任何 epoch。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var calculator = new BispectrumCalculator(SampleRate, nfft, noverlap, maxFreq);
        int totalSteps = chIndices.Count * epochs.Count;
        _progressBar.Maximum = totalSteps;
        var progress = new Progress<(int done, int total, string info)>(p =>
        {
            _progressBar.Value = Math.Min(p.done, _progressBar.Maximum);
            _lblProgress.Text = $"{p.done}/{p.total}  {p.info}";
        });

        await Task.Run(() =>
        {
            int done = 0;
            foreach (var chKv in chIndices)
            {
                if (_cts!.IsCancellationRequested) break;
                foreach (var ep in epochs)
                {
                    if (_cts.IsCancellationRequested) break;
                    double[] data = bdfReader.ReadChannelDataByTime(chKv.Value, ep.Item1, ep.Item2, bdfSampleRate);
                    if (data.Length < nfft) continue;
                    {   // 去直流偏置，防止残留 DC 污染低频双相干 (peak_diag 恒为 100)
                        double mean = data.Average();
                        for (int si = 0; si < data.Length; si++) data[si] -= mean;
                    }
                    data = EegFilter.Filter(data);
                    var result = calculator.Compute(data);
                    var metrics = ComputeBandMetricsStatic(result);
                    var row = new Dictionary<string, object> {
                        ["subject"] = sub, ["session"] = ses, ["channel"] = chKv.Key,
                        ["epoch_id"] = ep.Item4, ["condition"] = ep.Item3,
                        ["start_sec"] = Math.Round(ep.Item1, 1),
                        ["sample_rate"] = SampleRate, ["n_segments"] = result.NumSegments,
                    };
                    foreach (var kvM in metrics) row[kvM.Key] = kvM.Value;
                    if (ep.Item5.HasValue) row["Q1"] = ep.Item5.Value;
                    lock (_results) _results.Add(row);
                    // 累加热力图
                    if (_heatmapAccum != null)
                    {
                        string hKey = $"{chKv.Key}|Q1={ep.Item5}";
                        AccumulateBicoherence(hKey, result);
                    }
                    done++;
                    if (done % 10 == 0) ((IProgress<(int, int, string)>)progress).Report((done, totalSteps, $"{chKv.Key}"));
                }
            }
        }, _cts!.Token);
    }

    // ── 模式1 & 2: 全录音滑窗 ──
    private async Task RunSlidingWindowMode(string sub, string[] bdfFiles, List<string> selectedChannels,
        int mode, int nfft, int noverlap, double maxFreq, double epochSec, double stepSec)
    {

        // 先统计总工作量
        int totalSteps = 0;
        var fileEpochs = new List<(string bdfPath, string condition, List<(double start, double dur)> epochs)>();
        foreach (string bdfPath in bdfFiles)
        {
            using var reader = new BdfReader(bdfPath);
            int bdfSr = (int)reader.Channels[0].SamplesPerRecord;
            double totalDur = reader.RecordDuration * reader.NumDataRecords;

            string fileName = Path.GetFileNameWithoutExtension(bdfPath);
            string condition = mode == 1
                ? (fileName.ToUpperInvariant().Contains("MED") ? "meditation" : "thinking")
                : "full_recording";

            var triggerEvents = reader.ExtractTriggerEvents(bdfSr);
            var blockEpochs = EventReader.ExtractBlockEpochs(triggerEvents, totalDur, epochSec, stepSec);
            var epochList = new List<(double start, double dur)>();
            if (blockEpochs.Count > 0 && blockEpochs[0].DurationSec > 1.0)
            {
                foreach (var be in blockEpochs) epochList.Add((be.StartTimeSec, be.DurationSec));
            }
            else
            {
                for (double t = 0; t + epochSec <= totalDur; t += stepSec)
                    epochList.Add((t, epochSec));
            }
            fileEpochs.Add((bdfPath, condition, epochList));
            totalSteps += selectedChannels.Count * epochList.Count;
        }

        if (totalSteps == 0) { MessageBox.Show("未生成任何 epoch。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        _lblStatus.Text = $"[滑窗 {epochSec}s/{stepSec}s] {bdfFiles.Length} BDF, ~{totalSteps} epoch";
        _progressBar.Maximum = totalSteps;
        var progress = new Progress<(int done, int total, string info)>(p =>
        {
            _progressBar.Value = Math.Min(p.done, _progressBar.Maximum);
            _lblProgress.Text = $"{p.done}/{p.total}  {p.info}";
        });

        string ses = _cbSession.SelectedItem as string ?? "";

        await Task.Run(() =>
        {
            int done = 0;
            foreach (var (bdfPath, condition, epochList) in fileEpochs)
            {
                if (_cts!.IsCancellationRequested) break;
                string taskName = Path.GetFileNameWithoutExtension(bdfPath).Replace("_eeg", "");
                using var reader = new BdfReader(bdfPath);
                int bdfSr = (int)reader.Channels[0].SamplesPerRecord;

                var chIndices = BuildChannelIndices(selectedChannels);
                if (chIndices.Count == 0) chIndices = BuildChannelIndicesFromLabels(reader, selectedChannels);
                var calculator = new BispectrumCalculator(SampleRate, nfft, noverlap, maxFreq);

                foreach (var chKv in chIndices)
                {
                    if (_cts.IsCancellationRequested) break;
                    int epochIdx = 0;
                    foreach (var (start, dur) in epochList)
                    {
                        if (_cts.IsCancellationRequested) break;
                        double[] data = reader.ReadChannelDataByTime(chKv.Value, start, dur, bdfSr);
                        if (data.Length < nfft) { epochIdx++; continue; }
                        if (bdfSr != SampleRate)
                        {
                            int factor = bdfSr / SampleRate;
                            if (factor > 1) { var ds = new double[data.Length / factor]; for (int di = 0; di < ds.Length; di++) ds[di] = data[di * factor]; data = ds; }
                        }
                        {   // 去直流偏置
                            double mean = data.Average();
                            for (int si = 0; si < data.Length; si++) data[si] -= mean;
                        }
                        data = EegFilter.Filter(data);
                        var result = calculator.Compute(data);
                        var metrics = ComputeBandMetricsStatic(result);
                        var row = new Dictionary<string, object> {
                            ["subject"] = sub, ["session"] = ses, ["task"] = taskName,
                            ["channel"] = chKv.Key, ["condition"] = condition, ["epoch_id"] = epochIdx,
                            ["start_sec"] = Math.Round(start, 1),
                            ["sample_rate"] = SampleRate, ["n_segments"] = result.NumSegments,
                        };
                        foreach (var kvM in metrics) row[kvM.Key] = kvM.Value;
                        lock (_results) _results.Add(row);
                        // 累加热力图
                        if (_heatmapAccum != null)
                        {
                            string hKey = $"{chKv.Key}|{condition}";
                            lock (_heatmapAccum) AccumulateBicoherence(hKey, result);
                        }
                        done++; epochIdx++;
                        if (done % 20 == 0) ((IProgress<(int, int, string)>)progress).Report((done, totalSteps, $"{taskName}/{chKv.Key}"));
                    }
                }
            }
        }, _cts!.Token);
    }

    /// <summary>使用 channels.tsv 映射表将标准通道名 → BDF 索引</summary>
    private Dictionary<string, int> BuildChannelIndices(List<string> selectedChannels)
    {
        var result = new Dictionary<string, int>();
        // 优先使用 channels.tsv 映射 (标准名 → 索引)
        foreach (var ch in selectedChannels)
        {
            if (_channelMap.TryGetValue(ch, out int idx))
                result[ch] = idx;
        }
        // 兜底：直接用 BDF 标签匹配 (适用于 ds003969 等有语义标签的数据集)
        if (result.Count == 0)
        {
            // 需要通过 BDF reader 获取标签，但这里没有 reader；
            // 如果 _channelMap 也为空，调用方应传入 reader 回退
        }
        return result;
    }

    /// <summary>兜底：直接从 BDF 标签名匹配通道</summary>
    private static Dictionary<string, int> BuildChannelIndicesFromLabels(BdfReader reader, List<string> selectedChannels)
    {
        var result = new Dictionary<string, int>();
        string[] labels = reader.GetChannelNames();
        for (int i = 0; i < labels.Length; i++)
            if (selectedChannels.Contains(labels[i], StringComparer.OrdinalIgnoreCase))
                result[labels[i]] = i;
        return result;
    }

    // ═════════════════════════════════════════════════════════
    //  结果显示 & 导出
    // ═════════════════════════════════════════════════════════

    private void ShowResults()
    {
        if (_results.Count == 0) return;
        var dt = new DataTable();
        var colOrder = new List<string>(); var colSet = new HashSet<string>();
        foreach (var row in _results) foreach (var k in row.Keys) { if (colSet.Add(k)) colOrder.Add(k); }
        foreach (var c in colOrder) dt.Columns.Add(c, typeof(string));
        var ci = CultureInfo.InvariantCulture;
        foreach (var row in _results)
        {
            var dr = dt.NewRow();
            foreach (var c in colOrder)
                dr[c] = row.TryGetValue(c, out var v) && v != null ? (v is double d ? d.ToString("F2", ci) : v.ToString()!) : "";
            dt.Rows.Add(dr);
        }
        _dgvResults.DataSource = dt;
        _dgvResults.AutoResizeColumns();
    }

    private void ExportCsv()
    {
        if (_results.Count == 0) return;
        using var sfd = new SaveFileDialog { Title = "导出分析结果", Filter = "CSV文件|*.csv", FileName = $"bicoherence_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (sfd.ShowDialog() != DialogResult.OK) return;
        var ci = CultureInfo.InvariantCulture;
        var colOrder = new List<string>(); var colSet = new HashSet<string>();
        foreach (var row in _results) foreach (var k in row.Keys) { if (colSet.Add(k)) colOrder.Add(k); }
        using var sw = new StreamWriter(sfd.FileName, false, System.Text.Encoding.UTF8);
        sw.WriteLine(string.Join(",", colOrder));
        foreach (var row in _results)
        {
            var vals = colOrder.Select(c => row.TryGetValue(c, out var v) && v != null ? (v is double d ? d.ToString("F4", ci) : v.ToString()!) : "");
            sw.WriteLine(string.Join(",", vals));
        }
        _lblStatus.Text = $"已导出 {_results.Count} 行 → {Path.GetFileName(sfd.FileName)}";
        MessageBox.Show($"已导出 {_results.Count} 行到:\n{sfd.FileName}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ═════════════════════════════════════════════════════════
    //  热力图生成
    // ═════════════════════════════════════════════════════════

    /// <summary>将 BispectrumResult 累加到指定 key 的累加器中</summary>
    private void AccumulateBicoherence(string key, BispectrumResult result)
    {
        if (_heatmapAccum == null) return;
        if (!_heatmapAccum.TryGetValue(key, out var acc))
        {
            int n = result.Bicoherence.GetLength(0);
            acc = new BicoherenceAccumulator
            {
                Sum = new double[n, n],
                Count = 0,
                Frequencies = (double[])result.Frequencies.Clone(),
                FreqResolution = result.FreqResolution,
                NumSegments = result.NumSegments
            };
            _heatmapAccum[key] = acc;
        }
        int size = result.Bicoherence.GetLength(0);
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++)
            {
                double v = result.Bicoherence[i, j];
                if (!double.IsNaN(v)) acc.Sum[i, j] += v;
            }
        acc.Count++;
    }

    /// <summary>从累加器生成 PNG 热力图</summary>
    private void GenerateHeatmaps(string sub)
    {
        if (_heatmapAccum == null || _heatmapAccum.Count == 0) return;
        var visualizer = new Visualizer();
        string heatmapDir = Path.Combine(ResultsRoot, "heatmaps", sub);
        Directory.CreateDirectory(heatmapDir);

        foreach (var kv in _heatmapAccum)
        {
            var acc = kv.Value;
            if (acc.Count == 0) continue;

            // 计算平均 bicoherence
            int n = acc.Sum.GetLength(0);
            double[,] avgBico = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    avgBico[i, j] = acc.Sum[i, j] / acc.Count;

            // 构建简化版 BispectrumResult
            var avgResult = new BispectrumResult
            {
                Bicoherence = avgBico,
                Frequencies = acc.Frequencies,
                FreqResolution = acc.FreqResolution,
                MaxFreqIndex = acc.Frequencies.Length - 1,
                NumSegments = acc.Count * acc.NumSegments,
                Bispectrum = new Complex[0, 0],
                BispectrumMagnitude = new double[0, 0]
            };

            // key 格式: "通道|分组"
            string safeKey = kv.Key.Replace('|', '_').Replace("=", "");
            string pngPath = Path.Combine(heatmapDir, $"bicoherence_{safeKey}.png");
            string[] parts = kv.Key.Split('|');
            string chName = parts.Length > 0 ? parts[0] : kv.Key;
            string groupLabel = parts.Length > 1 ? parts[1] : "all";

            visualizer.SaveBicoherenceHeatmap(avgResult, pngPath, chName, groupLabel, 40.0);
        }
    }

    /// <summary>自动导出 CSV（不弹对话框）</summary>
    private void AutoExportCsv(string sub)
    {
        if (_results.Count == 0) return;
        string csvDir = Path.Combine(ResultsRoot, "csv");
        Directory.CreateDirectory(csvDir);
        string fileName = $"bicoherence_{sub}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string csvPath = Path.Combine(csvDir, fileName);
        var ci = CultureInfo.InvariantCulture;
        var colOrder = new List<string>(); var colSet = new HashSet<string>();
        foreach (var row in _results) foreach (var k in row.Keys) { if (colSet.Add(k)) colOrder.Add(k); }
        using var sw = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
        sw.WriteLine(string.Join(",", colOrder));
        foreach (var row in _results)
        {
            var vals = colOrder.Select(c => row.TryGetValue(c, out var v) && v != null ? (v is double d ? d.ToString("F4", ci) : v.ToString()!) : "");
            sw.WriteLine(string.Join(",", vals));
        }
        _lblStatus.Text = $"已自动导出 {_results.Count} 行 → {fileName}";
    }

    // ═════════════════════════════════════════════════════════
    //  算法验证
    // ═════════════════════════════════════════════════════════

    private void RunValidation()
    {
        _btnValidate.Enabled = false;
        _btnValidate.Text = "验证中...";
        Task.Run(() =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var originalOut = Console.Out;
                using (var sw = new StringWriter(sb))
                {
                    Console.SetOut(sw);
                    ValidationRunner.RunAll();
                    Console.SetOut(originalOut);
                }
                string resultText = sb.ToString();
                Invoke(() =>
                {
                    _btnValidate.Enabled = true;
                    _btnValidate.Text = "算法验证";
                    using var dlg = new Form
                    {
                        Text = "算法验证结果",
                        Size = new Size(700, 500),
                        StartPosition = FormStartPosition.CenterParent,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        MaximizeBox = false, MinimizeBox = false
                    };
                    var txt = new TextBox
                    {
                        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                        Font = new Font("Consolas", 10), ScrollBars = ScrollBars.Vertical,
                        Text = resultText
                    };
                    dlg.Controls.Add(txt);
                    dlg.ShowDialog(this);
                });
            }
            catch (Exception ex)
            {
                Invoke(() =>
                {
                    _btnValidate.Enabled = true;
                    _btnValidate.Text = "算法验证";
                    MessageBox.Show($"验证失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
        });
    }

    // ═════════════════════════════════════════════════════════
    //  指标提取
    // ═════════════════════════════════════════════════════════

    private static Dictionary<string, double> ComputeBandMetricsStatic(BispectrumResult result)
    {
        var m = new Dictionary<string, double>();
        double[,] b = result.Bicoherence; double[] f = result.Frequencies; int n = b.GetLength(0);
        var bands = new[] { ("delta", 1.0, 4.0), ("theta", 4.0, 7.0), ("alpha", 8.0, 13.0), ("beta", 13.0, 25.0), ("gamma", 25.0, 40.0) };
        double dSum = 0; int dCnt = 0;
        foreach (var (bn, lo, hi) in bands)
        {
            double s = 0; int c = 0;
            for (int i = 0; i < n; i++) { if (f[i] >= lo && f[i] <= hi) { double v = b[i, i]; if (!double.IsNaN(v)) { s += v; c++; } } }
            m[$"mean_diag_{bn}"] = c > 0 ? Math.Round(s / c * 100, 4) : double.NaN; dSum += s; dCnt += c;
        }
        m["mean_diag"] = dCnt > 0 ? Math.Round(dSum / dCnt * 100, 4) : double.NaN;
        double tSum = 0; int tCnt = 0;
        for (int f1 = 0; f1 < n; f1++) for (int f2 = 0; f2 < n; f2++) { if (f1 + f2 >= n) break; double v = b[f1, f2]; if (!double.IsNaN(v)) { tSum += v; tCnt++; } }
        m["mean_all"] = tCnt > 0 ? Math.Round(tSum / tCnt * 100, 4) : double.NaN;
        double oSum = 0; int oCnt = 0;
        for (int f1 = 0; f1 < n; f1++) for (int f2 = 0; f2 < n; f2++) { if (f1 + f2 >= n) break; if (f1 == f2) continue; double v = b[f1, f2]; if (!double.IsNaN(v)) { oSum += v; oCnt++; } }
        m["mean_offdiag"] = oCnt > 0 ? Math.Round(oSum / oCnt * 100, 4) : double.NaN;
        double taS = 0; int taC = 0;
        for (int f1 = 0; f1 < n; f1++) for (int f2 = 0; f2 < n; f2++) { if (f1 + f2 >= n) break; if (f[f1] < 4 || f[f1] > 7) continue; if (f[f2] < 8 || f[f2] > 12) continue; double v = b[f1, f2]; if (!double.IsNaN(v)) { taS += v; taC++; } }
        m["theta_alpha_coupling"] = taC > 0 ? Math.Round(taS / taC * 100, 4) : double.NaN;
        double abS = 0; int abC = 0;
        for (int f1 = 0; f1 < n; f1++) for (int f2 = 0; f2 < n; f2++) { if (f1 + f2 >= n) break; if (f[f1] < 8 || f[f1] > 13) continue; if (f[f2] < 13 || f[f2] > 25) continue; double v = b[f1, f2]; if (!double.IsNaN(v)) { abS += v; abC++; } }
        m["alpha_beta_coupling"] = abC > 0 ? Math.Round(abS / abC * 100, 4) : double.NaN;
        double pV = 0, pF = 0;
        for (int i = 0; i < n; i++) { if (f[i] < 1.0) continue; double v = b[i, i]; if (!double.IsNaN(v) && v > pV) { pV = v; pF = f[i]; } }
        m["peak_diag"] = Math.Round(pV * 100, 4); m["peak_diag_freq"] = Math.Round(pF, 2);
        return m;
    }
}
