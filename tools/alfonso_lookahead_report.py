"""
PROJECT_STATE 3.47's report: the lookahead A/B, concurrency, gap risk and slippage sensitivity for
3.46's assembled rule. Run it to reproduce that section's tables.

  A. the same rule reading the still-forming vs the last-closed daily bar
  B. concurrency caps on the lookahead-free rule
  C. touch fills vs gap-aware fills
  D. mean instead of median stop slippage

Expected (3.47, and re-verified when this moved into the repo):
  doff=-1  n=4,995  hit 29.03%  +0.0326R  4/7 blocks positive
  doff=-2  n=4,330  hit 26.84%  -0.0571R  2/7 blocks positive
"""
import os, sys, statistics, math, collections

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from alfonso_portfolio import *

def build(doff):
    bars={}; ent=[]
    for tag in TAGS:
        e,h4=entries(tag,doff=doff); bars[tag]=h4; ent+=e
    ent.sort(key=lambda x:x[0]); return bars,ent

def stats(taken):
    rows=[(block_of(t),tag,r,o) for (t,tag,d,r,o,gp,g) in taken if block_of(t)]
    allr=[r for _,_,r,_ in rows]
    bm=[]; 
    for name,a,b in BLOCKS:
        sub=[r for bl,_,r,_ in rows if bl==name]
        if sub: bm.append((name,len(sub),statistics.mean(sub),
                           sum(1 for bl,_,_,o in rows if bl==name and o=='target')/len(sub)))
    byi={}
    for _,tag,r,_ in rows: byi.setdefault(tag,[]).append(r)
    n=len(allr); m=statistics.mean(allr); sd=statistics.pstdev(allr)
    se=sd/math.sqrt(n) if n else 0
    return dict(n=n,mean=m,ci=(m-1.96*se,m+1.96*se),blocks=bm,
                pos=sum(1 for _,_,x,_ in bm if x>0),
                inst={k:statistics.mean(v) for k,v in byi.items()},
                hit=sum(1 for _,_,_,o in rows if o=='target')/n)

def show(label,st):
    print(f'\n=== {label} ===')
    print(f"  {'block':8s} {'n':>5s} {'hit%':>7s} {'netR':>9s}")
    for name,n,m,h in st['blocks']: print(f'  {name:8s} {n:5d} {h:7.2%} {m:+9.4f}')
    print(f"  ALL      {st['n']:5d} {st['hit']:7.2%} {st['mean']:+9.4f}   95% CI [{st['ci'][0]:+.4f}, {st['ci'][1]:+.4f}]")
    print(f"  blocks positive {st['pos']}/{len(st['blocks'])}   instruments positive "
          f"{sum(1 for v in st['inst'].values() if v>0)}/6   "
          + ' '.join(f'{k}{v:+.3f}' for k,v in st['inst'].items()))

print('#'*76); print('# A. LOOKAHEAD: same rule, only which daily bar it reads'); print('#'*76)
res={}
for doff,name in [(-1,'reads the STILL-FORMING daily bar (lookahead, ~10h avg)'),
                  (-2,'reads the LAST CLOSED daily bar (lookahead-free)')]:
    bars,ent=build(doff)
    t,sc,si=simulate(ent,bars,cap=None,gap_aware=False,slip=SLIP_MEDIAN,one_per_instrument=False)
    res[doff]=stats(t); show(f'doff={doff}: {name}',res[doff])
print(f"\n  >> lookahead is worth {res[-1]['mean']-res[-2]['mean']:+.4f}R per trade "
      f"({res[-1]['pos']}/7 blocks positive vs {res[-2]['pos']}/7)")

print('\n'+'#'*76); print('# B. CONCURRENCY (lookahead-free rule, touch fills)'); print('#'*76)
bars,ent=build(-2)
for cap in [1,2,3,6,None]:
    t,sc,si=simulate(ent,bars,cap=cap,gap_aware=False,slip=SLIP_MEDIAN)
    st=stats(t); show(f'cap={cap or "unlimited"}  (max 1/instrument; {sc} dropped on cap, {si} on instrument-busy)',st)

print('\n'+'#'*76); print('# C. GAP RISK (lookahead-free rule)'); print('#'*76)
for cap in [3,None]:
    tt,sc,si=simulate(ent,bars,cap=cap,gap_aware=False,slip=SLIP_MEDIAN)
    tg,_,_  =simulate(ent,bars,cap=cap,gap_aware=True, slip=SLIP_MEDIAN)
    a,b=stats(tt),stats(tg)
    g=[x for x in tg if x[5]]; st=[x for x in tg if x[4]=='stop']; sg=[x for x in st if x[5]]
    print(f'\n=== cap={cap or "unlimited"} ===')
    print(f"  touch fills {a['mean']:+.4f}R   gap-aware fills {b['mean']:+.4f}R   "
          f"cost of gaps {b['mean']-a['mean']:+.4f}R/trade")
    print(f'  gapped fills {len(g)}/{len(tg)} = {len(g)/len(tg):.2%} of trades; '
          f'stops gapped {len(sg)}/{len(st)} = {len(sg)/len(st):.2%}')
    if sg: print(f'  mean realised R on a gapped stop {statistics.mean([x[6] for x in sg]):+.3f} (vs -1.000 assumed)')

print('\n'+'#'*76); print('# D. SENSITIVITY: mean (pessimistic) instead of median slippage'); print('#'*76)
for cap,lab in [(None,'unlimited'),(3,'cap=3')]:
    t,_,_=simulate(ent,bars,cap=cap,gap_aware=True,slip=SLIP_MEAN)
    st=stats(t); print(f"  {lab:10s} gap-aware + mean slippage: {st['mean']:+.4f}R  "
                       f"blocks positive {st['pos']}/7  instruments positive "
                       f"{sum(1 for v in st['inst'].values() if v>0)}/6")
