import csv, glob, os, collections
T=['gbpjpy','nas100','us30','gold','silver','eurusd']

rows=collections.defaultdict(list)
for t in T:
    p=f'/mnt/storage/scratch/alfonso/cand-{t}.csv'
    if not os.path.exists(p) or os.path.getsize(p)<100: continue
    with open(p, encoding='utf-8-sig') as f:
        for r in csv.DictReader(f): rows[t].append(r)

if not rows:
    print("no candidate rows yet"); raise SystemExit

# 1. the skew, decomposed by outcome
print("=== 1. candidates by side and outcome (does the agent even SEE more supply?) ===")
outcomes=sorted({r['outcome'] for t in rows for r in rows[t]})
print(f"{'inst':8s} {'side':7s} " + ' '.join(f"{o[:11]:>12s}" for o in outcomes) + f" {'total':>7s}")
tot=collections.Counter()
for t in T:
    if t not in rows: continue
    for side in ('Demand','Supply'):
        c=collections.Counter(r['outcome'] for r in rows[t] if r['side']==side)
        n=sum(c.values())
        for o in outcomes: tot[(side,o)]+=c[o]
        print(f"{t:8s} {side:7s} " + ' '.join(f"{c[o]:12d}" for o in outcomes) + f" {n:7d}")
print('-'*(16+13*len(outcomes)+8))
for side in ('Demand','Supply'):
    n=sum(tot[(side,o)] for o in outcomes)
    print(f"{'POOLED':8s} {side:7s} " + ' '.join(f"{tot[(side,o)]:12d}" for o in outcomes) + f" {n:7d}")

ed=tot[('Demand','Entered')]; es=tot[('Supply','Entered')]
cd=sum(tot[('Demand',o)] for o in outcomes); cs=sum(tot[('Supply',o)] for o in outcomes)
print(f"\nEntered  supply:demand = {es}:{ed} = {es/ed if ed else float('nan'):.2f}   (trade skew observed: 2.63)")
print(f"Seen     supply:demand = {cs}:{cd} = {cs/cd if cd else float('nan'):.2f}")
print(f"Fill rate  demand {ed/cd if cd else 0:.1%}   supply {es/cs if cs else 0:.1%}")

# 2. trend state at decision time, from the real pipeline
print("\n=== 2. trend state at decision time (agent's own states) ===")
for role in ('topTrend','middleTrend','lowerTrend'):
    c=collections.Counter(r[role] for t in rows for r in rows[t] if r[role])
    n=sum(c.values())
    print(f"  {role:12s} " + '  '.join(f"{k}={v/n:5.1%}" for k,v in c.most_common()))

# 3. what states actually produced entries
print("\n=== 3. joint state on ENTERED candidates ===")
c=collections.Counter(
    f"{r['topTrend']}/{r['middleTrend']}/{r['lowerTrend']}"
    for t in rows for r in rows[t] if r['outcome']=='Entered')
n=sum(c.values())
for k,v in c.most_common(8): print(f"  {k:48s} {v:5d}  {v/n:6.1%}")
allthree=sum(v for k,v in c.items() if len(set(k.split('/')))==1 and 'trend' in k.lower())
print(f"  full three-timeframe agreement: {allthree}/{n} = {allthree/n if n else 0:.1%}")

# 4. entered by side and joint state
print("\n=== 4. entered by side ===")
for side in ('Demand','Supply'):
    c=collections.Counter(
        f"{r['topTrend']}/{r['middleTrend']}/{r['lowerTrend']}"
        for t in rows for r in rows[t] if r['outcome']=='Entered' and r['side']==side)
    n=sum(c.values())
    print(f"  {side} (n={n}):")
    for k,v in c.most_common(4): print(f"    {k:46s} {v:5d}  {v/n if n else 0:6.1%}")
