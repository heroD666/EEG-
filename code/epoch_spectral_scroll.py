"""
epoch_spectral_scroll.py
========================
绘制每个 stimulus epoch 的双相干（bicoherence）指标趋势图。

数据来源：C# 管线生成的 expert_epochs_metrics.csv（无需重新读 BDF）

输出一张图，包含两栏：
  上栏 — 各频段双相干对角均值 (mean_diag_*) 随 epoch 的变化趋势
  下栏 — Q1/Q2 评分柱状图

双相干值单位：%（0-100），数值越大 = 该频段的非线性相位耦合越强。

用法：
  python epoch_spectral_scroll.py                          # sub-001/ses-01/Cz
  python epoch_spectral_scroll.py -s sub-002 -c C3
  python epoch_spectral_scroll.py -s sub-001 --all-channels
"""

import argparse
import numpy as np
import pandas as pd
from pathlib import Path

# ---- matplotlib + 中文字体 ----
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches

_CHINESE_FONT = None
for _font in ["Microsoft YaHei", "SimHei", "WenQuanYi Micro Hei", "Noto Sans CJK SC"]:
    try:
        matplotlib.font_manager.findfont(_font, fallback_to_default=False)
        _CHINESE_FONT = _font
        break
    except Exception:
        continue
if _CHINESE_FONT:
    plt.rcParams["font.family"] = _CHINESE_FONT
plt.rcParams["axes.unicode_minus"] = False

# =====================================================================
#  配置
# =====================================================================
_PROJECT_ROOT = Path(__file__).resolve().parent.parent
CSV_PATH = _PROJECT_ROOT / "results" / "expert_epochs" / "expert_epochs_metrics.csv"
OUT_DIR = _PROJECT_ROOT / "results" / "spectral_scrolls"
FIG_DPI = 150

# 所有指标（频段内对角双相干 + 跨频段耦合），统一结构:
#   (CSV列名, 显示标签, 颜色, 线型, 标记)
ALL_METRICS = [
    # 频段内对角双相干（实线 + 圆点标记）
    ("mean_diag_delta", "δ (diag)",  "#9C27B0", "-",  "o"),   # 紫
    ("mean_diag_theta", "θ (diag)",  "#2196F3", "-",  "o"),   # 蓝
    ("mean_diag_alpha", "α (diag)",  "#4CAF50", "-",  "o"),   # 绿
    ("mean_diag_beta",  "β (diag)",  "#FF9800", "-",  "o"),   # 橙
    ("mean_diag_gamma", "γ (diag)",  "#F44336", "-",  "o"),   # 红
    # 跨频段相位耦合（虚线 + 方形/菱形标记，与对角区分）
    ("theta_alpha_coupling", "θ-α (cross)", "#00BCD4", "--", "s"),   # 青
    ("alpha_beta_coupling",  "α-β (cross)", "#E91E63", "--", "D"),   # 粉
]

TARGET_CHANNELS = ["Fp1","Fp2","C3","C4","CP3","CP4","FCz","Fz","Cz","Pz","O1","O2"]
Q1_COLORS = {2: "#4CAF50", 4: "#FF9800", 8: "#F44336", 0: "#BDBDBD"}


# =====================================================================
#  核心：从 CSV 提取趋势数据
# =====================================================================
def extract_subject_data(df, subject, session, channel):
    """从 DataFrame 中筛选指定被试/会话/通道，按 epoch_id 排序返回"""
    sub = df[
        (df["subject"] == subject) &
        (df["session"] == session) &
        (df["channel"] == channel)
    ].sort_values("epoch_id")
    return sub


