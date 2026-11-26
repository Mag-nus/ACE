#!/bin/bash
# Builds the harness once against the current-source ACE.Server (acebin-head, made by make_variants.py) and stamps out one
# run directory per source-level variant (only ACE.Server.dll differs), plus one against the older binary snapshot.
set -e
cd "$(dirname "$0")"
SNAP=${SNAP:-$(pwd)/../../../Source/ACE.Server/bin/Release/net10.0}
[ -d acebin-head ] || python3 make_variants.py
rm -rf out
dotnet build PhysE2E.csproj -c Release -o out/base -v q -nologo 2>&1 | grep -E "error|Warn|Elapsed" || true
for d in variants/*/; do
  v=$(basename "$d")
  [ "$v" = base ] && continue
  cp -r out/base "out/$v"
  cp "variants/$v/ACE.Server.dll" "variants/$v/ACE.Server.pdb" "out/$v/"
done
if [ -d "$SNAP" ]; then
  dotnet build PhysE2E.csproj -c Release -p:AceBin="$SNAP" -p:UseAsp=true -o out/snapshot -v q -nologo 2>&1 | grep -E "error|Warn|Elapsed" || true
fi
ls out
