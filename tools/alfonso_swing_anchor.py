#!/usr/bin/env python3
"""Measure the swing-anchor mismatch described in PROJECT_STATE.md 3.54.

SwingPoint documents Index as "Bar index of the swing extreme, used for slope arithmetic", but
AlfonsoTrendDetector.RecordSwings sets Index from zone.BaseEnd while Price is zone.Distal - the
extreme over the WHOLE base. Whenever a base spans more than one candle and its extreme is not on
the last one, the two fields describe different bars, and TrendlineBuilder.Fit does slope arithmetic
on the pair.

This replicates ImbalanceDetector.TryFindBase's ordinary basing branch against exported H4 bars and
reports how often that happens. It measures base geometry only - it does not run the confirmation,
width or impulse rules, so it counts candidate bases rather than confirmed zones.

Usage:  python3 tools/alfonso_swing_anchor.py <dir-of-*-h4.csv>
CSVs are those produced by tools/alfonso_export_bars.py: openTime,open,high,low,close
"""
import csv
import glob
import os
import statistics
import sys

BASING = 0.50   # ImbalanceOptions.MaximumBasingBodyRatio
MAX_BASE = 6    # ImbalanceOptions.MaximumBaseCandles


def load(path):
    with open(path) as handle:
        return [(float(r['open']), float(r['high']), float(r['low']), float(r['close']))
                for r in csv.DictReader(handle)]


def body_ratio(bar):
    o, h, l, c = bar
    span = h - l
    return 1.0 if span <= 0 else abs(c - o) / span


def main(directory):
    multi = misplaced = 0
    offsets, drops, lengths = [], [], {}

    for path in sorted(glob.glob(os.path.join(directory, '*-h4.csv'))):
        bars = load(path)
        count = len(bars)

        for base_end in range(count - 1):
            if body_ratio(bars[base_end]) > BASING:
                continue
            # "The base has to end where the pause ends."
            if base_end + 1 < count and body_ratio(bars[base_end + 1]) <= BASING:
                continue

            base_start = base_end
            while (base_start - 1 >= 0
                   and base_end - (base_start - 1) + 1 <= MAX_BASE
                   and body_ratio(bars[base_start - 1]) <= BASING):
                base_start -= 1

            length = base_end - base_start + 1
            lengths[length] = lengths.get(length, 0) + 1
            if length < 2:
                continue

            multi += 1
            lows = [bars[i][2] for i in range(base_start, base_end + 1)]
            extreme_at = base_start + lows.index(min(lows))
            if extreme_at == base_end:
                continue

            misplaced += 1
            offsets.append(base_end - extreme_at)
            height = max(bars[i][1] for i in range(base_start, base_end + 1)) - min(lows)
            if height > 0:
                drops.append((bars[base_end][2] - min(lows)) / height)

    print("base length histogram:", dict(sorted(lengths.items())))
    if not multi:
        print("no multi-candle bases found")
        return

    print(f"\nmulti-candle bases: {multi}")
    print(f"  extreme NOT on the base-end bar: {misplaced} ({misplaced / multi:.0%})")
    if offsets:
        print(f"  anchor index wrong by (bars): median {statistics.median(offsets):.0f}  "
              f"mean {statistics.mean(offsets):.2f}  max {max(offsets)}")
    if drops:
        drops.sort()
        print("  anchor price below the base-end bar's own low, as a fraction of base height:")
        print(f"    median {statistics.median(drops):.2f}  mean {statistics.mean(drops):.2f}  "
              f"p90 {drops[int(.9 * len(drops))]:.2f}")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '.')
