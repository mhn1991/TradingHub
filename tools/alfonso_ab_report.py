#!/usr/bin/env python3
"""
Strategy-level A/B for the Alfonso agent - the trade tables in PROJECT_STATE 3.50 and 3.52.

Reads simulation-result.json from pairs of BacktestRunner output directories and compares two arms
on true R, with an optional split by trade open date so one run can serve both an out-of-sample and
an in-sample period.

  ./tools/alfonso_ab_report.py --experiment holdout-period
  ./tools/alfonso_ab_report.py --experiment holdout-instruments
  ./tools/alfonso_ab_report.py --experiment host-rule
  ./tools/alfonso_ab_report.py --root DIR --arms P2,P5 --instruments gold,silver --split 2025-11-24

TRUE R, NOT rMultiple
---------------------
`rMultiple` in the result JSON divides by risk PLUS an assumed round-trip cost, so it is not
profit-per-risk and understates a tight-stopped winner badly (3.44). Everything here uses
netProfitLoss / (|entry - stop| * quantity).

WHAT THIS TEST CANNOT DO
------------------------
At the observed dispersion of true R (sd around 1.7), resolving a 0.08R effect at 95% needs roughly
3,500 trades per arm. Every strategy-level run in this repo has a few hundred. So a "CI contains
zero" verdict here is close to guaranteed for effects of the size the zone study measures, and it is
not evidence of absence. For zone or setup quality worth less than about 0.2R, use
alfonso_grade_report.py instead - it gets tens of thousands of observations from the same candles
because it is not throttled by the single resting-order slot.
"""

import argparse
import json
import math
import os
import sys

FIVE = ["gbpjpy", "nas100", "us30", "silver", "eurusd"]
SIXFX = ["audusd", "gbpusd", "nzdusd", "usdcad", "usdchf", "usdjpy"]
SIX = ["gbpjpy", "nas100", "us30", "gold", "silver", "eurusd"]

SCRATCH = os.environ.get("ALFONSO_DATA") or "/mnt/storage/scratch/alfonso"

EXPERIMENTS = {
    # 3.52 axis A: 2:1 vs 5:1 over the held-out period. The runs cover to 2026-07-23 and are split
    # by trade open date, which is equivalent to stopping them at the cutoff because no trade can
    # depend on candles after it opened.
    "holdout-period": dict(root=f"{SCRATCH}/holdout", arms=("P2", "P5"),
                           instruments=FIVE, split="2025-11-24",
                           labels=("2:1 (the book)", "5:1")),
    # 3.52 axis B: same comparison on six FX pairs that produced none of the finding.
    "holdout-instruments": dict(root=f"{SCRATCH}/holdout", arms=("R2", "R5"),
                                instruments=SIXFX, split=None,
                                labels=("2:1 (the book)", "5:1")),
    # 3.50: module 7's host-validity rule, off vs on.
    "host-rule": dict(root=f"{SCRATCH}/book2", arms=("A", "B"),
                      instruments=SIX, split=None,
                      labels=("hosts unchecked", "hosts must be imbalances")),
}


def trades(root, arm, instrument):
    path = os.path.join(root, f"{arm}-{instrument}", "simulation-result.json")
    if not os.path.exists(path):
        return None
    strategies = json.load(open(path))["strategies"]
    hit = [s for s in strategies if s["strategyId"] == "alfonso"]
    return (hit[0].get("trades") or []) if hit else []


def quote_rate(t):
    """Quote-currency to account-currency rate for one trade, derived from its own P&L.

    `quantity` is in units of the base currency, so |entry - stop| * quantity is denominated in the
    QUOTE currency while netProfitLoss is in the account currency. For a USD-quoted instrument these
    coincide and the rate is 1.0; for GBP/JPY on a USD account it is about 0.0063, and dividing an
    unconverted risk into a USD P&L understates R by a factor of ~158 - which silently removed
    GBP/JPY from every pooled average (3.63).

    Derived per trade rather than taken from --quote-rate so it cannot drift from the run.
    """
    move = (t["entryPrice"] - t["exitPrice"]) if t["side"] == "Sell" else (t["exitPrice"] - t["entryPrice"])
    denominator = move * t["quantity"]
    if abs(denominator) < 1e-9:
        return 1.0
    rate = t["grossProfitLoss"] / denominator
    # A rate far from a plausible FX quote means the trade is degenerate, not that the market moved.
    return rate if 1e-6 < rate < 1e6 else 1.0


