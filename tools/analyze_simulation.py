#!/usr/bin/env python3
"""
Query a completed simulation strategy directory without re-deriving the funnel/lifecycle
data by hand every time.

A strategy directory (Dashboard/public/data/simulations/{simId}/strategies/{profileKey}/)
contains, since the funnel-summary/signals.ndjson/events.ndjson.gz artifacts were added:

  funnel-summary.json  - event-type x reason-code counts, plus signal-to-trade conversion
  signals.ndjson        - one row per distinct SetupId with its terminal disposition
  events.ndjson.gz       - every event this strategy emitted, one JSON object per line
  events-NNNNNN.json.gz  - the older chunked per-frame replay format (dashboard scrubbing UI)
  trades.ndjson          - executed trades only

Examples:
  analyze_simulation.py funnel   <dir>
  analyze_simulation.py signals  <dir> --disposition Rejected
  analyze_simulation.py signals  <dir> --reason-code SpreadAtrHardLimit
  analyze_simulation.py events   <dir> --type TradingConditionRejected
  analyze_simulation.py events   <dir> --setup-id 37a7c5484d83c490cf084d99e8ad090935196d6a68645d262f0bafb3ccab9599
"""

import argparse
import gzip
import json
import sys
from collections import Counter
from pathlib import Path


def load_funnel(strategy_dir: Path) -> dict:
    path = strategy_dir / "funnel-summary.json"
    if not path.exists():
        sys.exit(f"missing {path} (run predates the funnel-summary artifact, or hasn't completed yet)")
    return json.loads(path.read_text())


def iter_signals(strategy_dir: Path):
    path = strategy_dir / "signals.ndjson"
    if not path.exists():
        sys.exit(f"missing {path} (run predates the signals.ndjson artifact, or hasn't completed yet)")
    with path.open(encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if line:
                yield json.loads(line)


def iter_events(strategy_dir: Path):
    path = strategy_dir / "events.ndjson.gz"
    if not path.exists():
        sys.exit(f"missing {path} (run predates the consolidated events.ndjson.gz artifact, or hasn't completed yet)")
    with gzip.open(path, "rt", encoding="utf-8-sig") as handle:
        for line in handle:
            line = line.strip()
            if line:
                yield json.loads(line)


def cmd_funnel(args):
    summary = load_funnel(Path(args.directory))
    print(f"strategy: {summary['strategyId']} ({summary['strategyName']})")
    print(f"generated: {summary['generatedAt']}")
    print()
    print("=== event type counts ===")
    for event_type, count in sorted(summary["eventTypeCounts"].items(), key=lambda kv: -kv[1]):
        print(f"  {count:>7}  {event_type}")
    print()
    print("=== reason codes by event type ===")
    for event_type, reasons in summary["reasonCodesByEventType"].items():
        print(f"  {event_type}:")
        for reason, count in sorted(reasons.items(), key=lambda kv: -kv[1]):
            print(f"    {count:>7}  {reason}")
    print()
    conv = summary["signalConversion"]
    print("=== signal conversion ===")
    print(f"  distinct signals:      {conv['distinctSignals']}")
    print(f"  executed:              {conv['executed']}")
    print(f"  rejected:              {conv['rejected']}")
    print(f"  expired/invalidated:   {conv['expiredOrInvalidated']}")
    print(f"  unresolved:            {conv['unresolved']}")
    print(f"  conversion rate:       {conv['conversionRatePercent']}%")
    if conv["rejectionReasonCounts"]:
        print("  rejection reasons:")
        for reason, count in sorted(conv["rejectionReasonCounts"].items(), key=lambda kv: -kv[1]):
            print(f"    {count:>7}  {reason}")


def cmd_signals(args):
    rows = list(iter_signals(Path(args.directory)))
    if args.disposition:
        rows = [r for r in rows if r["disposition"] == args.disposition]
    if args.reason_code:
        rows = [r for r in rows if r.get("lastReasonCode") == args.reason_code]
    if args.setup_id:
        rows = [r for r in rows if r["setupId"] == args.setup_id]

    print(f"{len(rows)} signal(s)")
    dispositions = Counter(r["disposition"] for r in rows)
    for disposition, count in dispositions.most_common():
        print(f"  {count:>5}  {disposition}")
    print()
    for row in rows[: args.limit]:
        r_r = row.get("realizedR")
        r_r_text = f" R={r_r:.2f}" if r_r is not None else ""
        print(
            f"  {row['setupId'][:16]}  {row['firstSeenAt']} -> {row['lastEventAt']}  "
            f"{row['disposition']}{r_r_text}  reason={row.get('lastReasonCode')}"
        )
    if len(rows) > args.limit:
        print(f"  ... and {len(rows) - args.limit} more (use --limit to see more)")


def cmd_events(args):
    matched = 0
    for event in iter_events(Path(args.directory)):
        if args.type and event.get("type") != args.type:
            continue
        if args.reason_code and event.get("reasonCode") != args.reason_code:
            continue
        if args.setup_id and event.get("setupId") != args.setup_id:
            continue
        matched += 1
        if matched > args.limit:
            continue
        print(json.dumps(event))
    print(f"\n{matched} matching event(s)" + (f" (showing first {args.limit})" if matched > args.limit else ""), file=sys.stderr)


def cmd_timeclusters(args):
    """Group matching events by UTC hour-of-day, to spot session/liquidity clustering
    (this is exactly the ad hoc analysis used to confirm the SpreadAtrHardLimit rejections
    were a real low-liquidity-session pattern rather than a bug)."""
    hours = Counter()
    for event in iter_events(Path(args.directory)):
        if args.type and event.get("type") != args.type:
            continue
        if args.reason_code and event.get("reasonCode") != args.reason_code:
            continue
        event_time = event.get("eventTime")
        if not event_time:
            continue
        hour = event_time[11:13]
        hours[hour] += 1
    for hour in sorted(hours):
        count = hours[hour]
        print(f"  {hour}:00 UTC  {'#' * count} ({count})")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    subparsers = parser.add_subparsers(dest="command", required=True)

    p_funnel = subparsers.add_parser("funnel", help="print the event-type/reason-code funnel and conversion rate")
    p_funnel.add_argument("directory", help="path to the strategy directory")
    p_funnel.set_defaults(func=cmd_funnel)

    p_signals = subparsers.add_parser("signals", help="list per-signal dispositions")
    p_signals.add_argument("directory")
    p_signals.add_argument("--disposition", choices=["Completed", "OpenAtEndOfRun", "Rejected", "Expired", "Invalidated", "Unresolved"])
    p_signals.add_argument("--reason-code")
    p_signals.add_argument("--setup-id")
    p_signals.add_argument("--limit", type=int, default=50)
    p_signals.set_defaults(func=cmd_signals)

    p_events = subparsers.add_parser("events", help="stream/filter the raw consolidated event log")
    p_events.add_argument("directory")
    p_events.add_argument("--type", help="e.g. TradingConditionRejected")
    p_events.add_argument("--reason-code")
    p_events.add_argument("--setup-id")
    p_events.add_argument("--limit", type=int, default=200)
    p_events.set_defaults(func=cmd_events)

    p_clusters = subparsers.add_parser("timeclusters", help="bucket matching events by UTC hour-of-day")
    p_clusters.add_argument("directory")
    p_clusters.add_argument("--type")
    p_clusters.add_argument("--reason-code")
    p_clusters.set_defaults(func=cmd_timeclusters)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
