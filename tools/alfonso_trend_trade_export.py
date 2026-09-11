#!/usr/bin/env python3
"""Export completed trend-fix trading replays alongside structural-stop controls."""
import argparse
import csv
import json
from pathlib import Path

from alfonso_dashboard_export import historical_context, read, stamp, trade_context
from alfonso_structural_dashboard_export import recorded_decision


def match_decision(trade, decisions):
    matches = [row for row in decisions.get(stamp(trade['signalCreatedAt']), [])
               if row['side'] == ('Supply' if trade['side'] == 'Sell' else 'Demand')
               and abs(float(row['proximal']) - trade['entryPrice']) < 1e-7]
    if len(matches) != 1:
        raise ValueError(f'Ambiguous/missing decision for {trade["setupId"]}')
    return matches[0]


def export(root, controls, output, cache):
    runs, history, input_hash = [], None, None
    for variant, label in [('baseline', 'No trend fix'), ('price-break', 'Price-break invalidation'),
                           ('confirmed', 'Confirmed structure'), ('both', 'Both trend fixes')]:
        for reward in ('0.75', '1'):
            tag = f'{variant}-{reward}r'
            source = controls / f'structural-{reward}r' if variant == 'baseline' else root / tag
            manifest = read(source / 'manifest.json')
            result = read(source / 'simulation-result.json')
            strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
            simulation = source / 'simulations' / result['simulationId'].replace('-', '')
            if not strategy['isComplete'] or not (simulation / 'COMPLETE').exists():
                raise ValueError(f'{tag}: replay incomplete')
            if input_hash is None:
                input_hash = result['inputHash']
                history = historical_context(cache, read(simulation / 'manifest.json'))
            if result['inputHash'] != input_hash or manifest['instrument'] != 'METAL:XAG/USD':
                raise ValueError(f'{tag}: unexpected input')
            decisions = {}
            if variant != 'baseline':
                with (root / f'{tag}.csv').open(encoding='utf-8-sig', newline='') as handle:
                    for row in csv.DictReader(handle):
                        if row['outcome'] == 'Entered':
                            decisions.setdefault(stamp(row['at']), []).append(row)
            trades = read(source / 'improved-progressive.json')['trades']
            if len(trades) != strategy['performance']['tradeCount']:
                raise ValueError(f'{tag}: trade count mismatch')
            records = []
            for trade in trades:
                decision = recorded_decision(trade) if variant == 'baseline' else match_decision(trade, decisions)
                context = trade_context(history, trade)
                records.append({'trade': trade, 'decision': decision,
                                'candles': context.pop('1m'), 'timeframes': context})
            runs.append({'id': tag, 'arm': tag, 'label': f'{label} · {reward}R',
                         'instrument': manifest['instrument'], 'generatedAt': manifest['generatedAt'],
                         'from': manifest['from'], 'to': manifest['to'],
                         'simulationId': result['simulationId'], 'inputHash': input_hash,
                         'performance': strategy['performance'], 'records': records})
    payload = {'schemaVersion': 1, 'title': 'Silver · trend fixes with structural stops',
               'generatedAt': max(run['generatedAt'] for run in runs), 'runs': runs}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, separators=(',', ':')))
    print(f'Exported {len(runs)} runs and {sum(len(r["records"]) for r in runs)} trades to {output}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('controls', type=Path)
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1] /
                        'Dashboard/public/data/backtests/alfonso-trend-trades.json')
    parser.add_argument('--cache', type=Path, default=Path(__file__).resolve().parents[1] / '.cache/historical')
    args = parser.parse_args()
    export(args.root, args.controls, args.out, args.cache)
