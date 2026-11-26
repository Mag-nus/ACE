#!/bin/bash
# For every source-level fix variant: the long random walk (identical-results check against base) and a subset of scenarios.
# One process at a time. Output: results/variants.txt
cd "$(dirname "$0")"
SCN=${SCN:-"a a-edge b-diag d e f-near-20 f-slide-20 m"}
VARS=${VARS:-"base pg1 pc7 pc8 pc11 q2 pg2 pc3 pc2 items1to5 pool all base"}
out=results/variants.txt
: > $out
mkdir -p tmp
for seed in 1 2; do
  dotnet out/base/PhysE2E.dll walk --n=200000 --seed=$seed --dump=tmp/walk-base-s$seed.bin | grep -v "^#" >> $out
done
for v in $VARS; do
  echo "=== variant $v" >> $out
  if [ "$v" != base ]; then
    for seed in 1 2; do
      dotnet out/$v/PhysE2E.dll walk --n=200000 --seed=$seed --compare=tmp/walk-base-s$seed.bin | grep -v "^#" >> $out
    done
  fi
  for s in $SCN; do
    dotnet out/$v/PhysE2E.dll measure $s | grep "per update_object" | sed "s/^/$(printf '%-12s' $s)/" >> $out
  done
done
cat $out
