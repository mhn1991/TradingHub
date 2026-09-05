#!/usr/bin/env python3
"""Build the Alfonso trade-inspection page - the artifact described in PROJECT_STATE.md 3.54.

Reads both arms of a BacktestRunner A/B, matches trades across arms by setup id, recovers each
trade's zone geometry, slices a candle window per trade from the historical cache, and injects the
result into alfonso_trade_viz.template.html.

  ./tools/alfonso_trade_viz.py --root /mnt/storage/scratch/alfonso/m3ab --out trades.html

ZONE RECOVERY
-------------
The logged `setupReason` rounds prices to two decimals, which is unusable on FX - every EUR/USD zone
reads "1.17/1.17". The trade record does not round, so the zone is reconstructed from entry and stop
instead. Entry is the proximal line (placement is Proximal by default and the 3.53/3.54 runs used
defaults); the stop is the distal padded by 25% of the zone width, so

    distal = (stop + padding * proximal) / (1 + padding)

Checked against the two-decimal value in `stopSource` on every trade - a run made with
--alfonso-half-entry puts the entry at the midpoint instead, the inversion does not hold, and the
mismatch check below fails loudly rather than drawing a wrong zone.

CANDLES
-------
Windows are aggregated from 1m cache data to roughly 240 bars, so the interval differs per trade and
is reported on each chart. Bucket indices are emitted rather than timestamps, so market gaps stay
gaps instead of becoming flat bars.
"""
import argparse
import glob
import gzip
import json
import os
import re
import sys
from datetime import datetime, timezone

PADDING = 0.25

INSTRUMENTS = {
    "gold":   ("XAU/USD", "METAL_XAU_USD_1m_20251103_20260723", 2),
    "silver": ("XAG/USD", "METAL_XAG_USD_1m_20251124_20260723", 3),
    "nas100": ("NAS100", "CFD_NAS100_USD_1m_20251124_20260723", 1),
    "us30":   ("US30", "CFD_US30_USD_1m_20251124_20260723", 1),
    "eurusd": ("EUR/USD", "FX_EUR_USD_1m_20251124_20260723", 5),
    "gbpjpy": ("GBP/JPY", "FX_GBP_JPY_1m_20251124_20260723", 3),
}

ZONE = re.compile(r"Zone [\d.]+/([\d.]+) \(([A-Za-z]+), ([\d.]+):1, ([^)]*)\)")
STATED = re.compile(r"distal ([\d.]+) padded")

# The agent's timeframe sequence. The 3.53/3.54 runs passed no --alfonso-top/middle/lower, so these
# are AlfonsoStrategyOptions' defaults - module 8's scalping sequence. The zone an order rests on
# belongs to one of these, which is NOT the interval the chart is drawn at: that is chosen per trade
# to fit the window. Pass --sequence when a run used a different one.
DEFAULT_SEQUENCE = "4h,1h,15m"


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def cache_dir():
    return os.environ.get("ALFONSO_CACHE") or os.path.join(repo_root(), ".cache", "historical")


def default_root():
    return os.path.join(os.environ.get("ALFONSO_DATA") or "/mnt/storage/scratch/alfonso", "m3ab")


def ts(text):
    return datetime.fromisoformat(text).replace(tzinfo=timezone.utc).timestamp()


def read_trades(root, arm, tag):
    path = os.path.join(root, f"{arm}-{tag}", "simulation-result.json")
    if not os.path.exists(path):
        return None
    with open(path) as handle:
        return {t["setupId"]: t for t in json.load(handle)["strategies"][0]["trades"]}


def true_r(trade):
    """netProfitLoss / (|entry - stop| * quantity) - profit per unit of risk actually taken."""
    risk = abs(trade["entryPrice"] - trade["stopLossPrice"]) * trade["quantity"]
    return trade["netProfitLoss"] / risk if risk else 0.0


def entry_role(reason):
    """Which timeframe's zone the order was planned at, read from the scenario's action sentence."""
    action = reason.split(".")[1] if reason.count(".") >= 1 else reason
    if "at the middle timeframe" in action:
        return "middle"
    if "at lower" in action:
        return "lower"
    if "at top" in action:
        return "top"
    return None


