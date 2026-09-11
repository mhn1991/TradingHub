import csv, statistics
HDR=None
def rows(path):
    global HDR
    lines=open(path,encoding='utf-8-sig').read().splitlines()
    hdr=next(csv.reader([lines[0]])); HDR=hdr; n=len(hdr)
    ai=hdr.index('accomplished')
    for ln in lines[1:]:
        r=next(csv.reader([ln]))
        while len(r)>n:                       # flags enum split on its comma
            r[ai]=r[ai]+','+r[ai+1]; del r[ai+1]
        if len(r)!=n: continue
        yield dict(zip(hdr,r))
if __name__=='__main__':
    T=['gbpjpy','nas100','us30','gold','silver','eurusd']
    for label,pat in (('baseline','cand-%s.csv'),('min-stop-atr 1.0','scand-1p0-%s.csv')):
        sa=[];ct=[];ap=[];bad=0
        for t in T:
            try: rs=list(rows(pat%t))
            except Exception: continue
            for r in rs:
                if r['outcome']!='Entered': continue
                if r.get('stopAtrMultiple'): sa.append(float(r['stopAtrMultiple']))
                if r.get('costToRisk'): ct.append(float(r['costToRisk']))
                if r.get('atrPercentile'):
                    v=float(r['atrPercentile']); ap.append(v); bad += not (0<=v<=1)
        q=lambda v,p: sorted(v)[min(len(v)-1,int(p*len(v)))]
        print(f"--- {label} (entered candidates) ---")
        if sa: print(f"  stopAtrMultiple  n={len(sa):4d}  min {min(sa):.2f}  median {statistics.median(sa):.2f}  p90 {q(sa,.9):.2f}")
        if ct: print(f"  costToRisk       n={len(ct):4d}  median {statistics.median(ct):.2%}  p90 {q(ct,.9):.2%}")
        if ap: print(f"  atrPercentile    n={len(ap):4d}  median {statistics.median(ap):.2f}  out-of-range {bad}")
        print()
