#!/usr/bin/env python3
"""
Export the aggregated m15/h1/h4 bar CSVs that the Alfonso research harnesses read.

`Simulator.Tests/ZZAlfonsoGradeStudy.cs`, `ZZAlfonsoAttributeStudy.cs` and
`ZZAlfonsoTrendLayerDiagnostic.cs` are [Explicit] studies that read CSVs from the directory named by
$ALFONSO_DATA rather than going through the simulator. This regenerates those CSVs from the
1-minute OANDA series already in the historical cache, so PROJECT_STATE 3.30, 3.51 and 3.52 can be
reproduced from a clone plus a warm cache.

  ./tools/alfonso_export_bars.py --dataset study     # the 3.51 sets: six instruments + full-xauusd
  ./tools/alfonso_export_bars.py --dataset holdout   # the 3.52 held-out sets: hold5-* and newfx-*
  ./tools/alfonso_export_bars.py --dataset all
  ./tools/alfonso_export_bars.py --list              # show what each dataset resolves to

Paths:
  $ALFONSO_CACHE  cache directory   (default: <repo>/.cache/historical)
  $ALFONSO_DATA   output directory  (default: /mnt/storage/scratch/alfonso)

KNOWN DEFECT, DELIBERATELY PRESERVED
------------------------------------
Bucketing is `t - (t % period)` with NO completeness rule, so a bucket missing 1-minute members is
written as though it were whole. This is recorded in PROJECT_STATE 3.30 and re-confirmed in 3.48,
and it is the reason this harness does not exactly reproduce the agent running inside the simulator.

It is preserved rather than fixed because every published figure that depends on these CSVs was
measured under it. Fixing it silently would leave the numbers in 3.30/3.51/3.52 unreproducible while
appearing to reproduce them. If you want completeness-aware buckets, add them as a NEW option, keep
this path as the default, and re-measure anything you intend to compare against.

The defect understates bar quality uniformly, so it cannot manufacture a difference between two
buckets of the same run - which is why 3.51/3.52's relative comparisons survive it.
"""

import argparse
import glob
import gzip
import json
import os
import sys
from datetime import datetime, timezone

PERIODS = {"m15": 900, "h1": 3600, "h4": 14400}

# Each entry: tag -> (cache file stem, inclusive-from date or None, exclusive-to date or None).
# The stems carry the window the FETCH used, which is not the window the backtest asked for: the
# runner extends `from` backwards by 21 days for warmup, so a run started 2023-01-01 is cached as
# 20221211. See PROJECT_STATE 3.52.
STUDY = {
    "gold":        ("METAL_XAU_USD_1m_20251103_20260723", None, None),
    "silver":      ("METAL_XAG_USD_1m_20251124_20260723", None, None),
    "nas100":      ("CFD_NAS100_USD_1m_20251124_20260723", None, None),
    "us30":        ("CFD_US30_USD_1m_20251124_20260723", None, None),
    "eurusd":      ("FX_EUR_USD_1m_20251124_20260723", None, None),
    "gbpjpy":      ("FX_GBP_JPY_1m_20251124_20260723", None, None),
    # 3.5 years of gold, the second in-sample window in 3.51. Note this is a DIFFERENT XAU cache
    # entry from the one `gold` uses, and it carries a from-filter: the published CSVs start
    # 2023-01-02 (the first session of 2023; 2023-01-01 was a Sunday holiday) and run to 2026-07-23,
    # which only this entry covers. The 20221211 entry stops a day earlier and would silently
    # produce a different, shorter series.
    "full-xauusd": ("METAL_XAU_USD_1m_20221212_20260724", "2023-01-01", None),
}

# 3.52 axis A: the same five non-XAU instruments over the period BEFORE 3.51's window.
# 3.52 axis B: six FX pairs that contributed nothing to 3.51.
HOLDOUT = {
    "hold5-silver": ("METAL_XAG_USD_1m_20221211_20260723", None, "2025-11-24"),
    "hold5-eurusd": ("FX_EUR_USD_1m_20221211_20260723", None, "2025-11-24"),
    "hold5-gbpjpy": ("FX_GBP_JPY_1m_20221211_20260723", None, "2025-11-24"),
    "hold5-nas100": ("CFD_NAS100_USD_1m_20221211_20260723", None, "2025-11-24"),
    "hold5-us30":   ("CFD_US30_USD_1m_20221211_20260723", None, "2025-11-24"),
    "newfx-audusd": ("FX_AUD_USD_1m_20251124_20260723", None, None),
    "newfx-gbpusd": ("FX_GBP_USD_1m_20251124_20260723", None, None),
    "newfx-nzdusd": ("FX_NZD_USD_1m_20251124_20260723", None, None),
    "newfx-usdcad": ("FX_USD_CAD_1m_20251124_20260723", None, None),
    "newfx-usdchf": ("FX_USD_CHF_1m_20251124_20260723", None, None),
    "newfx-usdjpy": ("FX_USD_JPY_1m_20251124_20260723", None, None),
}

