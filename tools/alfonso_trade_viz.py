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
        "r": trade["rMultiple"], "net": trade["netProfitLoss"],
        "mfeR": trade.get("maximumFavourableExcursionR"),
        "mfeAt": ts(trade["maximumFavourableExcursionAt"]) if trade.get("maximumFavourableExcursionAt") else None,
        "maeR": trade.get("maximumAdverseExcursionR"),
        "maeAt": ts(trade["maximumAdverseExcursionAt"]) if trade.get("maximumAdverseExcursionAt") else None,
        "strength": strength, "ratio": ratio, "accs": accs,
        "scenario": reason.split(".")[0].strip() if reason else "",
        "nested": "nested at" in reason,
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


def slice_window(bars, start, end, digits, target_bars=260):
    """Candle window around a trade, aggregated so the chart lands near `target_bars`."""
    span = max(end - start, 900)
    lo, hi = start - span * 0.6, end + span * 0.6
    step = 14400
    for candidate in (60, 300, 900, 3600, 14400):
        if (hi - lo) / candidate <= target_bars:
            step = candidate
            break

    buckets = {}
    for t, o, h, l, c in bars:
        if t < lo or t > hi:
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
    return {
        "step": step,
        "t0": keys[0],
        "bars": [[int((k - keys[0]) // step)] + [round(v, digits) for v in buckets[k]] for k in keys],
    }


def build(root, arms, quiet=False):
    trades, candles = [], {}
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
            trades.append(record)
        if not quiet:
            print(f"{tag}: {len(set(a) | set(b))} distinct trades")
    return {"trades": trades, "candles": candles}


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=default_root(),
                        help="directory holding the <arm>-<instrument>/ run outputs")
    parser.add_argument("--arms", default="a,b", help="two comma-separated output-directory prefixes")
    parser.add_argument("--out", default="alfonso-trades.html", help="page to write")
    parser.add_argument("--json", help="also write the raw payload here")
    args = parser.parse_args()

    arms = tuple(args.arms.split(","))
    if len(arms) != 2:
        parser.error("--arms takes exactly two comma-separated prefixes")

    payload = build(args.root, arms)
    if not payload["trades"]:
        raise SystemExit(f"no runs found under {args.root}")

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
    print(f"\n{len(payload['trades'])} trades, {len(payload['candles'])} charts "
          f"-> {args.out} ({size:.2f} MB)")


if __name__ == "__main__":
    main()
