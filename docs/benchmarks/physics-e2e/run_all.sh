#!/bin/bash
# Reproduces every measurement, one process at a time (about 25 minutes in total). Results land in results/.
#   0. python3 make_variants.py   builds ACE.Server.dll variants from a COPY of the repo source (srcbuild/), repo is only read
#   1. ./build.sh                 builds the harness and stamps out out/<variant>/
set -e
cd "$(dirname "$0")"
[ -d srcbuild/Source ] || { mkdir -p srcbuild; rsync -a --exclude bin --exclude obj --exclude TestResults --exclude '*.Tests' "${ACE_SRC:-../../../Source}/" srcbuild/Source/; cp "${ACE_SRC:-../../../Source}/../LICENSE" srcbuild/LICENSE; }
[ -f variants/all/ACE.Server.dll ] || python3 make_variants.py
[ -d out/base ] || ./build.sh
mkdir -p results tmp
./run_scenarios.sh base                 > /dev/null      # results/measure-base.txt : bytes / time per update_object, by update class
./run_counts.sh base; ./run_counts.sh pg1                # results/count-*.txt      : call counts (DOTNET_JitNoInline=1)
python3 summ_counts.py base,pg1 a,a-edge,b-diag,d,f-slide-20,m > results/count-summary.txt
for s in a f-near-20 f-slide-20; do dotnet out/base/PhysE2E.dll types $s --mb=1500 | grep -v "^#"; done > results/types.txt
./run_variants.sh                       > /dev/null      # results/variants.txt     : every fix variant + identical-results walks
python3 summ_variants.py                > results/variants-summary.txt
DOTNET_JitNoInline=1 dotnet out/base/PhysE2E.dll freq > results/freq.txt
: > results/harmony.txt
for mode in "" "--client"; do
  dotnet out/base/PhysE2E.dll harmony pristine $mode    >> results/harmony.txt
  dotnet out/pg1/PhysE2E.dll  harmony pristine $mode    >> results/harmony.txt
  dotnet out/base/PhysE2E.dll harmony noop-prefix $mode >> results/harmony.txt
  dotnet out/base/PhysE2E.dll harmony old $mode         >> results/harmony.txt
  dotnet out/base/PhysE2E.dll harmony transpiler $mode  >> results/harmony.txt
done
for v in base pg1 q2 pg2 pc3 pc2 pc2naive items1to5 pool all; do dotnet out/$v/PhysE2E.dll place | grep -v "^#"; done > results/place.txt
for v in base pg7 snapshot; do dotnet out/$v/PhysE2E.dll lbload | grep -v "^#"; done > results/lbload.txt
{ dotnet out/base/PhysE2E.dll walk --n=1000000 --seed=3 --dump=tmp/walk-base-s3.bin | grep -v "^#"
  for v in pg1 pg2 items1to5 pool all pc2naive; do dotnet out/$v/PhysE2E.dll walk --n=1000000 --seed=3 --compare=tmp/walk-base-s3.bin | grep -E "walk seed|compared"; done; } > results/walk-1M.txt
rm -f tmp/walk-base-s3.bin
