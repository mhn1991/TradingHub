import gzip, json, glob, csv
from datetime import datetime, timedelta
M="/mnt/storage/scratch/v2-p0b/rungA-long/simulations/0091770424ba45fabdaff99123943f34/market"
specs={"m15":15,"h1":60,"h4":240}
state={k:{"out":[],"cur":None,"key":None} for k in specs}
n=0
for p in sorted(glob.glob(M+"/chunk-*.json.gz")):
    try:
        with gzip.open(p,'rt') as f: data=json.load(f)
    except Exception: continue
    for r in data:
        if r.get("isWarmup"): continue
        n+=1
        dt=datetime.fromisoformat(r["openTime"]).replace(second=0,microsecond=0)
        o,h,l,c=float(r["open"]),float(r["high"]),float(r["low"]),float(r["close"])
        for name,mins in specs.items():
            s=state[name]
            k = dt-timedelta(minutes=dt.minute%mins) if mins<60 else dt.replace(minute=0)-timedelta(hours=dt.hour%(mins//60))
            if k!=s["key"]:
                if s["cur"]: s["out"].append(s["cur"])
                s["cur"]=[k.isoformat(),o,h,l,c]; s["key"]=k
            else:
                s["cur"][2]=max(s["cur"][2],h); s["cur"][3]=min(s["cur"][3],l); s["cur"][4]=c
for name in specs:
    s=state[name]
    if s["cur"]: s["out"].append(s["cur"])
    path=f"/mnt/storage/scratch/alfonso/full-xauusd-{name}.csv"
    with open(path,"w",newline="") as f:
        w=csv.writer(f); w.writerow(["openTime","open","high","low","close"]); w.writerows(s["out"])
    print(f"{name}: {len(s['out'])} bars -> {path}", flush=True)
print("1m rows", n, flush=True)
