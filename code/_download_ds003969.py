"""
下载 ds003969 (Meditation vs thinking task) 数据集
从 OpenNeuro S3 镜像 (无需认证) 下载到 external_data/ds003969/
"""
import os, sys, time
from pathlib import Path
from urllib.request import urlopen, Request
from urllib.error import HTTPError
from concurrent.futures import ThreadPoolExecutor, as_completed

S3_BASE = "https://s3.amazonaws.com/openneuro.org/ds003969"
DEST = Path(__file__).resolve().parent.parent / "external_data" / "ds003969"

ROOT_FILES = [
    "dataset_description.json", "participants.json", "participants.tsv",
    "README", "CHANGES",
    "task-med1breath_events.json", "task-med2_events.json",
    "task-think1_events.json", "task-think2_events.json",
]


def build_file_list(max_subj=98):
    """构建全部文件列表"""
    files = list(ROOT_FILES)

    for subj in range(1, max_subj + 1):
        sid = f"sub-{subj:03d}"
        for task in ["med1breath", "med2", "think1", "think2"]:
            base = f"{sid}_task-{task}"
            files.append(f"{sid}/eeg/{base}_eeg.bdf")
            files.append(f"{sid}/eeg/{base}_eeg.json")
            files.append(f"{sid}/eeg/{base}_channels.tsv")
    return files


def download_file(rel_path, dest_base, retries=2):
    url = f"{S3_BASE}/{rel_path}"
    dest = dest_base / rel_path
    dest.parent.mkdir(parents=True, exist_ok=True)

    if dest.exists():
        return (rel_path, True, 0)

    for attempt in range(retries):
        try:
            req = Request(url, headers={"User-Agent": "Python-downloader"})
            with urlopen(req, timeout=60) as resp:
                data = resp.read()
            with open(dest, "wb") as f:
                f.write(data)
            size = len(data)
            return (rel_path, True, size)
        except HTTPError as e:
            if e.code == 404:
                return (rel_path, False, 0)
            if attempt < retries - 1:
                time.sleep(2)
        except Exception:
            if attempt < retries - 1:
                time.sleep(2)
    return (rel_path, False, 0)


def main():
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument("--max-subj", type=int, default=98, help="max subject number (1-98)")
    p.add_argument("--test", type=int, default=0, help="test N subjects only")
    p.add_argument("--skip-bdf", action="store_true")
    args = p.parse_args()

    max_s = args.test if args.test > 0 else args.max_subj

    print(f"Building file list for {max_s} subjects...")
    all_files = build_file_list(max_s)
    if args.skip_bdf:
        all_files = [f for f in all_files if not f.endswith(".bdf")]

    print(f"  {len(all_files)} files")

    # 先测试前 3 个文件是否存在
    print("Testing first 3 files...")
    for f in all_files[:3]:
        url = f"{S3_BASE}/{f}"
        try:
            req = Request(url, headers={"User-Agent": "Mozilla/5.0"}, method="HEAD")
            with urlopen(req, timeout=10) as resp:
                print(f"  [OK] {f}  ({resp.status})")
        except HTTPError as e:
            print(f"  [{e.code}] {f}")

    print(f"\nDownloading (8 threads)...")
    total_bytes, ok, fail, skipped = 0, 0, 0, 0
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {pool.submit(download_file, f, DEST): f for f in all_files}
        for i, fut in enumerate(as_completed(futures)):
            rel, success, size = fut.result()
            if success and size > 0:
                ok += 1
                total_bytes += size
            elif success:
                skipped += 1
            else:
                fail += 1
            if (i + 1) % 50 == 0 or i == len(all_files) - 1:
                mb = total_bytes / (1024 * 1024)
                print(f"  [{i+1}/{len(all_files)}] ok={ok} skip={skipped} miss={fail}  {mb:.1f} MB")

    print(f"\nDone. ok={ok} skipped(exist)={skipped} not-found={fail}  total={total_bytes/(1024*1024):.1f} MB")
    print(f"Output: {DEST}")


if __name__ == "__main__":
    main()
