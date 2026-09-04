import json, math, csv, os
T=['gbpjpy','nas100','us30','gold','silver','eurusd']
def perf(d):
    return json.load(open(f'{d}/simulation-result.json'))['strategies'][0]['performance']
def rs(d):
    p=perf(d); out=[]
    for t in p.get('trades',[]):
        r=t.get('rMultiple', t.get('realisedR', t.get('r')))
        if r is not None: out.append(float(r))
    return out
def stats(v):
    n=len(v)
    if n<2: return (n, sum(v)/n if n else 0.0, 0.0, 0.0)
    m=sum(v)/n; sd=math.sqrt(sum((x-m)**2 for x in v)/(n-1)); se=sd/math.sqrt(n)
    return (n, m, m-1.96*se, m+1.96*se)
rows=[]
print(f"{'instrument':10s} {'prior':>8s} | {'margin OFF':>22s} | {'margin 3:1':>22s} | delta")
print('-'*92)
tot={'off':[], 'on':[], 'prior':[]}
for t in T:
    line=f"{t:10s}"
    try: pr=perf(f'f-{t}-base'); line+=f" {pr['averageR']:+8.4f}"; tot['prior']+=rs(f'f-{t}-base')
    except Exception: line+=f" {'--':>8s}"; pr=None
    cells={}
    for tag,d in (('off',f'q-{t}-off'),('on',f'q-{t}-on')):
        try:
            p=perf(d); v=rs(d); tot[tag]+=v
            cells[tag]=p['averageR']
            line+=f" | n={p['tradeCount']:3d} {p['averageR']:+7.4f} w={p['winRatePercent']:4.1f}%"
        except Exception:
            cells[tag]=None; line+=f" | {'(pending)':>22s}"
    if cells.get('off') is not None and cells.get('on') is not None:
        line+=f" | {cells['on']-cells['off']:+7.4f}"
    print(line)
print('-'*92)
for tag,lab in (('prior','prior base'),('off','margin OFF'),('on','margin 3:1')):
    n,m,lo,hi=stats(tot[tag])
    if n: print(f"{lab:12s} pooled n={n:4d}  avgR={m:+.4f}  95% CI [{lo:+.4f}, {hi:+.4f}]")
pos_off=sum(1 for t in T if os.path.exists(f'q-{t}-off/simulation-result.json') and perf(f'q-{t}-off')['averageR']>0)
pos_on =sum(1 for t in T if os.path.exists(f'q-{t}-on/simulation-result.json')  and perf(f'q-{t}-on')['averageR']>0)
print(f"\npositive instruments:  margin OFF {pos_off}/6   margin 3:1 {pos_on}/6   (pre-registered bar: 5/6)")
