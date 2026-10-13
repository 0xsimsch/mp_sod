"""Summarize sodprobe.csv: tick rate and culling state vs. distance to the player.

usage: python -I analyze.py sodprobe.csv [first_sample] [last_sample]
"""
import csv
import statistics as st
import sys
from collections import defaultdict

RATES = ["veryHigh", "high", "medium", "low", "veryLow"]
BUCKETS = [(0, 10), (10, 25), (25, 50), (50, 100), (100, 200), (200, 1e9)]


def load(path, lo, hi):
    rows = []
    with open(path, encoding="utf-8-sig") as f:
        for r in csv.DictReader(f):
            s = int(r["sample"])
            if r["humanId"] == "-1" or r["paused"] == "True" or not (lo <= s <= hi):
                continue
            r["dist"] = float(r["dist"])
            rows.append(r)
    return rows


def pct(n, d):
    return f"{100 * n / d:5.1f}%" if d else "   - "


def main():
    path = sys.argv[1]
    lo = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    hi = int(sys.argv[3]) if len(sys.argv) > 3 else 10**9
    rows = load(path, lo, hi)
    samples = sorted({int(r["sample"]) for r in rows})
    print(f"{len(rows)} citizen-rows over {len(samples)} unpaused samples ({samples[:1]}..{samples[-1:]})\n")

    print("== A1: tick rate vs distance to player")
    by_rate = defaultdict(list)
    for r in rows:
        by_rate[r["tickRate"]].append(r)
    print(f"{'rate':10} {'rows':>6} {'dist min':>9} {'median':>8} {'max':>8} {'sameBldg':>9} {'visible':>8} {'asleep':>7}")
    for rate in RATES + [k for k in by_rate if k not in RATES]:
        g = by_rate.get(rate)
        if not g:
            continue
        d = [r["dist"] for r in g]
        print(f"{rate:10} {len(g):6} {min(d):9.1f} {st.median(d):8.1f} {max(d):8.1f} "
              f"{pct(sum(r['sameBuilding'] == 'True' for r in g), len(g)):>9} "
              f"{pct(sum(r['visible'] == 'True' for r in g), len(g)):>8} "
              f"{pct(sum(r['isAsleep'] == 'True' for r in g), len(g)):>7}")

    print("\nrate share per distance bucket:")
    print(f"{'dist':>12} {'rows':>6} " + " ".join(f"{r:>9}" for r in RATES))
    for a, b in BUCKETS:
        g = [r for r in rows if a <= r["dist"] < b]
        if not g:
            continue
        label = f"{a}-{int(b) if b < 1e9 else 'inf'}"
        print(f"{label:>12} {len(g):6} " + " ".join(f"{pct(sum(r['tickRate'] == k for r in g), len(g)):>9}" for k in RATES))

    print("\n== A1b: how often does each citizen's AI actually update? (aiLastUpdated changes between samples)")
    per_cit = defaultdict(list)
    for r in rows:
        per_cit[r["humanId"]].append(r)
    changed = defaultdict(lambda: [0, 0])
    for cid, rs in per_cit.items():
        rs.sort(key=lambda r: int(r["sample"]))
        for prev, cur in zip(rs, rs[1:]):
            if int(cur["sample"]) != int(prev["sample"]) + 1:
                continue
            k = cur["tickRate"]
            changed[k][1] += 1
            if cur["aiLastUpdated"] != prev["aiLastUpdated"]:
                changed[k][0] += 1
    for rate in RATES:
        n, d = changed.get(rate, [0, 0])
        if d:
            print(f"  {rate:10} updated between consecutive samples: {pct(n, d)} of {d}")

    print("\n== A2: culling state vs distance")
    cols = ["visible", "roomVisible", "roomGeomLoaded", "goActive", "animatorOn"]
    print(f"{'dist':>12} {'rows':>6} " + " ".join(f"{c:>14}" for c in cols) + f" {'rend on/tot':>12} {'coll on/tot':>12}")
    for a, b in BUCKETS:
        g = [r for r in rows if a <= r["dist"] < b]
        if not g:
            continue
        label = f"{a}-{int(b) if b < 1e9 else 'inf'}"
        ron = sum(int(r["renderersOn"]) for r in g); rtot = sum(int(r["renderersTotal"]) for r in g)
        con = sum(int(r["collidersOn"]) for r in g); ctot = sum(int(r["collidersTotal"]) for r in g)
        print(f"{label:>12} {len(g):6} " + " ".join(f"{pct(sum(r[c] == 'True' for r in g), len(g)):>14}" for c in cols)
              + f" {pct(ron, rtot):>12} {pct(con, ctot):>12}")


if __name__ == "__main__":
    main()
