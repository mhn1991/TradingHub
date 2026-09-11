#!/usr/bin/env python3
"""Publish saved Alfonso stop-floor tests and their original decision/execution records.

Usage: python3 tools/alfonso_dashboard_export.py /mnt/storage/scratch/alfonso/m7ab
"""
import argparse
import csv
import gzip
import json
from bisect import bisect_left, bisect_right
from datetime import datetime
from pathlib import Path


def read(path):
    return json.loads(path.read_text())


def stamp(value):
    return datetime.fromisoformat(value).timestamp()


PERIODS = {'15m': 900, '1h': 3600, '4h': 14400}


def aggregate(rows, include_minutes=False, periods=None):
    """UTC-aligned complete buckets only, matching the simulator's zero-gap policy.

    Unlike alfonso_export_bars.py, missing leading/interior/trailing minutes never
    produce a completed candle. No forming candle is exported.
    """
    periods = PERIODS if periods is None else periods
    result = {interval: [] for interval in periods}
    if include_minutes:
        result['1m'] = []
    pending = {}
    previous = None
    for row in rows:
        opened = int(stamp(row['openTime']))
        if previous is not None and opened <= previous:
            raise ValueError('Historical candles must be strictly ordered')
        if stamp(row['closeTime']) != opened + 60 or opened % 60:
            raise ValueError('Expected complete, aligned one-minute candles')
        if previous is not None and opened != previous + 60:
            pending.clear()
        previous = opened
        if include_minutes:
            result['1m'].append({**{key: row[key] for key in
                                    ['openTime', 'open', 'high', 'low', 'close', 'volume']},
                                 'availableAt': row['closeTime']})
        for interval, seconds in periods.items():
            if opened % seconds == 0:
                pending[interval] = {key: row[key] for key in
                                     ['openTime', 'open', 'high', 'low', 'close', 'volume']}
            elif interval in pending:
                candle = pending[interval]
                candle['high'] = max(candle['high'], row['high'])
                candle['low'] = min(candle['low'], row['low'])
                candle['close'] = row['close']
                candle['volume'] += row['volume']
            if interval in pending and (opened + 60) % seconds == 0:
                candle = pending.pop(interval)
                candle['availableAt'] = row['closeTime']
                result[interval].append(candle)
    return result


def historical_context(cache, manifest, periods=None):
    start, end = manifest['warmupFrom'], manifest['to']
    stem = manifest['instrument'].replace(':', '_').replace('/', '_')
    dates = [datetime.fromisoformat(value).strftime('%Y%m%d') for value in (start, end)]
    paths = list(cache.glob(f'{stem}_1m_{dates[0]}_{dates[1]}_*.jsonl.gz'))
    if len(paths) != 1:
        raise ValueError(f'Expected one historical cache for {stem}, found {len(paths)}')
    with gzip.open(paths[0], 'rt', encoding='utf-8-sig') as handle:
        header = json.loads(next(handle))
        if (header['instrument'] != manifest['instrument'] or header['interval'] != '1m'
                or stamp(header['from']) != stamp(start) or stamp(header['to']) != stamp(end)):
            raise ValueError(f'Historical cache does not match simulation: {paths[0]}')
        series = aggregate((json.loads(line) for line in handle if line.strip()), include_minutes=True, periods=periods)
    return {interval: (candles, [stamp(row['availableAt']) for row in candles])
            for interval, candles in series.items()}


def trade_context(history, trade):
    result = {}
    decision = stamp(trade['signalCreatedAt'])
    end = stamp(trade['closedAt'] or trade['openedAt'] or trade['signalCreatedAt'])
    for interval, (candles, times) in history.items():
        split = bisect_right(times, decision)
        # Count real completed bars, not elapsed periods (weekends/gaps do not count).
        # The candle containing an intrabar exit is not one of the 200 after it.
        stop = bisect_left(candles, max(decision, end), key=lambda row: stamp(row['openTime'])) + 200
        result[interval] = candles[max(0, split - 200):stop]
    return result


def export(root, output, cache):
    runs = []
    histories = {}
    matched = 0
    charted = 0
    for market in ['gold', 'silver', 'nas100', 'us30', 'eurusd', 'gbpjpy']:
        for arm, label in [('a', 'No minimum stop'), ('b', 'Minimum 0.25 × top ATR'),
                           ('c', 'Minimum 0.50 × top ATR')]:
            run_id = f'{arm}-{market}'
            source = root / run_id
            manifest = read(source / 'manifest.json')
            result = read(source / 'simulation-result.json')
            strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
            if not strategy['isComplete']:
                raise ValueError(f'{run_id} has not completed')
            simulation = source / 'simulations' / result['simulationId'].replace('-', '')
            if not (simulation / 'COMPLETE').exists():
                raise ValueError(f'{run_id} has no completion marker')
            replay_manifest = read(simulation / 'manifest.json')
            history_key = replay_manifest['inputStreamId']
            if history_key not in histories:
                # Keep only one market's full minute history in memory.
                histories.clear()
                histories[history_key] = historical_context(cache, replay_manifest)
            decisions = {}
            with (root / f'{run_id}.csv').open(encoding='utf-8-sig', newline='') as handle:
                for row in csv.DictReader(handle):
                    if row['outcome'] == 'Entered':
                        decisions.setdefault(stamp(row['at']), []).append(row)
            trades = read(source / 'improved-progressive.json')['trades']
            if len(trades) != strategy['performance']['tradeCount']:
                raise ValueError(f'{run_id}: trade count mismatch')
            records = []
            for trade in trades:
                candidates = [row for row in decisions.get(stamp(trade['signalCreatedAt']), [])
                              if row['side'] == ('Supply' if trade['side'] == 'Sell' else 'Demand')
                              and abs(float(row['proximal']) - trade['entryPrice']) < 1e-7]
                if len(candidates) != 1:
                    raise ValueError(f'{run_id}: ambiguous/missing decision for {trade["setupId"]}')
                matched += 1
                context = trade_context(histories[history_key], trade)
                candles = context.pop('1m')
                charted += bool(candles)
                records.append({'trade': trade, 'decision': candidates[0], 'candles': candles,
                                'timeframes': context})
            runs.append({'id': run_id, 'arm': arm, 'label': label,
                         'instrument': manifest['instrument'], 'generatedAt': manifest['generatedAt'],
                         'from': manifest['from'], 'to': manifest['to'],
                         'simulationId': result['simulationId'], 'performance': strategy['performance'],
                         'records': records})
    payload = {'schemaVersion': 1, 'title': 'Alfonso · stop-distance tests',
               'generatedAt': max(run['generatedAt'] for run in runs), 'runs': runs}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, separators=(',', ':')))
    print(f'Exported {len(runs)} runs, {matched} matched decisions, {charted} trade charts to {output}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1] /
                        'Dashboard/public/data/backtests/alfonso-tests.json')
    parser.add_argument('--cache', type=Path, default=Path(__file__).resolve().parents[1] /
                        '.cache/historical')
    args = parser.parse_args()
    export(args.root, args.out, args.cache)
