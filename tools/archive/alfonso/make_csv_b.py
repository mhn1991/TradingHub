import gzip, json, glob, csv, re, sys
from datetime import datetime

tag, label, out = sys.argv[1], sys.argv[2], sys.argv[3]
zone_rx = re.compile(r"Zone ([\d.]+)/([\d.]+) \((\w+), ([\d.]+):1, ([^)]+)\)(?:, nested at ([\d.]+))?")
rows = []
for cfg, d in (("baseline", f"b-{tag}-base"), ("control-gate", f"b-{tag}-ctrl")):
    dirs = glob.glob(f"/mnt/storage/scratch/alfonso/{d}/simulations/*/strategies/*")
    if not dirs: continue
    for p in sorted(glob.glob(dirs[0] + "/events-*.json.gz")):
        try:
            with gzip.open(p, 'rt') as f: evts = json.load(f)
        except Exception: continue
        for e in evts:
            t = e.get('completedTrade')
            if not t or t.get('rMultiple') is None: continue
            reason = t.get('setupReason') or ''
            m = zone_rx.search(reason)
            prox, dist, strength, ratio, acc, host = m.groups() if m else (None,)*6
            opened, closed = t.get('openedAt'), t.get('closedAt')
            hours = round((datetime.fromisoformat(closed)-datetime.fromisoformat(opened)).total_seconds()/3600, 2) if opened and closed else None
            rows.append({
                "instrument": label, "run": cfg,
                "openedAt": opened, "closedAt": closed, "holdHours": hours,
                "session": t.get('entrySession'), "side": t.get('side'),
                "entryPrice": t.get('entryPrice'), "exitPrice": t.get('exitPrice'),
                "initialStop": t.get('initialStopLossPrice'), "takeProfit": t.get('takeProfitPrice'),
                "rMultiple": t.get('rMultiple'), "netProfitLoss": t.get('netProfitLoss'),
                "grossProfitLoss": t.get('grossProfitLoss'), "commission": t.get('commission'),
                "exitReason": t.get('exitReason'),
                "mfeR": t.get('maximumFavourableExcursionR'), "maeR": t.get('maximumAdverseExcursionR'),
                "quantity": t.get('quantity'), "initialRiskCash": t.get('initialRiskCash'),
                "zoneProximal": prox, "zoneDistal": dist,
                "zoneWidth": (round(abs(float(prox)-float(dist)),5) if prox else None),
                "impulseStrength": strength, "impulseToBaseRatio": ratio,
                "accomplishment": acc.strip() if acc else None,
                "nested": bool(host), "hostProximal": host,
                "scenario": reason.split('.')[0] if reason else '',
                "entryConfidence": t.get('entryConfidence'), "setupReason": reason,
            })
if not rows:
    print(f"{label}: NO TRADES"); sys.exit(0)
rows.sort(key=lambda r: (r["run"], r["openedAt"] or ""))
with open(out, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0].keys())); w.writeheader(); w.writerows(rows)
for cfg in ("baseline", "control-gate"):
    sub=[r for r in rows if r["run"]==cfg]
    if not sub: continue
    wins=[r for r in sub if r["rMultiple"]>0]
    print(f"  {label:<10} {cfg:<13} n={len(sub):>4} win {len(wins)/len(sub)*100:>5.1f}% netR {sum(r['rMultiple'] for r in sub):>+8.2f}")
