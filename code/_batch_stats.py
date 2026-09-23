"""ds003969 组水平统计: Meditation vs Thinking -- 被试内平均 + 配对 t 检验 + 可视化"""
import csv, os, glob
import numpy as np
from collections import defaultdict

base = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "results", "ds003969_block_analysis")
csvs = glob.glob(os.path.join(base, "*", "*_block_bicoherence.csv"))
print(f"CSV files: {len(csvs)}")

# ── 加载所有数据 ──
all_rows = []
for f in sorted(csvs):
    with open(f, encoding="utf-8-sig") as fh:
        all_rows.extend(list(csv.DictReader(fh)))

print(f"Total rows: {len(all_rows)}")

# ── 概览 ──
subjects = sorted(set(r["subject"] for r in all_rows))
conditions = sorted(set(r["condition"] for r in all_rows))
channels = sorted(set(r["channel"] for r in all_rows))
metrics = [k for k in all_rows[0].keys() if k.startswith("mean_") or k.endswith("_coupling")]

print(f"Subjects: {len(subjects)} ({', '.join(subjects[:5])}...)")
print(f"Conditions: {conditions}")
print(f"Channels: {len(channels)}")
print(f"Metrics: {metrics}")

# ── 每被试 med/think epoch 数 ──
print("\n--- Per-subject epoch counts ---")
for sub in subjects:
    sub_rows = [r for r in all_rows if r["subject"] == sub]
    med = len([r for r in sub_rows if r["condition"] == "meditation"])
    think = len([r for r in sub_rows if r["condition"] == "thinking"])
    print(f"  {sub}: meditation={med}, thinking={think}")

# ── Group means ──
print("\n--- Group-level Meditation vs Thinking ---")
print(f"{'Metric':<25} {'Channel':<6} {'Med mean':>10} {'Think mean':>10} {'Diff':>8} {'t-stat':>8}")
print("-" * 75)

from scipy import stats

results_table = []
for metric in metrics:
    for ch in channels:
        med_vals = []
        think_vals = []
        # aggregate per subject first, then across subjects
        for sub in subjects:
            sub_per_med = [float(r[metric]) for r in all_rows
                          if r["subject"] == sub and r["channel"] == ch and r["condition"] == "meditation"]
            sub_per_think = [float(r[metric]) for r in all_rows
                            if r["subject"] == sub and r["channel"] == ch and r["condition"] == "thinking"]
            if sub_per_med:
                med_vals.append(np.mean(sub_per_med))
            if sub_per_think:
                think_vals.append(np.mean(sub_per_think))
        
        if len(med_vals) >= 3 and len(think_vals) >= 3:
            t_stat, p_val = stats.ttest_rel(med_vals, think_vals)
            med_mean = np.mean(med_vals)
            think_mean = np.mean(think_vals)
            diff = med_mean - think_mean
            results_table.append((metric, ch, med_mean, think_mean, diff, t_stat, p_val, len(med_vals)))

# sort by p-value
results_table.sort(key=lambda x: x[6])

for metric, ch, med_mean, think_mean, diff, t_stat, p_val, n in results_table[:30]:
    sig = "*" if p_val < 0.05 else ("**" if p_val < 0.01 else "")
    print(f"{metric:<25} {ch:<6} {med_mean:9.3f}% {think_mean:9.3f}% {diff:+7.3f}% {t_stat:+7.3f}  p={p_val:.4f} {sig}")

# ── Overall summary ──
print(f"\n--- Significant (p<0.05, uncorrected) ---")
sig_results = [r for r in results_table if r[6] < 0.05]
if sig_results:
    for metric, ch, med_mean, think_mean, diff, t_stat, p_val, n in sig_results:
        direction = "Med > Think" if diff > 0 else "Think > Med"
        print(f"  {metric} @ {ch}: {direction}, t={t_stat:.2f}, p={p_val:.4f}, n={n}")
else:
    print("  None (all p > 0.05)")

# ── Top 10 by effect size ──
print(f"\n--- Top 10 by |effect size| ---")
results_by_effect = sorted(results_table, key=lambda x: -abs(x[4]))
for metric, ch, med_mean, think_mean, diff, t_stat, p_val, n in results_by_effect[:10]:
    print(f"  {metric} @ {ch}: diff={diff:+.3f}%, t={t_stat:.2f}, p={p_val:.4f}, n={n}")
