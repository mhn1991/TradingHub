#!/usr/bin/env python3
"""
Zone-level analysis of the Alfonso grade study - the tables in PROJECT_STATE 3.51 and 3.52.

Reads the gradestudy-{prefix}.csv files written by Simulator.Tests/ZZAlfonsoGradeStudy.cs and asks
whether module 7's qualifiers order outcomes across the FULL zone population, including the zones
the hard gates reject.

  ./tools/alfonso_grade_report.py --sample insample-five
  ./tools/alfonso_grade_report.py --sample holdout-period --controls
  ./tools/alfonso_grade_report.py --compare            # every sample, ratio effect side by side
  ./tools/alfonso_grade_report.py --prefixes gold,silver

READ THE GROSS COLUMN, NOT THE NET ONE
--------------------------------------
Cost is charged as a flat 2.4 basis points of price, so cost-per-R scales as 1/risk - and risk
correlates with zone width, which correlates with most of the attributes under test. Net R therefore
manufactures orderings that are pure cost. 3.52 found two of them and threw them away: base candle
count and entry timeframe both look cleanly monotone on net R and are flat on gross.

So the ordering statistic here is `grossR` = 4*(win rate) - 1, which is cost-free, and net is printed
beside it only to show the cost mechanism. The one place net matters is the conclusion that a high
impulse ratio is a narrow zone, so the best gross bucket is the worst net bucket.

Data: $ALFONSO_DATA (default /mnt/storage/scratch/alfonso).
"""

import argparse
import collections
import csv
import math
import os
import statistics
import sys

SAMPLES = {
    # 3.51: the two in-sample sets that produced the 5:1 finding.
    "insample-five": ["silver", "eurusd", "gbpjpy", "nas100", "us30"],
    "insample-xau": ["full-xauusd"],
    "insample-six": ["gold", "silver", "eurusd", "gbpjpy", "nas100", "us30"],
    # 3.52: the two held-out axes. Neither contributed to the finding under test.
    "holdout-period": ["hold5-silver", "hold5-eurusd", "hold5-gbpjpy", "hold5-nas100", "hold5-us30"],
    "holdout-instruments": ["newfx-audusd", "newfx-gbpusd", "newfx-nzdusd",
                            "newfx-usdcad", "newfx-usdchf", "newfx-usdjpy"],
}


def data_dir():
    return os.environ.get("ALFONSO_DATA") or "/mnt/storage/scratch/alfonso"


def load(prefix):
    path = os.path.join(data_dir(), f"gradestudy-{prefix}.csv")
    if not os.path.exists(path):
        print(f"  missing {path}", file=sys.stderr)
        return []
    rows = []
    for r in csv.DictReader(open(path)):
        if r["resolved"] != "True":        # never hit stop or target before the data ended
            continue
        r["won"] = r["won"] == "True"
        r["net"] = float(r["r"])
        r["ratio"] = float(r["impulseToBaseRatio"])
        r["risk"] = float(r["risk"])
        r["score"] = int(r["scoreTotal"])
        # the study writes net R; recover what the flat cost model charged
        r["costR"] = (3.0 - r["net"]) if r["won"] else (-r["net"] - 1.0)
        r["prefix"] = prefix
        rows.append(r)
    return rows


def gross(rows):
    """(n, gross R, standard error). Gross R = 4*winrate - 1, so its SE is 4x the binomial SE."""
    n = len(rows)
    if n == 0:
        return 0, 0.0, 0.0
    w = sum(1 for r in rows if r["won"]) / n
    return n, 4 * w - 1, 4 * math.sqrt(w * (1 - w) / n)


RATIO_LADDER = [("a <2 REJECTED by the gate", lambda x: x < 2),
                ("b 2-3", lambda x: 2 <= x < 3),
                ("c 3-5", lambda x: 3 <= x < 5),
                ("d >=5", lambda x: x >= 5)]


def ratio_bucket(r):
    for name, test in RATIO_LADDER:
        if test(r["ratio"]):
            return name
    return "?"


def bucket(title, rows, key, order=None):
    print(f"\n{title}")
    print(f'  {"":26}{"n":>7}{"win%":>8}{"grossR":>10}{"95% CI (gross)":>22}{"costR":>9}{"netR":>10}')
    groups = collections.defaultdict(list)
    for r in rows:
        groups[key(r)].append(r)
    for k in (order or sorted(groups, key=str)):
        g = groups.get(k)
        if not g:
            continue
        n, gr, se = gross(g)
        w = sum(1 for r in g if r["won"]) / n
        cost = sum(r["costR"] for r in g) / n
        net = sum(r["net"] for r in g) / n
        print(f"  {str(k):26}{n:7}{w:8.1%}{gr:+10.4f}"
              f"   [{gr-1.96*se:+.4f}, {gr+1.96*se:+.4f}]{cost:9.3f}{net:+10.4f}")


