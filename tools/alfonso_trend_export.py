"""Export continuous live-feed trend audits, with descriptive (not ground-truth) comparisons."""
import argparse
import json
import statistics
from datetime import datetime, timezone
from pathlib import Path

ARMS = ['Baseline', 'Price-break invalidation', 'Confirmed structure', 'Both fixes']
STATES = ['Unknown', 'Uptrend', 'Downtrend', 'OutOfAlignment']
MARKETS = {'gold': 'Gold', 'silver': 'Silver', 'nas100': 'NAS100 / US100',
           'us30': 'US30', 'gbpjpy': 'GBPJPY', 'eurusd': 'EURUSD'}


def epoch(value):
    return int(datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp())


def percent(numerator, denominator):
    return round(100 * numerator / denominator, 2) if denominator else None


def direction(state):
    return 1 if state == 1 else -1 if state == 2 else 0


def summarize(rows):
    """All metrics share a candle timeline; neutral labels are never counted as correct predictions.

    Breakout reference: a close outside the preceding 12 candles' high/low range. Only changes
    of breakout direction are events. This is a causal price reference, not Alfonso's definition
    of trend. Detection gets at most 12 subsequent candles, stopping before the next opposite
    event. Events without 12 subsequent samples are censored, not marked as misses.
    """
    reference = 0
    raw_events = []
    for i in range(12, len(rows)):
        close = rows[i]['c']
        previous = rows[i-12:i]
        current = 1 if close > max(r['h'] for r in previous) else (
            -1 if close < min(r['l'] for r in previous) else reference)
        if current != reference and reference:
            raw_events.append((i, current))
        reference = current
    events = []
    for k, (i, current) in enumerate(raw_events):
        if i + 12 >= len(rows):
            continue
        stop = min(i + 13, raw_events[k+1][0] if k+1 < len(raw_events) else len(rows))
        delays = [next((j-i for j in range(i, stop)
                        if direction(rows[j]['s'][arm][0]) == current), None) for arm in range(4)]
        events.append({'index': i, 'at': rows[i]['at'], 'direction': current, 'delays': delays})
    stats = []
    common = [i for i in range(len(rows)-4)
              if all(direction(s[0]) for s in rows[i]['s']) and rows[i+4]['c'] != rows[i]['c']]
    for arm in range(4):
        labels = [r['s'][arm][0] for r in rows]
        scored = [i for i in range(len(rows)-4)
                  if direction(labels[i]) and rows[i+4]['c'] != rows[i]['c']]
        def agrees(i):
            return direction(labels[i]) * (rows[i+4]['c'] - rows[i]['c']) > 0
        delays = [e['delays'][arm] for e in events if e['delays'][arm] is not None]
        stats.append({
            'bars': len(rows), 'directionalPercent': percent(sum(bool(direction(s)) for s in labels), len(rows)),
            'disagreementPercent': percent(sum(s != rows[i]['s'][0][0] for i, s in enumerate(labels)), len(rows)),
            'stateChanges': sum(a != b for a, b in zip(labels, labels[1:])),
            'forwardMatchPercent': percent(sum(agrees(i) for i in scored), len(scored)), 'scoredBars': len(scored),
            'commonMatchPercent': percent(sum(agrees(i) for i in common), len(common)), 'commonBars': len(common),
            'breakoutEvents': len(events), 'breakoutsRecognized': len(delays),
            'medianRecognitionBars': statistics.median(delays) if delays else None,
        })
    return stats, events


def read_rows(path, instrument, start, end):
    grouped = {15: [], 60: [], 240: []}
    reasons, reason_ids = [], {}
    last = {}
    with path.open() as source:
        for line in source:
            raw = json.loads(line)
            interval = raw['intervalMinutes']
            if interval not in grouped or raw['instrument'] != instrument or len(raw['states']) != 4:
                raise ValueError(f'Unexpected instrument/timeframe/arms in {path}')
            t, at = epoch(raw['openTime']), epoch(raw['availableAt'])
            if at < t + interval * 60:
                raise ValueError(f'Unclosed candle in {path}: {raw["openTime"]}')
            if interval in last and (t <= last[interval][0] or at <= last[interval][1]):
                raise ValueError(f'Duplicate or unordered candle in {path}')
            last[interval] = (t, at)
            if not start <= at <= end:
                continue
            states = []
            for state in raw['states']:
                reason = state['reason']
                if reason not in reason_ids:
                    reason_ids[reason] = len(reasons)
                    reasons.append(reason)
                states.append([STATES.index(state['trend']), reason_ids[reason],
                               state['opposingEliminations'], state['isOverExtended']])
            grouped[interval].append({'t': t, 'at': at, 'o': raw['open'], 'h': raw['high'],
                                      'l': raw['low'], 'c': raw['close'], 's': states})
    if any(not rows for rows in grouped.values()):
        raise ValueError(f'Missing timeframe observations in {path}')
    return grouped, reasons


def write_json(path, payload):
    temporary = path.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(payload, separators=(',', ':'), allow_nan=False))
    temporary.replace(path)


def export(root, out, tags):
    out.mkdir(parents=True, exist_ok=True)
    revision = (root / 'code-revision.txt').read_text().strip()
    index = {'schemaVersion': 1, 'generatedAt': datetime.now(timezone.utc).isoformat(),
             'revision': revision, 'arms': ARMS, 'states': STATES, 'instruments': []}
    for tag in tags:
        manifest = json.loads((root / tag / 'manifest.json').read_text())
        result = json.loads((root / tag / 'simulation-result.json').read_text())
        start, end = epoch(manifest['from']), epoch(manifest['to'])
        grouped, reasons = read_rows(root / f'{tag}.jsonl', manifest['instrument'], start, end)
        item = {'id': tag, 'label': MARKETS[tag], 'instrument': manifest['instrument'],
                'from': manifest['from'], 'to': manifest['to'], 'simulationId': result['simulationId'],
                'inputHash': result['inputHash'], 'timeframes': []}
        for interval, rows in grouped.items():
            stats, events = summarize(rows)
            filename = f'alfonso-trend-{tag}-{interval}.json'
            write_json(out / filename, {'schemaVersion': 1, 'rows': rows, 'reasons': reasons, 'events': events})
            item['timeframes'].append({'minutes': interval, 'file': filename, 'stats': stats,
                                       'firstAt': rows[0]['at'], 'lastAt': rows[-1]['at']})
        # No P&L comparison: shadows do not trade. Preserve provenance for checking baseline parity.
        item['baselineTradeCount'] = manifest['runs'][0]['performance']['tradeCount']
        index['instruments'].append(item)
        print(f'{MARKETS[tag]}: ' + ', '.join(f'{n}m={len(r)} bars' for n, r in grouped.items()))
    write_json(out / 'alfonso-trend-index.json', index)
    return index


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1] / 'Dashboard/public/data/backtests')
    parser.add_argument('--instruments', nargs='+', choices=MARKETS, default=list(MARKETS))
    args = parser.parse_args()
    export(args.root, args.out, args.instruments)