def parse(trade, tag, arm, label, digits):
    reason = trade.get("setupReason") or ""
    match = ZONE.search(reason)
    strength, ratio, accs = None, None, []
    if match:
        strength, ratio = match.group(2), float(match.group(3))
        accs = [a.strip() for a in match.group(4).split(",") if a.strip()]

    entry, stop = trade["entryPrice"], trade["stopLossPrice"]
    distal = (stop + PADDING * entry) / (1 + PADDING)

    stated = STATED.search(trade.get("stopSource") or "")
    if stated:
        want = float(stated.group(1))
        if abs(distal - want) > 0.006 * max(1.0, abs(want)):
            raise SystemExit(
                f"zone recovery failed on {tag} {trade['setupId']}: reconstructed distal {distal:.6f} "
                f"but the run logged {want}. A midpoint entry (--alfonso-half-entry) breaks the "
                f"inversion; re-check before trusting the drawn zones.")

    opened, closed = ts(trade["openedAt"]), ts(trade["closedAt"])
    return {
        "id": f"{tag}:{trade['setupId']}",
        "inst": tag, "label": label, "digits": digits, "arm": arm,
        "side": trade["side"],
        "open": opened, "close": closed, "dur": (closed - opened) / 60.0,
        "entry": entry, "stop": stop,
        "target": trade["takeProfitPrice"], "exit": trade["exitPrice"],
        "proximal": entry, "distal": distal,
        "exitReason": trade["exitReason"],
        # TRUE R, not the JSON's rMultiple. 3.44: that field divides by risk PLUS an assumed
        # round-trip cost, so it is not profit-per-risk and understates the magnitude. This matches
        # tools/alfonso_ab_report.py, so the page's tiles reproduce 3.54's published table.
        "r": true_r(trade), "net": trade["netProfitLoss"],
        "mfeR": trade.get("maximumFavourableExcursionR"),
        "mfeAt": ts(trade["maximumFavourableExcursionAt"]) if trade.get("maximumFavourableExcursionAt") else None,
        "maeR": trade.get("maximumAdverseExcursionR"),
        "maeAt": ts(trade["maximumAdverseExcursionAt"]) if trade.get("maximumAdverseExcursionAt") else None,
        "strength": strength, "ratio": ratio, "accs": accs,
        "scenario": reason.split(".")[0].strip() if reason else "",
        "nested": "nested at" in reason,
        "entryRole": entry_role(reason),
    }


def load_candles(stem):
    hits = sorted(glob.glob(os.path.join(cache_dir(), stem + "_*.jsonl.gz")))
    if not hits:
        raise SystemExit(f"no cache file matching {stem}_*.jsonl.gz in {cache_dir()}")
    bars = []
    with gzip.open(hits[0], "rt", encoding="utf-8-sig") as handle:
        for line in handle:
            row = json.loads(line)
            if "openTime" not in row:
                continue  # the leading manifest line
            bars.append((ts(row["openTime"]), row["open"], row["high"], row["low"], row["close"]))
    bars.sort()
    return bars


UNITS = {"s": 1, "m": 60, "h": 3600, "d": 86400}


def interval_seconds(text):
    match = re.fullmatch(r"(\d+)([smhd])", text.strip())
    if not match:
        raise SystemExit(f"cannot read interval {text!r}; use forms like 15m, 1h, 4h")
    return int(match.group(1)) * UNITS[match.group(2)]


