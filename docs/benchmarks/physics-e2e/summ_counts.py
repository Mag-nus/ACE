#!/usr/bin/env python3
"""Compact view of results/count-<variant>.txt: one column per scenario (average calls per update_object)."""
import sys, re, collections
def load(path):
    data = collections.OrderedDict()
    for blk in open(path).read().split('\n\n'):
        lines = [l for l in blk.split('\n') if l.strip()]
        if not lines: continue
        name = lines[0].split(':')[0].strip()
        rows = collections.OrderedDict()
        hdr = lines[1].split()
        cols = lines[1][62:].split()
        for l in lines[2:]:
            key = l[3:61].strip(); vals = l[61:].split()
            rows[key] = dict(zip(cols, vals))
        data[name] = rows
    return data
variants = sys.argv[1].split(',')
scn = sys.argv[2].split(',') if len(sys.argv) > 2 else None
col = sys.argv[3] if len(sys.argv) > 3 else 'all'
D = {v: load(f'results/count-{v}.txt') for v in variants}
names = scn or list(D[variants[0]].keys())
keys = []
for v in variants:
    for n in names:
        for k in D[v].get(n, {}):
            if k not in keys: keys.append(k)
print(f"{'calls per update_object (column: ' + col + ')':<58}" + ''.join(f"{(n + ('/' + v if len(variants) > 1 else ''))[:13]:>14}" for n in names for v in variants))
for k in keys:
    if k.startswith('calls per'): continue
    print(f"{k:<58}" + ''.join(f"{D[v].get(n, {}).get(k, {}).get(col, '-'):>14}" for n in names for v in variants))
