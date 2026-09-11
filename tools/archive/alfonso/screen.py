import csv, statistics, math, datetime, bisect
T=['gold','silver','nas100','us30','eurusd','gbpjpy']
def load(tag,tf):
    return [(datetime.datetime.fromisoformat(r['openTime']),float(r['open']),float(r['high']),
             float(r['low']),float(r['close']))
            for r in csv.DictReader(open(f'long-{tag}-{tf}.csv',encoding='utf-8-sig'))]
def atr(b,p=14):
    out=[0.0]*len(b); run=0.0
    for i in range(len(b)):
        pc=b[i-1][4] if i else b[0][1]
        tr=max(b[i][2]-b[i][3], abs(b[i][2]-pc), abs(b[i][3]-pc))
        run = tr if i==0 else ((run*(p-1))+tr)/p
        out[i]=run
    return out
def race(b,i,d,unit,stop_mult=1.0,target_mult=3.0,horizon=200):
    """+3 units vs -1 unit, stop checked first. 1=win, 0=loss, None=timeout."""
    if unit<=0: return None
    e=b[i][4]; stop=e-d*stop_mult*unit; tgt=e+d*target_mult*unit
    for j in range(i+1,min(i+horizon,len(b))):
        hi,lo=b[j][2],b[j][3]
        if (d>0 and lo<=stop) or (d<0 and hi>=stop): return 0
        if (d>0 and hi>=tgt) or (d<0 and lo<=tgt): return 1
    return None
def report(name, rows):
    """rows: list of (instrument, conditional_hits, conditional_n, base_hits, base_n)"""
    print(f"\n=== {name} ===")
    print(f"{'inst':8s} {'signals':>8s} {'hit%':>7s} {'base%':>7s} {'edge':>7s} {'EV/trade':>9s}")
    edges=[]; tc=th=bc=bh=0
    for inst,ch,cn,bh_,bn in rows:
        if cn<50: print(f"{inst:8s} {cn:8d}  too few"); continue
        c=ch/cn; base=bh_/bn; edges.append(c-base)
        tc+=cn; th+=ch; bc+=bn; bh+=bh_
        print(f"{inst:8s} {cn:8d} {c:7.2%} {base:7.2%} {c-base:+7.2%} {4*c-1:+9.3f}R")
    if edges:
        m=statistics.mean(edges); pos=sum(1 for e in edges if e>0)
        print(f"{'POOLED':8s} {tc:8d} {th/tc:7.2%} {bh/bc:7.2%} {th/tc-bh/bc:+7.2%} {4*th/tc-1:+9.3f}R")
        print(f"  mean edge {m:+.2%}   positive on {pos}/{len(edges)} instruments   (break-even hit rate 25%)")
