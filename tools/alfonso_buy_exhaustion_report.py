#!/usr/bin/env python3
"""Compare a completed buy-exhaustion replay with the existing pending-guard control."""
import argparse
import csv
import json
from collections import Counter
from datetime import datetime, timedelta
from decimal import Decimal
from pathlib import Path

from alfonso_reversal_experiments_report import run


def candidates(root):
    with (root / 'lower-aligned.csv').open(encoding='utf-8-sig') as source:
        return list(csv.DictReader(source))


def exhaustion_events(simulation):
    events = {}
    with (simulation / 'strategies/alfonso/journal.ndjson').open() as source:
        for line in source:
            row = json.loads(line)
            message = row.get('message') or ''
            if not message.startswith('5m buy exhaustion:'):
                continue
            # Journal mirrors a decision across event types; count each action only once.
            key = row['timestamp'], row['action'], message
            fields = dict(pair.strip().split('=', 1) for pair in message.removeprefix(
                '5m buy exhaustion: ').removesuffix('.').split(';'))
            at = datetime.fromisoformat(row['timestamp'])
            candle_age = at - datetime.fromisoformat(fields['candle'])
            assert candle_age in (timedelta(minutes=5), timedelta(minutes=10))
            assert at.minute % 5 == 0 and at.second == 0
            assert Decimal(fields['high']) >= Decimal(fields['BBUpper'])
            rsi = Decimal(fields['RSI']) if fields['RSI'] else None
            cci = Decimal(fields['CCI']) if fields['CCI'] else None
            assert (rsi is not None and rsi > 70) or (cci is not None and cci >= 100)
            assert row['action'] in ('Observe', 'Cancel')
            events[key] = {'at': row['timestamp'], 'action': row['action'],
                           'matchedCandle': 'current' if candle_age == timedelta(minutes=5) else 'previous', **fields}
    return list(events.values())


def report(control_root, filtered_root):
    control, before, control_simulation = run(control_root)
    filtered, after, simulation = run(filtered_root)
    assert control['inputHash'] == filtered['inputHash']
    assert control['processedBaseCandles'] == filtered['processedBaseCandles'] == 253035
    control_manifest = json.loads((control_simulation / 'manifest.json').read_text())
    filtered_manifest = json.loads((simulation / 'manifest.json').read_text())
    for key in ('from', 'to', 'warmupFrom', 'baseInterval', 'analysisIntervals', 'fillModel',
                'spreadBasisPoints', 'slippageBasisPoints', 'commissionRate', 'positionSizing'):
        assert control_manifest[key] == filtered_manifest[key], key
    annotation = filtered_manifest['runtimeOptions']['annotationOptions']
    assert annotation == control_manifest['runtimeOptions']['annotationOptions']
    for key, expected in [('rsiPeriod', 14), ('bollingerPeriod', 20),
                          ('bollingerStandardDeviations', 2), ('cciPeriod', 20)]:
        assert annotation[key] == expected, key
    old = {t['openedAt']: t for t in before['trades']}
    new = {t['openedAt']: t for t in after['trades']}
    retained = []
    for at in sorted(old.keys() & new.keys()):
        unchanged = all(old[at][k] == new[at][k] for k in (
            'side', 'entryPrice', 'stopLossPrice', 'takeProfitPrice', 'closedAt', 'exitReason'))
        retained.append({'openedAt': at, 'sameGeometryAndExit': unchanged,
                         'controlNet': old[at]['netProfitLoss'], 'filteredNet': new[at]['netProfitLoss']})
    events = exhaustion_events(simulation)
    rows = candidates(filtered_root)
    blocked = [r for r in rows if r['outcome'] == 'BuyExhaustion']
    observe_times = {datetime.fromisoformat(e['at']) for e in events if e['action'] == 'Observe'}
    assert len(blocked) == len(observe_times)
    assert all(r['side'] == 'Demand' and datetime.fromisoformat(r['at']) in observe_times for r in blocked)
    fields = ('openedAt', 'side', 'netProfitLoss', 'exitReason')
    return {
        'inputHash': control['inputHash'], 'processedBaseCandles': filtered['processedBaseCandles'],
        'control': {'simulationId': control['simulationId'], 'performance': before['performance']},
        'filtered': {'simulationId': filtered['simulationId'], 'performance': after['performance']},
        'netChange': after['performance']['netProfit'] - before['performance']['netProfit'],
        'exactTradeParity': before['trades'] == after['trades'],
        'removedFills': [{k: old[at][k] for k in fields} for at in sorted(old.keys() - new.keys())],
        'addedFills': [{k: new[at][k] for k in fields} for at in sorted(new.keys() - old.keys())],
        'retainedFills': retained,
        'controlCandidates': dict(Counter(r['outcome'] for r in candidates(control_root))),
        'filteredCandidates': dict(Counter(r['outcome'] for r in rows)),
        'exhaustionActions': dict(Counter(e['action'] for e in events)),
        'exhaustionEvents': events,
    }


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('control', type=Path)
    parser.add_argument('filtered', type=Path)
    args = parser.parse_args()
    print(json.dumps(report(args.control, args.filtered), indent=2))
