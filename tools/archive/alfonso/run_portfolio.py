import os
import sys, statistics, datetime
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
from alfonso_portfolio import *

print('building entries...', file=sys.stderr)
bars, all_entries = {}, []
for tag in TAGS:
    e, h4 = entries(tag)
    bars[tag] = h4
    all_entries += e
all_entries.sort(key=lambda x: x[0])
print(f'{len(all_entries)} raw signals across {len(TAGS)} instruments', file=sys.stderr)


def summarise(label, taken, skc, ski):
    rows = [(block_of(t), tag, r, outcome, gapped) for (t, tag, d, r, outcome, gapped, g) in taken
            if block_of(t)]
    if not rows:
        print(f'{label}: no trades'); return None
    print(f'\n=== {label} ===')
    print(f'  taken {len(rows)}   skipped: cap {skc}, instrument-busy {ski}')
    print(f"  {'block':8s} {'n':>5s} {'hit%':>7s} {'netR/trade':>11s} {'totalR':>9s}")
    means, pos = [], 0
    for name, a, b in BLOCKS:
        sub = [r for (bl, tag, r, o, gp) in rows if bl == name]
        wins = [1 for (bl, tag, r, o, gp) in rows if bl == name and o == 'target']
        if not sub: continue
        m = statistics.mean(sub); means.append(m); pos += (m > 0)
        print(f'  {name:8s} {len(sub):5d} {len(wins)/len(sub):7.2%} {m:+11.4f} {sum(sub):+9.1f}')
    allr = [r for (bl, tag, r, o, gp) in rows]
    print(f"  {'ALL':8s} {len(allr):5d} {sum(1 for x in rows if x[3]=='target')/len(allr):7.2%} "
          f'{statistics.mean(allr):+11.4f} {sum(allr):+9.1f}')
    print(f'  blocks positive {pos}/{len(means)}   mean of block means {statistics.mean(means):+.4f}')
    byinst = {}
    for (bl, tag, r, o, gp) in rows: byinst.setdefault(tag, []).append(r)
    ip = sum(1 for t in byinst if statistics.mean(byinst[t]) > 0)
    print('  instruments positive %d/%d  ' % (ip, len(byinst)) +
          '  '.join(f'{t}{statistics.mean(v):+.3f}' for t, v in byinst.items()))
    return statistics.mean(allr)


print('\n' + '#' * 78)
print('# PART 1 - reproduce 3.46 (no concurrency limit, touch fills)')
print('#' * 78)
t, sc, si = simulate(all_entries, bars, cap=None, gap_aware=False, slip=SLIP_MEDIAN,
                     one_per_instrument=False)
base = summarise('3.46 baseline: unlimited, touch fills, median slippage', t, sc, si)

print('\n' + '#' * 78)
print('# PART 2 - CONCURRENCY: cap simultaneous positions, first-come-first-served')
print('#' * 78)
for cap in [1, 2, 3, 6, None]:
    t, sc, si = simulate(all_entries, bars, cap=cap, gap_aware=False, slip=SLIP_MEDIAN)
    summarise(f'cap={cap if cap else "unlimited"} (max 1 per instrument), touch fills', t, sc, si)

print('\n' + '#' * 78)
print('# PART 3 - GAP RISK: fill at the bar open when price gaps past the level')
print('#' * 78)
for cap in [3, None]:
    t, sc, si = simulate(all_entries, bars, cap=cap, gap_aware=True, slip=SLIP_MEDIAN)
    summarise(f'cap={cap if cap else "unlimited"}, GAP-AWARE fills', t, sc, si)
    gp = [x for x in t if x[5]]
    st = [x for x in t if x[4] == 'stop']
    stg = [x for x in st if x[5]]
    print(f'  gapped fills: {len(gp)}/{len(t)} = {len(gp)/len(t):.2%} of trades')
    if stg:
        print(f'  stop exits {len(st)}, of which gapped {len(stg)} ({len(stg)/len(st):.2%}); '
              f'mean gross R on gapped stops {statistics.mean([x[6] for x in stg]):+.3f} '
              f'vs -1.000 assumed')
