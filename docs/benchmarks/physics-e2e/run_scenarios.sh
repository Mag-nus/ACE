#!/bin/bash
# usage: run_scenarios.sh <variant> [scenario ...]   -> results/measure-<variant>.txt (one process per scenario, sequential)
cd "$(dirname "$0")"
v=$1; shift
scn=("$@")
[ ${#scn[@]} -eq 0 ] && mapfile -t scn < <(dotnet out/$v/PhysE2E.dll list)
out=results/measure-$v.txt
: > $out
for s in "${scn[@]}"; do
  dotnet out/$v/PhysE2E.dll measure $s --classes 2>&1 | grep -v "^#" >> $out
done
cat $out
