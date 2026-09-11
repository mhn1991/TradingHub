#!/usr/bin/env python3
"""Measure the drop/rally base deviation described in PROJECT_STATE.md 3.53.

Module 2 allows a valley's basing structure to be "made of only a bearish ERC and a bullish ERC
(drop/rally)" - two candles. Module 4 then requires the distal to "always include the lowest low in
the basing structure". ImbalanceDetector.TryFindBase takes this path with baseStart == baseEnd, so
Lines() spans the turning candle alone and the opposing ERC's extreme is left outside the zone.

This script replicates TryFindBase's fallback condition exactly against exported H4 bars and reports
how often the two readings disagree, and by how much relative to the zone width.

Usage:  python3 tools/alfonso_droprally_distal.py <dir-of-*-h4.csv>
CSVs are those produced by tools/alfonso_export_bars.py: openTime,open,high,low,close
"""
import csv
import glob
import os
import statistics
import sys

ERC = 0.80  # AlfonsoBar.ExtendedRangeBodyRatio


def load(path):
    with open(path) as handle:
        return [(float(r['open']), float(r['high']), float(r['low']), float(r['close']))
                for r in csv.DictReader(handle)]


def body_ratio(bar):
    o, h, l, c = bar
    span = h - l
    return 1.0 if span <= 0 else abs(c - o) / span   # AlfonsoBar.BodyRatio


def is_bullish(bar):
    return bar[3] > bar[0]


def body_top(bar):
    return max(bar[0], bar[3])


def body_bottom(bar):
    return min(bar[0], bar[3])


def main(directory):
    total = differ = 0
    fractions = []

    for path in sorted(glob.glob(os.path.join(directory, '*-h4.csv'))):
        bars = load(path)
        found = disagreed = 0

        for i in range(1, len(bars) - 1):
            prior, turn = bars[i - 1], bars[i]

            # ImbalanceDetector.TryFindBase, drop-rally / rally-drop branch.
            if not (body_ratio(turn) >= ERC and body_ratio(prior) >= ERC
                    and is_bullish(turn) != is_bullish(prior)):
                continue

            # ImbalanceDetector.TryBuild decides the kind from the first leg-out candle.
            nxt = bars[i + 1]
            demand = nxt[3] > body_top(turn) or is_bullish(nxt)

            if demand:
                code_distal, book_distal = turn[2], min(prior[2], turn[2])
                proximal = body_top(turn)
                omitted = code_distal - book_distal
            else:
                code_distal, book_distal = turn[1], max(prior[1], turn[1])
                proximal = body_bottom(turn)
                omitted = book_distal - code_distal

            width = abs(proximal - code_distal)
            found += 1
            if omitted > 0:
                disagreed += 1
                if width > 0:
                    fractions.append(omitted / width)

        total += found
        differ += disagreed
        if found:
            print(f"{os.path.basename(path):28s} bars={len(bars):5d}  "
                  f"drop/rally bases={found:4d}  distal differs={disagreed:4d} "
                  f"({disagreed / found:.0%})")

    if not total:
        print("no drop/rally bases found")
        return

    print(f"\nTOTAL drop/rally bases={total}  distal differs={differ} ({differ / total:.0%})")
    if fractions:
        fractions.sort()
        print("omitted extreme beyond the code's distal, as a fraction of zone width:")
        print(f"  median {statistics.median(fractions):.2f}  mean {statistics.mean(fractions):.2f}  "
              f"p90 {fractions[int(.9 * len(fractions))]:.2f}  max {max(fractions):.2f}")
        print(f"  share above 0.25 zone widths: "
              f"{sum(1 for x in fractions if x > 0.25) / len(fractions):.0%}")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '.')
