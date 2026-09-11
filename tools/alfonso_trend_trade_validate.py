#!/usr/bin/env python3
"""Check full-replay parity, outcomes and causal structural-stop geometry."""
import argparse
import json
import re
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path


def read(path):
    return json.loads(path.read_text(), parse_float=Decimal)


def validate(root, controls):
    baseline = read(controls / 'structural-1r/simulation-result.json')
    for variant in ('baseline', 'price-break', 'confirmed', 'both'):
        for reward in ('0.75', '1'):
            tag = f'{variant}-{reward}r'
            folder = controls / f'structural-{reward}r' if variant == 'baseline' else root / tag
            result = read(folder / 'simulation-result.json')
            strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
            simulation = folder / 'simulations' / result['simulationId'].replace('-', '')
            assert strategy['isComplete'] and (simulation / 'COMPLETE').exists(), tag
            assert result['inputHash'] == baseline['inputHash'], tag
            assert result['processedBaseCandles'] == baseline['processedBaseCandles'], tag
            trades = read(folder / 'improved-progressive.json')['trades']
            perf = strategy['performance']
            assert len(trades) == perf['tradeCount'], tag
            assert abs(sum(t['netProfitLoss'] for t in trades) - perf['netProfit']) < Decimal('0.000001'), tag
            assert sum(t['netProfitLoss'] > 0 for t in trades) == perf['winningTrades'], tag
            for trade in trades:
                risk = abs(trade['signalPrice'] - trade['stopLossPrice'])
                distance = abs(trade['takeProfitPrice'] - trade['signalPrice'])
                assert abs(distance - risk * Decimal(reward)) < Decimal('0.000000001'), (tag, trade['setupId'])
                assert trade['stopAmendmentCount'] == 0, tag
                assert 'structural 15m' in trade['stopSource'], (tag, trade['stopSource'])
                assert f'target {reward}:1' in trade['stopSource'], tag
                match = re.search(r'at (\d{4}-\d\d-\d\d \d\d:\d\d) UTC', trade['stopSource'])
                if match:
                    anchor = datetime.strptime(match[1], '%Y-%m-%d %H:%M').replace(tzinfo=timezone.utc)
                    assert anchor + timedelta(minutes=45) <= datetime.fromisoformat(trade['signalCreatedAt']), tag
            disputed = [t for t in trades if t['side'] == 'Sell' and
                        t['signalCreatedAt'].startswith('2025-12-23T15:45:00')]
            print(tag, {k: str(perf[k]) for k in ('tradeCount', 'winningTrades', 'netProfit',
                  'profitFactor', 'maximumDrawdown', 'averageR')}, f'Dec23 disputed shorts={len(disputed)}')
    print(f'PASS: 8 completed runs; same {baseline["processedBaseCandles"]} candles and input hash; outcomes and brackets verified.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('controls', type=Path)
    args = parser.parse_args()
    validate(args.root, args.controls)
