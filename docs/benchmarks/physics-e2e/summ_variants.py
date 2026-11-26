#!/usr/bin/env python3
"""Table from results/variants*.txt: bytes per update_object and best time, per scenario and variant, with deltas against base."""
import re, sys, collections
files = sys.argv[1:] or ['results/variants.txt']
B = collections.defaultdict(dict); T = collections.defaultdict(lambda: collections.defaultdict(list)); H = collections.defaultdict(dict)
walk = collections.defaultdict(list)
order = []
for f in files:
    for line in open(f):
        m = re.match(r'(\S+)\s+\[(\S+)\] per update_object.*?:\s+([\d.]+) B .*?time best\s+(\d+) ns\s+median\s+(\d+) ns.*hash20k=(\w+)', line)
        if m:
            s, v, b, tb, tm, h = m.groups()
            if v not in order: order.append(v)
            B[s][v] = float(b); T[s][v].append(int(tb)); H[s][v] = h
            continue
        m = re.match(r'walk seed=(\d+) n=(\d+) \[(\S+)\].*hash=(\w+)', line)
        if m: cur = (m.group(3), m.group(1), m.group(2), m.group(4)); continue
        m = re.search(r'compared with .*mismatches (\d+).*different shadow-cell set (\d+)', line)
        if m: walk[cur[0]].append(f"seed {cur[1]} n={cur[2]}: {m.group(1)} mismatches, {m.group(2)} shadow-set diffs")
scn = list(B.keys())
print("bytes per update_object (exact):")
print(f"{'variant':<11}" + ''.join(f"{s:>20}" for s in scn))
for v in order:
    print(f"{v:<11}" + ''.join(f"{B[s].get(v, float('nan')):>10.1f} ({B[s].get(v, 0) - B[s]['base']:+7.1f})" for s in scn))
print("\nbest time per update_object, ns (min over runs; ratio to base in brackets; base was run first and last):")
print(f"{'variant':<11}" + ''.join(f"{s:>20}" for s in scn))
for v in order:
    row = ''
    for s in scn:
        t = min(T[s][v]) if T[s][v] else float('nan'); tb = min(T[s]['base'])
        row += f"{t:>12.0f} ({t / tb:5.3f})"
    print(f"{v:<11}" + row)
print("\nbase runs (ns):", {s: T[s]['base'] for s in scn})
print("\nidentical-results checks:")
for v in order:
    same = all(H[s].get(v) == H[s]['base'] for s in scn)
    print(f"  {v:<10} hash of first 20k updates equal to base in all {len(scn)} scenarios: {same};  random walk vs base: {'; '.join(walk.get(v, ['(reference)']))}")
