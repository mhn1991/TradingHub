#!/usr/bin/env python3
"""Publish a completed lower-alignment replay to the dashboard's latest-tests archive."""
import argparse
import csv
import fcntl
import json
import os
import tempfile
from pathlib import Path

from alfonso_dashboard_export import PERIODS, historical_context, read, stamp, trade_context
from alfonso_opposing_target_export import validate_trade
from alfonso_trend_trade_export import match_decision


def build_run(root, cache, label=None):
    source = root / 'lower-aligned'
    result, manifest = read(source / 'simulation-result.json'), read(source / 'manifest.json')
    strategy = next(s for s in result['strategies'] if s['strategyId'] == 'alfonso')
    simulation = source / 'simulations' / result['simulationId'].replace('-', '')
    if not strategy['isComplete'] or not (simulation / 'COMPLETE').exists():
        raise ValueError('Replay is incomplete; nothing published')
    simulation_manifest = read(simulation / 'manifest.json')
    assert simulation_manifest['simulationId'] == result['simulationId']
    assert simulation_manifest['inputHash'] == result['inputHash']
    assert all(manifest[k] == simulation_manifest[k] for k in ('instrument', 'from', 'to'))
    decisions = {}
    with (root / 'lower-aligned.csv').open(encoding='utf-8-sig', newline='') as handle:
        for row in csv.DictReader(handle):
            direction = 'Uptrend' if row['side'] == 'Demand' else 'Downtrend'
            assert row['lowerTrend'] == row['confirmationTrend'] == direction
            assert row['entryTimeframe'] == 'Lower'
            assert stamp(row['confirmationClosedAt']) == stamp(row['at'])
            assert stamp(row['at']) % 900 == 0
            if row['outcome'] == 'Entered':
                decisions.setdefault(stamp(row['at']), []).append(row)
    trades = read(source / 'improved-progressive.json')['trades']
    perf = strategy['performance']
    assert len(trades) == perf['tradeCount']
    assert sum(t['netProfitLoss'] > 0 for t in trades) == perf['winningTrades']
    assert abs(sum(t['netProfitLoss'] for t in trades) - perf['netProfit']) < 1e-6
    history = historical_context(cache, simulation_manifest, periods={**PERIODS, '5m': 300})
    records = []
    for trade in trades:
        decision = match_decision(trade, decisions)
        validate_trade(trade, decision)
        context = trade_context(history, trade)
        assert any(stamp(c['availableAt']) == stamp(trade['signalCreatedAt']) for c in context['5m'])
        records.append({'trade': trade, 'decision': decision,
                        'candles': context.pop('1m'), 'timeframes': context})
    run = {k: manifest[k] for k in ('instrument', 'from', 'to', 'generatedAt')}
    # Unique arms preserve repeated experiments rather than replacing their earlier results.
    run.update(id=result['simulationId'], arm=result['simulationId'],
               label=label or root.resolve().name, simulationId=result['simulationId'],
               inputHash=result['inputHash'], performance=perf, records=records)
    return run


def publish(run, output):
    """Atomic, idempotent upsert; a lock prevents simultaneous test processes losing runs."""
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.with_suffix('.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        payload = read(output) if output.exists() else {'schemaVersion': 1, 'runs': []}
        if payload.get('schemaVersion') != 1 or not isinstance(payload.get('runs'), list):
            raise ValueError('Existing dashboard archive is invalid; refusing to overwrite it')
        runs = [r for r in payload['runs'] if r['simulationId'] != run['simulationId']] + [run]
        runs.sort(key=lambda r: (stamp(r['generatedAt']), r['simulationId']), reverse=True)
        payload.update(title='Alfonso · latest completed tests', generatedAt=runs[0]['generatedAt'], runs=runs)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode='w', dir=output.parent, suffix='.tmp', delete=False) as handle:
                temporary = Path(handle.name)
                json.dump(payload, handle, separators=(',', ':'), allow_nan=False)
                handle.flush()
                os.fsync(handle.fileno())
            temporary.replace(output)
        finally:
            if temporary is not None and temporary.exists():
                temporary.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    base = Path(__file__).resolve().parents[1]
    parser.add_argument('root', type=Path)
    parser.add_argument('--label', help='Display label; defaults to the experiment directory name')
    parser.add_argument('--out', type=Path, default=base / 'Dashboard/public/data/backtests/alfonso-latest-tests.json')
    parser.add_argument('--cache', type=Path, default=base / '.cache/historical')
    args = parser.parse_args()
    run = build_run(args.root, args.cache, args.label)
    publish(run, args.out)
    print(f'Published {run["label"]}: {len(run["records"])} trades, run {run["simulationId"]}')
    print('Dashboard: /#alfonso-tests → Latest completed tests (refresh an already-open page)')
