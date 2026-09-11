#!/usr/bin/env python3
"""Publish the first 15m/5m direction experiment beside its unchanged zone-target control."""
import argparse
import csv
import json
from collections import Counter
from pathlib import Path

from alfonso_dashboard_export import PERIODS, historical_context, read, stamp, trade_context
from alfonso_opposing_target_export import validate_trade
from alfonso_trend_trade_export import match_decision


def export(root, base):
    source = root / 'lower-aligned'
    result, manifest = read(source / 'simulation-result.json'), read(source / 'manifest.json')
    strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
    simulation = source / 'simulations' / result['simulationId'].replace('-', '')
    assert strategy['isComplete'] and (simulation / 'COMPLETE').exists(), 'Incomplete replay'
    control = next(r for r in read(base / 'Dashboard/public/data/backtests/alfonso-opposing-targets.json')['runs']
                   if r['arm'] == 'both-zone')
    assert result['inputHash'] == control['inputHash'] and result['processedBaseCandles'] == 253035
    assert all(manifest[key] == control[key] for key in ('instrument', 'from', 'to'))
    history = historical_context(base / '.cache/historical', read(simulation / 'manifest.json'),
                                 periods={**PERIODS, '5m': 300})
    decisions, observations = {}, Counter()
    with (root / 'lower-aligned.csv').open(encoding='utf-8-sig', newline='') as handle:
        for row in csv.DictReader(handle):
            observations[row['outcome']] += 1
            wanted = 'Uptrend' if row['side'] == 'Demand' else 'Downtrend'
            assert row['lowerTrend'] == row['confirmationTrend'] == wanted, row
            assert row['entryTimeframe'] == 'Lower'
            assert stamp(row['confirmationClosedAt']) == stamp(row['at']), row
            assert int(stamp(row['at'])) % 900 == 0, row
            if row['outcome'] == 'Entered':
                decisions.setdefault(stamp(row['at']), []).append(row)
    trades, records = read(source / 'improved-progressive.json')['trades'], []
    for trade in trades:
        decision = match_decision(trade, decisions)
        validate_trade(trade, decision)
        context = trade_context(history, trade)
        assert any(stamp(c['availableAt']) == stamp(trade['signalCreatedAt']) for c in context['5m'])
        records.append({'trade': trade, 'decision': decision, 'candles': context.pop('1m'), 'timeframes': context})
    perf = strategy['performance']
    assert len(trades) == perf['tradeCount']
    assert sum(t['netProfitLoss'] > 0 for t in trades) == perf['winningTrades']
    assert abs(sum(t['netProfitLoss'] for t in trades) - perf['netProfit']) < 1e-6
    # Add matching 5m candle context to the reused control; no hindsight trend labels are invented.
    for record in control['records']:
        context = trade_context(history, record['trade'])
        record['candles'], record['timeframes'] = context.pop('1m'), context
    run = {key: manifest[key] for key in ('instrument', 'from', 'to', 'generatedAt')}
    run.update(id='lower-aligned', arm='lower-aligned', label='15m direction + 5m agreement',
               simulationId=result['simulationId'], inputHash=result['inputHash'], performance=perf, records=records)
    output = base / 'Dashboard/public/data/backtests/alfonso-lower-alignment.json'
    output.write_text(json.dumps({'schemaVersion': 1, 'title': 'Silver · 15m direction with 5m confirmation',
                                 'generatedAt': manifest['generatedAt'], 'runs': [run, control]}, separators=(',', ':')))
    print('PASS: completed replay, identical input, all candidate directions aligned, 15m decision cadence, causal brackets and P/L.')
    print('New:', {k: perf[k] for k in ('tradeCount', 'winningTrades', 'netProfit', 'profitFactor', 'maximumDrawdown')})
    print('Control:', {k: control['performance'][k] for k in ('tradeCount', 'winningTrades', 'netProfit')})
    print('Candidate observations:', dict(observations))
    print('Trades:', [{k: t[k] for k in ('openedAt', 'side', 'exitReason', 'netProfitLoss')} for t in trades])
    print('Exported:', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    args = parser.parse_args()
    export(args.root, Path(__file__).resolve().parents[1])
