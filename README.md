# EEG 双相干性冥想分析（EEG Bicoherence Meditation Analysis）

基于 **C#（.NET Framework 4.8）** 实现的 EEG 双谱 / 双相干性（bispectrum / bicoherence）分析管线，
配套 Python 统计与可视化脚本，用于探究**冥想状态对 EEG 非线性相位耦合的影响**。

## 核心结论

跨两个独立公开数据集的统一分析（ds001787 探针范式：12 名被试 / 6,432 epochs；ds003969 Block 设计：10 名被试 / 24,936 epochs），
**未发现冥想状态对双相干性具有显著且可重复的影响**。详见
[完整分析报告](results/final_report_figures/bicoherence_analysis_report.md)。

![ds003969 Med vs Think 效应量](results/final_report_figures/fig2_ds003969_forest.png)

## 功能特性

- **信号管线**：BDF 读取（Biosemi 24-bit，自研 BdfReader）→ 去直流 → 1–45 Hz 带通滤波 → Welch 分段双谱计算
  （Nfft = 512，50% 重叠，Hanning 窗）→ 双相干性归一化（N³·σ³，Haubrich 1965）
- **特征提取**：δ/θ/α/β/γ 频段对角双相干 + θ-α / α-β 跨频段耦合
- **三种分析模式**：
  1. 刺激前窗口分析 — 按 Q1 冥想深度评分（2/4/8）分组，输出含 Q1 列的 CSV
  2. Block 设计全录音 20 s 滑窗 — Meditation vs Thinking 条件分组
  3. 通用滑窗 — 任意 BDF 文件快速分析
- **统计验证**：Surrogate 显著性检验、伪迹检测（ArtifactDetector）
- **批量分析 GUI**：WinForms + ScottPlot 2D + WebView2 / ECharts 3D 曲面图
- **Python 工具**：epoch 级双相干趋势图、组水平统计检验

## 目录结构

```
├── code/
│   ├── BicoherenceAnalyzer/        # 核心计算管线 + CLI（net48）
│   ├── BicoherenceViewer/          # 批量分析 GUI（WinForms，net48）
│   ├── WebView2Plots/              # WebView2 + ECharts 3D 可视化类库
│   ├── epoch_spectral_scroll.py    # 双相干趋势图生成（读 expert_epochs_metrics.csv）
│   ├── _batch_stats.py             # 组水平统计（配对 t 检验）
│   └── _download_ds003969.py       # 公开数据集 ds003969 下载脚本
├── results/
│   ├── final_report_figures/       # 最终分析报告（Markdown）+ 精选图表
│   ├── bicoherence_research_report/# 研究报告文本
│   └── expert_epochs/
│       └── expert_epochs_metrics.csv   # epoch 级双相干指标（核心数据）
├── Changes_in_Electroencephalographic_Bicoh.pdf   # 参考论文
├── md2pdf.config.json              # Markdown → PDF 样式配置
└── 导出PDF.bat                      # 一键导出仓库内所有 .md 为 PDF
```

## 构建与运行

### C# 管线（Windows + .NET Framework 4.8）

```powershell
# 核心计算 + CLI
dotnet build code\BicoherenceAnalyzer\BicoherenceAnalyzer.csproj -c Release

# 批量分析 GUI（连带构建 WebView2Plots）
dotnet build code\BicoherenceViewer\BicoherenceViewer.csproj -c Release
```

运行：

```powershell
# CLI 示例（参数：--subject --session --datadir --mode --surrogate
#          --all-expert --external-ds --all-external --bdf --all --help）
code\BicoherenceAnalyzer\bin\Release\net48\BicoherenceAnalyzer.exe --datadir <数据根目录> --subject sub-001 --session ses-01 --mode stimulus

# 批量分析 GUI
code\BicoherenceViewer\bin\Release\net48\BicoherenceViewer.exe
```

依赖说明：

- NuGet 自动恢复：MathNet.Numerics、ScottPlot、ScottPlot.WinForms、Microsoft.Web.WebView2
- 本地依赖：SeeSharpTools 系列 DLL 已随仓库包含在 `code\*\lib\` 目录，无需额外安装

### Python 工具

```powershell
pip install pandas numpy matplotlib scipy

# 生成各被试各通道的双相干趋势图（输出至 results/spectral_scrolls/）
python code\epoch_spectral_scroll.py --all-expert
```

## 数据说明

受体积与被试隐私限制，原始 EEG 数据（BDF）未包含在本仓库中：

| 数据 | 说明 | 获取方式 |
|---|---|---|
| ds001787 | 冥想探针范式（本项目分析使用 12 名被试） | [OpenNeuro ds001787](https://openneuro.org/datasets/ds001787) |
| ds003969 | Meditation vs Thinking Block 设计（本项目分析使用前 10 名被试） | `python code\_download_ds003969.py`（OpenNeuro S3 直连，无需认证） |
| 本地采集数据 | 24 名被试的冥想 EEG 记录（未公开） | — |

仓库内保留的分析产物：

- `results/final_report_figures/` — 分析报告与精选图表
- `results/expert_epochs/expert_epochs_metrics.csv` — epoch 级双相干指标数据
- `results/bicoherence_research_report/` — 研究报告文本

## 报告导出

`导出PDF.bat` 可将仓库内所有 Markdown（含分析报告）导出为 PDF，
依赖 Node.js 的 [md-to-pdf](https://www.npmjs.com/package/md-to-pdf)：

```powershell
npm install -g md-to-pdf
.\导出PDF.bat
```

## 参考

- 参考论文：*Changes in Electroencephalographic Bicoherence During Sevoflurane Anesthesia*
  （`Changes_in_Electroencephalographic_Bicoh.pdf`）
- 双谱估计与归一化方法：Welch 分段平均 + N³·σ³ 归一化（Haubrich, 1965）