def true_r(t):
    risk = abs(t["entryPrice"] - t["stopLossPrice"]) * t["quantity"] * quote_rate(t)
    return t["netProfitLoss"] / risk if risk > 0 else None


def stats(ts):
    rs = [r for r in (true_r(t) for t in ts) if r is not None]
    n = len(rs)
    if n == 0:
        return dict(n=0, mean=None, se=None, net=0.0, win=None)
    m = sum(rs) / n
    sd = math.sqrt(sum((x - m) ** 2 for x in rs) / (n - 1)) if n > 1 else 0.0
    return dict(n=n, mean=m, se=sd / math.sqrt(n),
                net=sum(t["netProfitLoss"] for t in ts),
                win=sum(1 for t in ts if t["netProfitLoss"] > 0) / len(ts))


def section(title, root, arms, labels, instruments, keep):
    print(f'\n{"="*100}\n{title}\n{"="*100}')
    a, b = arms
    print(f'{"inst":9}{a+" n":>7}{a+" avgR":>11}{a+" win":>9}{a+" net":>12}  |'
          f'{b+" n":>7}{b+" avgR":>11}{b+" win":>9}{b+" net":>12}')
    pool = {a: [], b: []}
    for inst in instruments:
        cells = [inst]
        for arm in arms:
            ts = trades(root, arm, inst)
            if ts is None:
                cells += ["-", "-", "-", "-"]
                continue
            ts = [t for t in ts if keep(t)]
            pool[arm] += ts
            s = stats(ts)
            cells += [s["n"],
                      f'{s["mean"]:+.4f}' if s["mean"] is not None else "-",
                      f'{s["win"]:.1%}' if s["win"] is not None else "-",
                      f'{s["net"]:,.0f}']
        print(f"{cells[0]:9}{cells[1]:>7}{cells[2]:>11}{cells[3]:>9}{cells[4]:>12}  |"
              f"{cells[5]:>7}{cells[6]:>11}{cells[7]:>9}{cells[8]:>12}")

    print()
    got = {}
    for arm, label in zip(arms, labels):
        s = stats(pool[arm])
        got[arm] = s
        if s["n"]:
            positives = sum(1 for i in instruments
                            if (stats([t for t in (trades(root, arm, i) or []) if keep(t)])["net"] or 0) > 0)
            print(f'  {arm} ({label}): n={s["n"]}  win={s["win"]:.1%}  avgR={s["mean"]:+.4f}  '
                  f'95% CI [{s["mean"]-1.96*s["se"]:+.4f}, {s["mean"]+1.96*s["se"]:+.4f}]  '
                  f'net={s["net"]:,.0f}  instruments net-positive={positives}/{len(instruments)}')
    x, y = got[a], got[b]
    if x["n"] and y["n"]:
        d = y["mean"] - x["mean"]
        sd = math.sqrt(x["se"] ** 2 + y["se"] ** 2)
        print(f'\n  {b} - {a}: {d:+.4f}R  95% CI [{d-1.96*sd:+.4f}, {d+1.96*sd:+.4f}]'
              f'   trade reduction {1-y["n"]/x["n"]:.1%}')
    return got


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--experiment", choices=sorted(EXPERIMENTS))
    parser.add_argument("--root")
    parser.add_argument("--arms", help="two comma-separated output-directory prefixes")
    parser.add_argument("--instruments")
    parser.add_argument("--split", help="YYYY-MM-DD; report before and after separately")
    parser.add_argument("--labels", help="two comma-separated arm descriptions")
    args = parser.parse_args()

    if args.experiment:
        spec = dict(EXPERIMENTS[args.experiment])
    else:
        if not (args.root and args.arms and args.instruments):
            parser.error("give --experiment, or --root with --arms and --instruments")
        spec = dict(root=args.root, arms=tuple(args.arms.split(",")),
                    instruments=args.instruments.split(","), split=args.split,
                    labels=tuple((args.labels or args.arms).split(",")))
    if args.split:
        spec["split"] = args.split

    root, arms, labels = spec["root"], spec["arms"], spec["labels"]
    instruments, split = spec["instruments"], spec["split"]

    if not split:
        section("whole run", root, arms, labels, instruments, lambda t: True)
        return 0

    section(f"HELD-OUT PERIOD (opened before {split}) - did not produce the hypothesis",
            root, arms, labels, instruments, lambda t: t["openedAt"][:10] < split)
    section(f"IN-SAMPLE PERIOD (opened on or after {split}) - did produce it",
            root, arms, labels, instruments, lambda t: t["openedAt"][:10] >= split)
    return 0


if __name__ == "__main__":
    sys.exit(main())
