#!/usr/bin/env python3
"""Validate completed Silver pending-guard and shadow-only experiments against baseline."""
import argparse
import csv
import json
from collections import Counter
from datetime import datetime
from pathlib import Path


def read(path):
    return json.loads(path.read_text())


def run(root):
    output = root / 'lower-aligned'
    result = read(output / 'simulation-result.json')
    strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
    simulation = output / 'simulations' / result['simulationId'].replace('-', '')
    assert strategy['isComplete'] and (simulation / 'COMPLETE').exists(), root
    trades = read(output / 'improved-progressive.json')['trades']
    assert trades == strategy['trades']
    assert len(trades) == strategy['performance']['tradeCount']
    assert abs(sum(t['netProfitLoss'] for t in trades) - strategy['performance']['netProfit']) < 1e-6
    decisions = list(csv.DictReader((root / 'lower-aligned.csv').open(encoding='utf-8-sig')))
    for row in decisions:
        direction = 'Uptrend' if row['side'] == 'Demand' else 'Downtrend'
        assert row['lowerTrend'] == row['confirmationTrend'] == direction
        assert datetime.fromisoformat(row['at']) == datetime.fromisoformat(row['confirmationClosedAt'])
        assert datetime.fromisoformat(row['at']).minute % 15 == 0
    return result, strategy, simulation


def report(baseline, guard, shadow):
    runs = {name: run(root) for name, root in [('baseline', baseline), ('guard', guard), ('shadow', shadow)]}
    control = runs['baseline'][0]
    for result, _, _ in runs.values():
        assert result['inputHash'] == control['inputHash']
        assert result['processedBaseCandles'] == control['processedBaseCandles'] == 253035
    assert runs['shadow'][1]['trades'] == runs['baseline'][1]['trades'], 'Shadow changed actual trades'
    assert (shadow / 'lower-aligned.csv').read_bytes() == (baseline / 'lower-aligned.csv').read_bytes(), 'Shadow changed candidates'
    journal = runs['guard'][2] / 'strategies/alfonso/journal.ndjson'
    cancellations = sorted({(r['timestamp'], r['message']) for line in journal.open()
                            if (r := json.loads(line)).get('message', '').startswith('5m pending guard:')})
    before = {t['openedAt']: t for t in runs['baseline'][1]['trades']}
    after = {t['openedAt']: t for t in runs['guard'][1]['trades']}
    for at in before.keys() & after.keys():
        for key in ('side', 'entryPrice', 'stopLossPrice', 'takeProfitPrice', 'closedAt', 'exitReason'):
            assert before[at][key] == after[at][key], (at, key)
    rows = [json.loads(line) for line in (shadow / 'reversal-shadow.ndjson').open()]
    assert len(rows) == len({json.dumps(row, sort_keys=True) for row in rows}), 'Duplicate shadow events'
    paths = {}
    last = None
    for row in rows:
        o = row['Observation']
        at, sweep = datetime.fromisoformat(o['At']), datetime.fromisoformat(o['SweepAt'])
        assert last is None or at >= last
        last = at
        assert datetime.fromisoformat(o['SwingAt']) < sweep <= at
        assert datetime.fromisoformat(o['BreakSwingAt']) < sweep
        key = row['Instrument'], o['Side'], o['SweepAt']
        stages = paths.setdefault(key, {})
        assert o['Stage'] not in stages
        stages[o['Stage']] = at
        if o['Stage'] == 'Confirmed':
            assert stages['Sweep'] <= stages['Reclaimed'] < stages['StructureBreak'] < at
            assert 'Invalidated' not in stages and 'Expired' not in stages and 'RetestFailed' not in stages
        if o['Stage'] == 'Outcome':
            assert stages['Confirmed'] < at
    summary = {
        'inputHash': control['inputHash'],
        'processedBaseCandles': control['processedBaseCandles'],
        'runs': {name: {'simulationId': r['simulationId'], 'performance': s['performance']}
                 for name, (r, s, _) in runs.items()},
        'removedFills': [{k: before[at][k] for k in ('openedAt', 'side', 'netProfitLoss')}
                         for at in sorted(before.keys() - after.keys())],
        'addedFills': sorted(after.keys() - before.keys()),
        'distinctGuardCancellations': len(cancellations),
        'betweenEntryCloses': sum(datetime.fromisoformat(at).minute % 15 != 0 for at, _ in cancellations),
        'guardCancellations': cancellations,
        'shadowTradeAndCandidateParity': True,
        'shadowStages': dict(Counter(r['Observation']['Stage'] for r in rows)),
        'shadowOutcomes': dict(Counter(r['Observation']['Result'] for r in rows if r['Observation']['Stage'] == 'Outcome')),
        'shadowOutcomesBySide': {side: dict(Counter(r['Observation']['Result'] for r in rows
                                    if r['Observation']['Stage'] == 'Outcome' and r['Observation']['Side'] == side))
                                 for side in ('Buy', 'Sell')},
        'february5Morning': [r for r in rows if '2026-02-05T04:00' <= r['Observation']['At'] <= '2026-02-05T08:00'],
    }
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('guard', type=Path)
    parser.add_argument('shadow', type=Path)
    args = parser.parse_args()
    report(args.baseline, args.guard, args.shadow)
