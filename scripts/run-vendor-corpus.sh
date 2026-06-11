#!/usr/bin/env bash
# =============================================================================
# run-vendor-corpus.sh - compile every PowerBASIC 3.5 vendor example
# (tools/pb35/EXAMPLE/*.BAS) with OUR compiler. The examples that $LINK
# "PB35.PBL" need the library rebuilt with our own unit format first (the
# genuine .PBL is not binary-compatible - see docs/FORMATS.md, REQUIREMENTS W2),
# exactly like the vendor's BLDPBL.BAT does with the genuine toolchain.
#
# Known-unsupportable examples (excluded from the pass count expectations):
#   ASCIITSR.BAS - POPUP/TSR machinery (resident interrupt-driven popups)
#   BALL.BAS, EGABALL.BAS - CGA/EGA raster graphics (CIRCLE/GET/PUT images)
# =============================================================================
set -uo pipefail
cd "$(dirname "$0")/.."

echo "building compiler ..."
dotnet build pbc -c Release -v q --nologo
PBC="dotnet run --project pbc -c Release --no-build --"

LIB=build/pb35lib
mkdir -p "$LIB"

echo "rebuilding PB35.PBL from the unit sources ..."
units=(COMMUNIT DATEUNIT DIRUNIT DOSUNIT MATHUNIT MOUSUNIT SCRNUNIT)
for u in "${units[@]}"; do
  $PBC "tools/pb35/EXAMPLE/$u.BAS" -O "$LIB/$u.PBU" > /dev/null || { echo "::error::unit $u failed"; exit 1; }
done
$PBC lib build "$LIB/PB35.PBL" "${units[@]/#/$LIB/}" > /dev/null 2>&1 \
  || $PBC lib build "$LIB/PB35.PBL" $(printf "$LIB/%s.PBU " "${units[@]}") > /dev/null

ok=0
total=0
fails=()
for f in tools/pb35/EXAMPLE/*.BAS; do
  total=$((total + 1))
  if $PBC "$f" -L "$LIB" -O build/corpus.exe > /dev/null 2>&1; then
    ok=$((ok + 1))
  else
    fails+=("$(basename "$f")")
  fi
done

echo "vendor corpus: $ok/$total compiled"
[ ${#fails[@]} -gt 0 ] && printf '  FAIL %s\n' "${fails[@]}"
[ "$ok" -ge 35 ] || { echo "::error::corpus gate (>= 35/40) missed"; exit 1; }
exit 0
