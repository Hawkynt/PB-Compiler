#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$repo/build/vga-hardware"
mkdir -p "$out"

for cpu in 8086 80286 80386; do
  case "$cpu" in 8086) prefix=86;; 80286) prefix=286;; 80386) prefix=386;; esac
  file="$out/V${prefix}.BAS"
  sed "s/^\$CPU 8086$/\$CPU $cpu/" "$repo/tests/benchmarks/SCREEN12.BAS" > "$file"
  for variant in OPT REF; do
    args=(--dialect pb36)
    if [[ "$variant" == OPT ]]; then args+=(--optimize); else args+=(--no-optimize); fi
    exe="$out/V${prefix}${variant}.EXE"
    dotnet "$repo/pbc/bin/Release/net8.0/pbc.dll" "${args[@]}" "$file" -O "$exe"
    test -s "$exe"
    echo "$cpu $variant: $exe ($(wc -c < "$exe") bytes)"
  done
done
