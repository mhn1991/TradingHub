import os
import sys, statistics, bisect, collections
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
from alfonso_portfolio import *

def entries_v(tag, doff, trend_lookback=20, mult=2.0):
    h4,d1 = load(tag,'h4'), load(tag,'d1')
    ah4,ad1 = atr(h4), atr(d1)
    dopen=[b[0] for b in d1]; out=[]
    for i in range(1,len(h4)):
        t=h4[i][0]; di=bisect.bisect_left(dopen,t)+doff
        if di<trend_lookback or ad1[di]<=0 or ah4[i]<=0: continue
        move=d1[di][4]-d1[di-trend_lookback][4]; th=mult*ad1[di]
        if move>=th: d=1
        elif move<=-th: d=-1
        else: continue
        if d>0 and not h4[i][4]>h4[i-1][2]: continue
        if d<0 and not h4[i][4]<h4[i-1][3]: continue
        out.append((t,tag,d,i,h4[i][4],ah4[i]))
    return out,h4

for doff in (-1,-2):
    tot=0; oc=collections.Counter(); res_hits=0; res_n=0; allr=[]
    perblock=collections.defaultdict(lambda:[0,0,0,[]])  # n_res, hits, n_all, R
    for tag in TAGS:
        e,h4=entries_v(tag,doff)
        for (t,tg,d,i,p,u) in e:
            b=block_of(t)
            if not b: continue
            tot+=1
            xt,gross,outcome,gp = resolve(h4,i,d,p,u,False)
            oc[outcome]+=1
            pb=perblock[b]; pb[2]+=1
            r = net_r(tg,gross,outcome,SLIP_MEDIAN); pb[3].append(r); allr.append(r)
            if outcome!='timeout':
                pb[0]+=1; res_n+=1
                if outcome=='target': pb[1]+=1; res_hits+=1
    print(f'\n### daily offset {doff}: {tot} entries   outcomes {dict(oc)}')
    print(f'  hit% incl timeouts {oc["target"]/tot:.2%}   hit% resolved-only {res_hits/res_n:.2%}   timeouts {oc["timeout"]/tot:.2%}')
    print(f"  {'block':8s} {'n_all':>6s} {'n_res':>6s} {'hit%res':>8s} {'netR':>9s}")
    for name,a,b in BLOCKS:
        pb=perblock[name]
        if not pb[2]: continue
        print(f'  {name:8s} {pb[2]:6d} {pb[0]:6d} {pb[1]/pb[0]:8.2%} {statistics.mean(pb[3]):+9.4f}')
