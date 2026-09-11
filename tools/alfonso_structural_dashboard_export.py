#!/usr/bin/env python3
"""Export the six completed structural-stop replays without replacing older tests."""
import argparse
import json
import re
from pathlib import Path

from alfonso_dashboard_export import historical_context, read, trade_context


def recorded_decision(trade):
    # Only expose trend labels explicitly present in the saved explanation.
    match = re.search(r'All three timeframes (Uptrend|Downtrend)\.', trade['setupReason'])
    trend = match[1] if match else 'Not separately recorded'
    return {'topTrend': trend, 'middleTrend': trend, 'lowerTrend': trend,
            'entryTimeframe': '15m', 'detailsAvailable': 'False'}


def export(root, output, cache):
    runs, history, input_hash = [], None, None
    for policy in ('zone', 'structural'):
        for reward in ('3', '0.75', '1'):
            tag = f'{policy}-{reward}r'
            source = root / tag
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
            trades = read(source / 'improved-progressive.json')['trades']
            if len(trades) != strategy['performance']['tradeCount']:
                raise ValueError(f'{tag}: trade count mismatch')
            records = []
            for trade in trades:
                context = trade_context(history, trade)
                records.append({'trade': trade, 'decision': recorded_decision(trade),
                                'candles': context.pop('1m'), 'timeframes': context})
            label = f'{"Original zone" if policy == "zone" else "Structural 15m"} · {reward}R'
            runs.append({'id': tag, 'arm': tag, 'label': label,
                         'instrument': manifest['instrument'], 'generatedAt': manifest['generatedAt'],
                         'from': manifest['from'], 'to': manifest['to'],
                         'simulationId': result['simulationId'], 'performance': strategy['performance'],
                         'records': records})
    payload = {'schemaVersion': 1, 'title': 'Silver · structural stops and targets',
               'generatedAt': max(run['generatedAt'] for run in runs), 'runs': runs}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, separators=(',', ':')))
    print(f'Exported {len(runs)} runs and {sum(len(r["records"]) for r in runs)} trades to {output}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1] /
                        'Dashboard/public/data/backtests/alfonso-structural-tests.json')
    parser.add_argument('--cache', type=Path, default=Path(__file__).resolve().parents[1] /
                        '.cache/historical')
    args = parser.parse_args()
    export(args.root, args.out, args.cache)