# =====================================================================
#  画图
# =====================================================================
def plot_bicoherence_trends(sub_data, channel_name, subject, session, out_path):
    """
    趋势图（两张子图）：
      上栏 — 频段内对角双相干 (实线) + 跨频段耦合 (虚线)，全部 0-100%
      下栏 — Q1/Q2 评分柱状图
    """
    n = len(sub_data)
    if n == 0:
        return

    x = np.arange(n)
    q1_vals = sub_data["Q1"].values
    q2_vals = sub_data["Q2"].values

    # 全部 7 条指标线（频段对角 + 跨频段耦合）
    metrics = ALL_METRICS

    # ---- 创建图形 ----
    fig_w = max(14, n * 0.45)
    fig, (ax_bicoh, ax_score) = plt.subplots(
        2, 1, figsize=(fig_w, 8.5),
        gridspec_kw={"height_ratios": [3, 1.2], "hspace": 0.06})

    # ==== 上栏：双相干趋势（实线=频段内对角，虚线=跨频段耦合）====
    for col, name, color, ls, mk in metrics:
        ys = sub_data[col].values
        valid = ~np.isnan(ys)
        if valid.sum() >= 2:
            ax_bicoh.plot(x[valid], ys[valid], color=color, linewidth=1.8,
                          linestyle=ls, marker=mk, markersize=5,
                          markerfacecolor="white",
                          markeredgecolor=color, markeredgewidth=1.2,
                          label=name, zorder=3)

    # Q1 背景色带
    for i in range(n):
        bg = Q1_COLORS.get(int(q1_vals[i]), "#EEE")
        ax_bicoh.axvspan(i - 0.45, i + 0.45, facecolor=bg, alpha=0.12, zorder=0)

    ax_bicoh.set_ylabel("Bicoherence (%)", fontsize=11)
    ax_bicoh.set_ylim(0, 100)
    ax_bicoh.set_xlim(-0.6, n - 0.4)
    ax_bicoh.grid(True, alpha=0.25)
    ax_bicoh.legend(fontsize=8.5, ncol=len(metrics), loc="upper right", framealpha=0.8)

    title = f"{subject} / {session}    {channel_name}    {n} epochs"
    ax_bicoh.set_title(title, fontsize=12, fontweight="bold")
    ax_bicoh.tick_params(labelbottom=False)

    # ==== 下栏：Q1 / Q2 ====
    bar_w = 0.38
    colors_q1 = [Q1_COLORS.get(int(v), "#999") for v in q1_vals]
    ax_score.bar(x - bar_w/2, q1_vals, bar_w, color=colors_q1,
                 edgecolor="white", linewidth=0.5, label="Q1 (meditation depth)")
    ax_score.bar(x + bar_w/2, q2_vals, bar_w, color="#90CAF9",
                 edgecolor="white", linewidth=0.5, alpha=0.85, label="Q2 (mind wandering)")

    ax_score.set_xlabel("Epoch #", fontsize=10)
    ax_score.set_ylabel("Score", fontsize=10)
    ax_score.set_xlim(-0.6, n - 0.4)
    ax_score.set_ylim(0, 9)
    ax_score.set_yticks([2, 4, 8])
    ax_score.legend(fontsize=8, ncol=2, loc="upper right", framealpha=0.8)
    ax_score.grid(True, alpha=0.2, axis="y")
    ax_score.text(0.01, 0.95, "Q1: 2=shallow  4=medium  8=deep",
                  transform=ax_score.transAxes, fontsize=7, va="top", color="#666")

    # Y 轴范围说明 + 线型图例
    ax_bicoh.text(0.99, 0.02,
                  "solid = intra-band diagonal  |  dashed = cross-band coupling",
                  transform=ax_bicoh.transAxes, fontsize=7, va="bottom", ha="right",
                  color="#999", style="italic")

    out_path.parent.mkdir(parents=True, exist_ok=True)
    plt.savefig(out_path, dpi=FIG_DPI, bbox_inches="tight", facecolor="white")
    plt.close()
    print(f"  [OK] {out_path.name}")


# =====================================================================
#  主流程
# =====================================================================
def main():
    p = argparse.ArgumentParser(description="Bicoherence Trend Chart")
    p.add_argument("--subject", "-s", default="sub-001")
    p.add_argument("--session", "-ses", default="ses-01")
    p.add_argument("--channel", "-c", default="Cz")
    p.add_argument("--all-channels", action="store_true")
    p.add_argument("--all-sessions", action="store_true")
    p.add_argument("--all-expert", action="store_true",
                   help="process all expert subjects in CSV (overrides --subject)")
    args = p.parse_args()

    print("Loading CSV...")
    df = pd.read_csv(CSV_PATH)
    print(f"  {len(df)} rows, subjects: {df['subject'].nunique()}, "
          f"channels: {sorted(df['channel'].dropna().unique())}")

    if args.all_expert:
        subjects = sorted(df["subject"].unique())
        channels = TARGET_CHANNELS
        sessions_mode = "all"  # 自动处理所有 session
    else:
        subjects = [args.subject]
        channels = TARGET_CHANNELS if args.all_channels else [args.channel]
        sessions_mode = "all" if args.all_sessions else "single"

    for subj in subjects:
        if subj not in df["subject"].values:
            print(f"  [SKIP] {subj} not in CSV")
            continue

        if sessions_mode == "all":
            sessions = sorted(df[df["subject"] == subj]["session"].unique())
        else:
            sessions = [args.session]

        for ses in sessions:
            for ch in channels:
                sub_data = extract_subject_data(df, subj, ses, ch)
                n_eps = len(sub_data)
                if n_eps == 0:
                    print(f"  [SKIP] {subj}/{ses}/{ch}: no data")
                    continue
                print(f"  {subj}/{ses}/{ch}: {n_eps} epochs")

                out_dir = OUT_DIR / f"{subj}_{ses}"
                fname = f"bicoh_trend_{subj}_{ses}_{ch}.png"
                plot_bicoherence_trends(sub_data, ch, subj, ses, out_dir / fname)

    print(f"\nDone. Output: {OUT_DIR}")


if __name__ == "__main__":
    main()
