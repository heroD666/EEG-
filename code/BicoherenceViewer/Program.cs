using System;
using System.Drawing;
using System.Windows.Forms;
using BicoherenceViewer;

internal static class Program
{
    /// <summary>
    /// [STAThread] 是 WebView2 在 .NET Framework 4.8 下正常工作的必要条件。
    /// </summary>
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (s, e) =>
            MessageBox.Show($"未处理异常:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            string msg = e.ExceptionObject is Exception ex ? $"{ex.Message}\n\n{ex.StackTrace}" : (e.ExceptionObject.ToString() ?? "未知错误");
            MessageBox.Show(msg, "致命错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            // 启动入口：选择分析模式
            var selector = new StartupSelector();
            Application.Run(selector);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败:\n{ex.Message}\n\n{ex.StackTrace}", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>启动入口选择器</summary>
internal class StartupSelector : Form
{
    public StartupSelector()
    {
        Text = "EEG 双相干性分析工具";
        Size = new Size(460, 260);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl = new Label
        {
            Text = "请选择分析模式:",
            Left = 30, Top = 24, AutoSize = true,
            Font = new Font("Microsoft YaHei", 12, FontStyle.Bold)
        };
        Controls.Add(lbl);

        var btnBatch = new Button
        {
            Text = "📊 批量分析器\n(选择数据 → 一键出 CSV)",
            Left = 30, Top = 64, Width = 380, Height = 56,
            Font = new Font("Microsoft YaHei", 10),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(46, 139, 87),
            ForeColor = Color.White
        };
        btnBatch.Click += (_, _) =>
        {
            Visible = false;
            var f = new BatchAnalyzerForm();
            f.FormClosed += (_, _) => Close();
            f.Show();
        };
        Controls.Add(btnBatch);

        var btnViewer = new Button
        {
            Text = "🔬 交互式查看器\n(可视化对比 双相干热力图 / 3D曲面 / 对角线)",
            Left = 30, Top = 132, Width = 380, Height = 56,
            Font = new Font("Microsoft YaHei", 10),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(64, 64, 64),
            ForeColor = Color.White
        };
        btnViewer.Click += (_, _) =>
        {
            Visible = false;
            var f = new MainForm();
            f.FormClosed += (_, _) => Close();
            f.Show();
        };
        Controls.Add(btnViewer);
    }
}
