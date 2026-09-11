#!/usr/bin/env python3
"""Validate completed opposing-target replays and publish them beside fixed-R controls."""
import argparse
import csv
import json
import re
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

from alfonso_dashboard_export import historical_context, read, stamp, trade_context
from alfonso_trend_trade_export import match_decision


VARIANTS = [('baseline', 'No trend fix'), ('price-break', 'Price-break invalidation'),
            ('confirmed', 'Confirmed structure'), ('both', 'Both trend fixes')]


def validate_trade(trade, decision):
    source = trade.get('targetSource') or ''
    match = re.fullmatch(r'opposing (Supply|Demand) 15m: proximal ([\d.]+), distal ([\d.]+); '
                         r'base (\S+) to (\S+); confirmed close (\S+); state (Fresh|Tested|UsedUp)', source)
    if not match:
        raise ValueError(f'Missing/invalid target provenance: {source}')
    kind, proximal, distal, base_start, base_end, confirmed, _ = match.groups()
    buy = trade['side'] == 'Buy'
    assert kind == ('Supply' if buy else 'Demand'), trade['setupId']
    target, stop, entry = trade['takeProfitPrice'], trade['stopLossPrice'], trade['signalPrice']
    assert abs(float(proximal) - target) < 1e-9, trade['setupId']
    assert (stop < entry < target) if buy else (target < entry < stop), trade['setupId']
    assert (target <= float(distal)) if buy else (target >= float(distal)), trade['setupId']
    assert stamp(base_start) <= stamp(base_end) < stamp(confirmed) <= stamp(trade['signalCreatedAt']), trade['setupId']
    assert stamp(trade['signalCreatedAt']) <= stamp(trade['openedAt']), trade['setupId']
    assert abs(float(decision['target']) - target) < 1e-9, trade['setupId']
    assert abs(float(decision['stop']) - stop) < 1e-9, trade['setupId']
    assert trade['stopAmendmentCount'] == 0, trade['setupId']
    assert 'structural 15m' in trade['stopSource'] and 'target ' not in trade['stopSource'], trade['setupId']
    anchor = re.search(r'at (\d{4}-\d\d-\d\d \d\d:\d\d) UTC', trade['stopSource'])
    if anchor:
        available = datetime.strptime(anchor[1], '%Y-%m-%d %H:%M').replace(tzinfo=timezone.utc) + timedelta(minutes=45)
        assert available <= datetime.fromisoformat(trade['signalCreatedAt']), trade['setupId']
    return abs(target - entry) / abs(entry - stop)


def export(root, controls, output, cache):
    fixed = read(controls)
    assert len(fixed['runs']) == 8, 'Expected the eight completed fixed-R controls'
    input_hash = fixed['runs'][0]['inputHash']
    assert all(run['inputHash'] == input_hash for run in fixed['runs']), 'Control data differs'
    runs, history, candles = [], None, None
    for variant, label in VARIANTS:
        tag = f'{variant}-zone'
        source = root / tag
        manifest, result = read(source / 'manifest.json'), read(source / 'simulation-result.json')
        strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
        simulation = source / 'simulations' / result['simulationId'].replace('-', '')
        assert strategy['isComplete'] and (simulation / 'COMPLETE').exists(), f'{tag}: incomplete replay'
        assert result['inputHash'] == input_hash and manifest['instrument'] == 'METAL:XAG/USD', tag
        assert all(manifest[key] == fixed['runs'][0][key] for key in ('from', 'to')), tag
        if history is None:
            history = historical_context(cache, read(simulation / 'manifest.json'))
            candles = result['processedBaseCandles']
        assert candles == result['processedBaseCandles'] == 253035, tag
        decisions, outcomes = {}, Counter()
        with (root / f'{tag}.csv').open(encoding='utf-8-sig', newline='') as handle:
            for row in csv.DictReader(handle):
                outcomes[row['outcome']] += 1
                if row['outcome'] == 'Entered':
                    decisions.setdefault(stamp(row['at']), []).append(row)
        trades = read(source / 'improved-progressive.json')['trades']
        perf = strategy['performance']
        assert len(trades) == perf['tradeCount'], tag
        assert abs(sum(t['netProfitLoss'] for t in trades) - perf['netProfit']) < 1e-6, tag
        assert sum(t['netProfitLoss'] > 0 for t in trades) == perf['winningTrades'], tag
        records, rewards = [], []
        for trade in trades:
            decision = match_decision(trade, decisions)
            rewards.append(validate_trade(trade, decision))
            context = trade_context(history, trade)
            records.append({'trade': trade, 'decision': decision,
                            'candles': context.pop('1m'), 'timeframes': context})
        runs.append({'id': tag, 'arm': tag, 'label': f'{label} · opposing zone',
                     'instrument': manifest['instrument'], 'generatedAt': manifest['generatedAt'],
                     'from': manifest['from'], 'to': manifest['to'], 'simulationId': result['simulationId'],
                     'inputHash': input_hash, 'performance': perf, 'records': records})
        print(tag, {k: perf[k] for k in ('tradeCount', 'winningTrades', 'netProfit', 'profitFactor',
                                       'maximumDrawdown', 'averageR')},
              'planned R range:', (min(rewards), max(rewards)) if rewards else None,
              'candidate observations:', dict(outcomes))
        for trade in trades:
            if trade['openedAt'].startswith('2026-07-17T03:21:00'):
                print('July 17:', tag, {k: trade[k] for k in ('takeProfitPrice', 'closedAt', 'exitPrice',
                      'exitReason', 'netProfitLoss', 'targetSource')})
    runs.extend(fixed['runs'])
    payload = {'schemaVersion': 1, 'title': 'Silver · opposing-zone targets vs fixed R',
               'generatedAt': max(run['generatedAt'] for run in runs), 'runs': runs}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, separators=(',', ':')))
    print(f'PASS: four new full replays, {candles} matching candles; causal targets, stops and P/L checked.')
    print(f'Exported {len(runs)} runs and {sum(len(r["records"]) for r in runs)} trades to {output}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    base = Path(__file__).resolve().parents[1]
    parser.add_argument('root', type=Path)
    parser.add_argument('--controls', type=Path, default=base / 'Dashboard/public/data/backtests/alfonso-trend-trades.json')
    parser.add_argument('--out', type=Path, default=base / 'Dashboard/public/data/backtests/alfonso-opposing-targets.json')
    parser.add_argument('--cache', type=Path, default=base / '.cache/historical')
    args = parser.parse_args()
    export(args.root, args.controls, args.out, args.cache)