def fixed_window(bars, step, first, last, digits):
    """Candles on a FIXED interval spanning [first, last] - used for the context panes, where the
    interval is the agent's own (top / lower) rather than one chosen to fit the trade."""
    buckets = {}
    for t, o, h, l, c in bars:
        if t < first or t > last:
            continue
        key = int(t // step) * step
        cur = buckets.get(key)
        if cur is None:
            buckets[key] = [o, h, l, c]
        else:
            cur[1] = max(cur[1], h)
            cur[2] = min(cur[2], l)
            cur[3] = c
    keys = sorted(buckets)
    if not keys:
        return None
    return {"step": step, "t0": keys[0],
            "bars": [[int((k - keys[0]) // step)] + [round(v, digits) for v in buckets[k]]
                     for k in keys]}


def bollinger(closes, n=20, mult=2.0):
    """20-period SMA with +/- 2 population standard deviations."""
    out = [None] * len(closes)
    for i in range(n - 1, len(closes)):
        window = closes[i - n + 1:i + 1]
        mean = sum(window) / n
        var = sum((v - mean) ** 2 for v in window) / n
        sd = var ** 0.5
        out[i] = (mean + mult * sd, mean, mean - mult * sd)
    return out


def rsi(closes, n=14):
    """Wilder's RSI. Seeded on the first n changes, then smoothed."""
    out = [None] * len(closes)
    if len(closes) <= n:
        return out
    gains = losses = 0.0
    for i in range(1, n + 1):
        change = closes[i] - closes[i - 1]
        gains += max(change, 0.0)
        losses += max(-change, 0.0)
    avg_gain, avg_loss = gains / n, losses / n
    out[n] = 100.0 if avg_loss == 0 else 100 - 100 / (1 + avg_gain / avg_loss)
    for i in range(n + 1, len(closes)):
        change = closes[i] - closes[i - 1]
        avg_gain = (avg_gain * (n - 1) + max(change, 0.0)) / n
        avg_loss = (avg_loss * (n - 1) + max(-change, 0.0)) / n
        out[i] = 100.0 if avg_loss == 0 else 100 - 100 / (1 + avg_gain / avg_loss)
    return out


def cci(highs, lows, closes, n=20, constant=0.015):
    """Commodity Channel Index over the typical price, with mean absolute deviation."""
    typical = [(highs[i] + lows[i] + closes[i]) / 3 for i in range(len(closes))]
    out = [None] * len(closes)
    for i in range(n - 1, len(typical)):
        window = typical[i - n + 1:i + 1]
        mean = sum(window) / n
        dev = sum(abs(v - mean) for v in window) / n
        out[i] = 0.0 if dev == 0 else (typical[i] - mean) / (constant * dev)
    return out


# Longest indicator lookback plus room for Wilder's smoothing to settle. Bars this far before the
# displayed window are fetched, used for the maths, then dropped - so every candle drawn has values
# rather than a blank leading run.
WARMUP = 60


def slice_window(bars, start, end, digits, target_bars=260):
    """Candle window around a trade, aggregated so the chart lands near `target_bars`.

    Indicators (Bollinger 20/2, RSI 14, CCI 20) are computed on the aggregated series, i.e. on the
    interval the chart actually shows. They are NOT used by the agent - module 1 prohibits exactly
    these - and are carried only as a separate analysis layer the page can toggle.
    """
    span = max(end - start, 900)
    lo, hi = start - span * 0.6, end + span * 0.6
    step = 14400
    for candidate in (60, 300, 900, 3600, 14400):
        if (hi - lo) / candidate <= target_bars:
            step = candidate
            break

    buckets = {}
    for t, o, h, l, c in bars:
        if t < lo - WARMUP * step or t > hi:
            continue
        key = int(t // step) * step
        cur = buckets.get(key)
        if cur is None:
            buckets[key] = [o, h, l, c]
        else:
            cur[1] = max(cur[1], h)
            cur[2] = min(cur[2], l)
            cur[3] = c

    keys = sorted(buckets)
    if not keys:
        return None

    highs = [buckets[k][1] for k in keys]
    lows = [buckets[k][2] for k in keys]
    closes = [buckets[k][3] for k in keys]
    bands, strength, channel = bollinger(closes), rsi(closes), cci(highs, lows, closes)

    shown = [i for i, k in enumerate(keys) if k >= lo]
    if not shown:
        shown = list(range(len(keys)))
    base = keys[shown[0]]

    rows, ind = [], []
    for i in shown:
        k = keys[i]
        rows.append([int((k - base) // step)] + [round(v, digits) for v in buckets[k]])
        band = bands[i]
        ind.append([
            round(band[0], digits) if band else None,
            round(band[1], digits) if band else None,
            round(band[2], digits) if band else None,
            round(strength[i], 1) if strength[i] is not None else None,
            round(channel[i], 1) if channel[i] is not None else None,
        ])

    return {"step": step, "t0": base, "bars": rows, "ind": ind}


def build(root, arms, sequence, quiet=False):
    """`sequence` supplies the top and lower intervals the run used, so the context panes are drawn
    on the timeframes the agent actually read rather than on a display-derived one."""
    top_step = interval_seconds(sequence["top"])
    zone_step = interval_seconds(sequence["lower"])
    trades, candles, context, zonepane = [], {}, {}, {}
    for tag, (label, stem, digits) in INSTRUMENTS.items():
        a, b = read_trades(root, arms[0], tag), read_trades(root, arms[1], tag)
        if a is None and b is None:
            continue
        a, b = a or {}, b or {}
        bars = load_candles(stem)
        for key in sorted(set(a) | set(b)):
            arm = "both" if key in a and key in b else (arms[0] if key in a else arms[1])
            arm = {arms[0]: "a", arms[1]: "b", "both": "both"}[arm]
            record = parse(b.get(key) or a[key], tag, arm, label, digits)
            window = slice_window(bars, record["open"], record["close"], digits)
            if window:
                candles[record["id"]] = window

            # Top timeframe: enough history for a trend to be visible, since that is the claim the
            # scenario text makes and the execution pane is far too short to show it.
            ctx = fixed_window(bars, top_step, record["open"] - 56 * top_step,
                               record["close"] + 8 * top_step, digits)
            if ctx:
                context[record["id"]] = ctx

            # The zone's own timeframe - where the imbalance was actually drawn.
            zp = fixed_window(bars, zone_step, record["open"] - 80 * zone_step,
                              record["close"] + 16 * zone_step, digits)
            if zp:
                zonepane[record["id"]] = zp

            trades.append(record)
        if not quiet:
            print(f"{tag}: {len(set(a) | set(b))} distinct trades")
    return {"trades": trades, "candles": candles, "context": context, "zonepane": zonepane}


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=default_root(),
                        help="directory holding the <arm>-<instrument>/ run outputs")
    parser.add_argument("--arms", default="a,b", help="two comma-separated output-directory prefixes")
    parser.add_argument("--out", default="alfonso-trades.html", help="page to write")
    parser.add_argument("--json", help="also write the raw payload here")
    parser.add_argument("--sequence", default=DEFAULT_SEQUENCE,
                        help="top,middle,lower intervals the run used (default: the agent's own)")
    parser.add_argument("--execution", default="1m",
                        help="the run's --execution-interval, i.e. where fills actually resolve")
    args = parser.parse_args()

    arms = tuple(args.arms.split(","))
    if len(arms) != 2:
        parser.error("--arms takes exactly two comma-separated prefixes")

    top, middle, lower = (v.strip() for v in args.sequence.split(","))
    sequence = {"top": top, "middle": middle, "lower": lower, "execution": args.execution}
    payload = build(args.root, arms, sequence)
    if not payload["trades"]:
        raise SystemExit(f"no runs found under {args.root}")

    payload["sequence"] = sequence

    template = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "alfonso_trade_viz.template.html")
    with open(template) as handle:
        page = handle.read()
    if "/*__DATA__*/" not in page:
        raise SystemExit(f"{template} has no /*__DATA__*/ placeholder")

    blob = json.dumps(payload, separators=(",", ":"))
    with open(args.out, "w") as handle:
        handle.write(page.replace("/*__DATA__*/", blob))

    if args.json:
        with open(args.json, "w") as handle:
            handle.write(blob)

    size = os.path.getsize(args.out) / 1e6
    print(f"\n{len(payload['trades'])} trades, {len(payload['candles'])} execution charts, "
          f"{len(payload['context'])} {sequence['top']} context, {len(payload['zonepane'])} "
          f"{sequence['lower']} zone panes -> {args.out} ({size:.2f} MB)")


if __name__ == "__main__":
    main()
