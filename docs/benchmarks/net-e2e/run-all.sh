#!/bin/bash
# Rebuilds the harness and runs every mode, one process at a time. Output goes to ./out/<mode>.txt
# AceBin defaults to Source/ACE.Server/bin/Release/net10.0; override with: AceBin=/path/to/bin ./run-all.sh
set -e
cd "$(dirname "$0")"
mkdir -p out
dotnet build -c Release ${AceBin:+-p:AceBin=$AceBin} 2>&1 | tail -3
D="dotnet bin/Release/net10.0/NetE2E.dll"
$D behave                     | tee out/behave.txt        # task 4a, 4b            (~2 s)
$D tick                       | tee out/tick.txt          # task 2a                (~65 s)
$D systemic secs=5            | tee out/systemic.txt      # task 5                 (~35 s)
$D sendcost                   | tee out/sendcost.txt      # sendto by socket/loop  (~20 s)
$D flush n=20000              | tee out/flush.txt         # task 2b                (~55 s)
$D flush n=20000 log=none     | tee out/flush-lognone.txt # 2b without a log provider
$D retained                   | tee out/retained.txt      # task 2c                (~4 s)
$D broadcast                  | tee out/broadcast.txt     # task 2d                (~45 s)
$D inbound                    | tee out/inbound.txt       # task 2e                (~10 s)
$D e2e                        | tee out/e2e.txt           # 2e through the real receive loop (~10 s)
$D ack                        | tee out/ack.txt           # task 2f                (~40 s)
$D types                      | tee out/types.txt         # task 3, AllocationSampled (~15 s)
$D types sampler=tick         | tee out/types-tick.txt    # task 3, GCAllocationTick  (~15 s)