DATASETS = {"study": STUDY, "holdout": HOLDOUT, "all": {**STUDY, **HOLDOUT}}


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def cache_dir():
    return os.environ.get("ALFONSO_CACHE") or os.path.join(repo_root(), ".cache", "historical")


def out_dir():
    return os.environ.get("ALFONSO_DATA") or "/mnt/storage/scratch/alfonso"


def resolve(stem):
    """The one cache file for a stem. Ambiguity is an error, not a coin toss."""
    hits = sorted(glob.glob(os.path.join(cache_dir(), stem + "_*.jsonl.gz")))
    if not hits:
        return None, f"no cache file matching {stem}_*.jsonl.gz"
    if len(hits) > 1:
        names = ", ".join(os.path.basename(h) for h in hits)
        return None, f"{len(hits)} cache files match {stem}: {names}"
    return hits[0], None


def stamp(day):
    return None if day is None else datetime.fromisoformat(day + "T00:00:00+00:00").timestamp()


def export(tag, stem, lo, hi, destination):
    path, problem = resolve(stem)
    if path is None:
        print(f"{tag:16s} SKIPPED - {problem}")
        return False

    lo_t, hi_t = stamp(lo), stamp(hi)
    buckets = {k: {} for k in PERIODS}
    minutes = 0

    with gzip.open(path, "rt") as handle:
        for line in handle:
            line = line.lstrip("﻿").strip()
            if not line:
                continue
            row = json.loads(line)
            if "openTime" not in row:          # header record
                continue
            t = int(datetime.fromisoformat(row["openTime"]).timestamp())
            if lo_t is not None and t < lo_t:
                continue
            if hi_t is not None and t >= hi_t:
                continue
            o, h, l, c = row["open"], row["high"], row["low"], row["close"]
            minutes += 1
            for name, period in PERIODS.items():
                b = t - (t % period)           # see KNOWN DEFECT in the module docstring
                held = buckets[name]
                if b in held:
                    e = held[b]
                    e[1] = max(e[1], h)
                    e[2] = min(e[2], l)
                    e[3] = c
                else:
                    held[b] = [o, h, l, c]

    counts = []
    for name in PERIODS:
        held = buckets[name]
        with open(os.path.join(destination, f"{tag}-{name}.csv"), "w") as writer:
            writer.write("openTime,open,high,low,close\n")
            for b in sorted(held):
                o, h, l, c = held[b]
                ts = datetime.fromtimestamp(b, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S+00:00")
                writer.write(f"{ts},{o},{h},{l},{c}\n")
        counts.append(f"{name}={len(held)}")

    span = ""
    if buckets["m15"]:
        keys = sorted(buckets["m15"])
        span = (f"  {datetime.fromtimestamp(keys[0], timezone.utc):%Y-%m-%d} -> "
                f"{datetime.fromtimestamp(keys[-1], timezone.utc):%Y-%m-%d}")
    print(f"{tag:16s} 1m={minutes:>9,}  " + "  ".join(counts) + span)
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dataset", choices=sorted(DATASETS), default="all")
    parser.add_argument("--out", help="output directory (default: $ALFONSO_DATA)")
    parser.add_argument("--list", action="store_true",
                        help="show what the dataset resolves to and exit")
    args = parser.parse_args()

    selected = DATASETS[args.dataset]

    if args.list:
        print(f"cache: {cache_dir()}")
        for tag, (stem, lo, hi) in selected.items():
            path, problem = resolve(stem)
            where = os.path.basename(path) if path else f"MISSING ({problem})"
            window = f"{lo or 'start'} -> {hi or 'end'}"
            print(f"  {tag:16s} {window:26s} {where}")
        return 0

    destination = args.out or out_dir()
    os.makedirs(destination, exist_ok=True)
    print(f"cache:  {cache_dir()}\noutput: {destination}\n")

    exported = sum(export(tag, *spec, destination) for tag, spec in selected.items())
    print(f"\n{exported} of {len(selected)} datasets exported")
    return 0 if exported == len(selected) else 1


if __name__ == "__main__":
    sys.exit(main())
