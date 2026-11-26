#!/bin/bash
# usage: run_counts.sh <variant> [scenario ...]   -> results/count-<variant>.txt
# DOTNET_JitNoInline=1: Harmony prefixes are bypassed wherever the JIT inlined the patched method, so inlining is switched off
# for counting runs (and only for counting runs).
cd "$(dirname "$0")"
v=$1; shift
scn=("$@")
[ ${#scn[@]} -eq 0 ] && scn=(a a-edge b b-diag c c-diag d d-hills e f-near-20 f-slide-20 m m-big a-fast a-anim b-pop-50)
out=results/count-$v.txt
: > $out
for s in "${scn[@]}"; do
  DOTNET_JitNoInline=1 dotnet out/$v/PhysE2E.dll count $s 2>&1 | grep -v "^#" >> $out
  echo >> $out
done