def report(name, rows, controls):
    n, gr, se = gross(rows)
    if n == 0:
        print(f"\n{name}: no data")
        return
    cost = sum(r["costR"] for r in rows) / n
    net = sum(r["net"] for r in rows) / n
    print(f'\n{"="*96}\n{name}\n  {n} resolved zone touches   grossR {gr:+.4f}   '
          f'mean costR {cost:.3f}   netR {net:+.4f}\n{"="*96}')

    bucket("impulse:base ratio - the one attribute that replicates (3.51, 3.52)",
           rows, ratio_bucket, [k for k, _ in RATIO_LADDER])
    bucket("score total (module 7, 0-10) - does the composite order anything?",
           rows, lambda r: r["score"])
    bucket("grade", rows, lambda r: r["grade"], ["Weak", "Medium", "Strong"])
    bucket("the gates verdict", rows,
           lambda r: "PASSES" if r["meetsTradeability"] == "True" else "REJECTED",
           ["PASSES", "REJECTED"])
    bucket("departure (the gate refuses Weak)", rows, lambda r: r["strength"],
           ["Weak", "Strong", "Gap"])
    bucket("accomplishments (the gate refuses 0)", rows,
           lambda r: int(r["accomplishmentCount"]))
    bucket("base candles - flat on gross; the net ordering here is COST", rows,
           lambda r: int(r["baseCandles"]))
    bucket("timeframe - also flat on gross", rows, lambda r: r["role"],
           ["Top", "Middle", "Lower"])

    if not controls:
        return

    print("\ncontrols on the >=5 effect (is it just stop width in disguise?)")
    by_prefix = collections.defaultdict(list)
    for r in rows:
        by_prefix[r["prefix"]].append(r)
    for rs in by_prefix.values():
        med = statistics.median(x["risk"] for x in rs)
        for x in rs:
            x["nrisk"] = x["risk"] / med if med else 0.0

    ordered = sorted(rows, key=lambda r: r["nrisk"])
    q = len(ordered) // 4
    print(f'  {"":20}{"n <5":>8}{"gross":>9}{"n >=5":>8}{"gross":>9}{"diff":>9}')
    for i, label in enumerate(["Q1 tightest stops", "Q2", "Q3", "Q4 widest"]):
        part = ordered[i * q:(i + 1) * q] if i < 3 else ordered[3 * q:]
        lo = [r for r in part if r["ratio"] < 5]
        hi = [r for r in part if r["ratio"] >= 5]
        if not lo or not hi:
            continue
        nl, gl, _ = gross(lo)
        nh, gh, _ = gross(hi)
        print(f"  {label:20}{nl:8}{gl:+9.4f}{nh:8}{gh:+9.4f}{gh-gl:+9.4f}")

    for label, subset in ([(p, rs) for p, rs in sorted(by_prefix.items())] +
                          [("first half", sorted(rows, key=lambda r: r["at"])[:len(rows)//2]),
                           ("second half", sorted(rows, key=lambda r: r["at"])[len(rows)//2:])]):
        lo = [r for r in subset if r["ratio"] < 5]
        hi = [r for r in subset if r["ratio"] >= 5]
        if not lo or not hi:
            continue
        nl, gl, _ = gross(lo)
        nh, gh, _ = gross(hi)
        print(f"  {label:20}{nl:8}{gl:+9.4f}{nh:8}{gh:+9.4f}{gh-gl:+9.4f}")


def compare():
    """The 3.52 headline: the >=5 effect in each sample, in-sample and held-out side by side."""
    print(f'{"sample":24}{"n":>8}{"share>=5":>10}{"<5 gross":>10}{">=5 gross":>11}'
          f'{"diff":>9}{"95% CI of diff":>22}')
    for name in ["insample-five", "insample-xau", "holdout-period", "holdout-instruments"]:
        rows = [r for p in SAMPLES[name] for r in load(p)]
        if not rows:
            continue
        lo = [r for r in rows if r["ratio"] < 5]
        hi = [r for r in rows if r["ratio"] >= 5]
        nl, gl, sl = gross(lo)
        nh, gh, sh = gross(hi)
        d = gh - gl
        sd = math.sqrt(sl ** 2 + sh ** 2)
        print(f"  {name:22}{len(rows):8}{nh/len(rows):10.1%}{gl:+10.4f}{gh:+11.4f}"
              f"{d:+9.4f}   [{d-1.96*sd:+.4f}, {d+1.96*sd:+.4f}]")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sample", choices=sorted(SAMPLES))
    parser.add_argument("--prefixes", help="comma-separated gradestudy prefixes instead of --sample")
    parser.add_argument("--controls", action="store_true",
                        help="risk-quartile, per-instrument and chronological splits of the >=5 effect")
    parser.add_argument("--compare", action="store_true", help="every sample, ratio effect only")
    args = parser.parse_args()

    if args.compare or (not args.sample and not args.prefixes):
        compare()
        return 0

    prefixes = args.prefixes.split(",") if args.prefixes else SAMPLES[args.sample]
    rows = [r for p in prefixes for r in load(p)]
    report(args.sample or args.prefixes, rows, args.controls)
    return 0


if __name__ == "__main__":
    sys.exit(main())
