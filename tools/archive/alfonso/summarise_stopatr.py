import json, csv, os, statistics
T=['gbpjpy','nas100','us30','gold','silver','eurusd']
def base(t): return 'c2-gold' if t=='gold' else f'c-{t}'
def load(d):
    p=f'{d}/simulation-result.json'
    if not os.path.exists(p): return None
    s=json.load(open(p))['strategies'][0]
    return s['performance'], s['trades']
def block(label, dirs, cands):
    rows=[]; allr=[]; wr=[]; pos=0; have=0
    for t in T:
        r=load(dirs(t))
        if not r: rows.append((t,None)); continue
        perf,tr=r; have+=1; pos += perf['averageR']>0
        allr+=[x['rMultiple'] for x in tr if x.get('rMultiple') is not None]
        wr+=[x['rMultiple'] for x in tr if x['rMultiple']>0]
        rows.append((t,perf))
    if not allr: return None
    n=len(allr); m=statistics.mean(allr)
    se=statistics.stdev(allr)/ (n**0.5) if n>1 else 0
    sa=[]
    for t in T:
        f=cands(t)
        if f and os.path.exists(f) and os.path.getsize(f)>100:
            sa+=[float(r['stopAtrMultiple']) for r in csv.DictReader(open(f,encoding='utf-8-sig'))
                 if r.get('stopAtrMultiple')]
    return dict(label=label, rows=rows, n=n, avg=m, lo=m-1.96*se, hi=m+1.96*se,
                winmean=statistics.mean(wr) if wr else float('nan'),
                nwin=len(wr), pos=pos, have=have,
                stopatr=statistics.median(sa) if sa else float('nan'))
blocks=[block('0 (baseline)', base, lambda t: f'cand-{t}.csv')]
for X,tag in (('0.5','0p5'),('1.0','1p0'),('1.5','1p5')):
    blocks.append(block(X, lambda t,tag=tag: f'sa-{tag}-{t}', lambda t,tag=tag: f'scand-{tag}-{t}.csv'))
print(f"{'min-stop-atr':>13s} {'trades':>7s} {'avgR':>9s} {'95% CI':>22s} {'win mean R':>11s} {'pos':>5s} {'median stopATR':>15s}")
print('-'*90)
for b in blocks:
    if b is None: continue
    ratio = f"{b['pos']}/{b['have']}"
    print(f"{b['label']:>13s} {b['n']:7d} {b['avg']:+9.4f} [{b['lo']:+7.4f},{b['hi']:+7.4f}] "
          f"{b['winmean']:+11.3f} {ratio:>5s} {b['stopatr']:15.2f}")
print()
for b in blocks:
    if b is None: continue
    print(f"--- {b['label']} ---")
    for t,perf in b['rows']:
        print(f"   {t:8s} " + (f"n={perf['tradeCount']:3d} avgR={perf['averageR']:+.4f} win={perf['winRatePercent']:5.1f}%" if perf else "(pending)"))
