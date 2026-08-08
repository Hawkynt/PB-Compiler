#!/usr/bin/env bash
# =============================================================================
# stage-pds.sh - stage the DOS tools of MS BASIC PDS 7.x into tools/<dialect>/
# =============================================================================
# THE LIBRARIES MATTER AS MUCH AS THE EXECUTABLES. The staging this replaces
# expanded the whole toolchain with a broken expander, and a corrupt .LIB announces
# itself no more than a corrupt .EXE does: every file had exactly the right length,
# the compiler ran fine, and LINK reported "invalid object module" from one library
# out of ninety. So the whole tree is re-expanded from the media, not just the tools.
#
#   scripts/stage-pds.sh pds71 ~/Downloads/pds71.7z        # an archive
#   scripts/stage-pds.sh pds71 ~/Downloads/pds71/          # a directory, or
#                                                          # loose floppy images
#
# Accepts an archive, a directory of extracted files, or a directory of floppy
# images (.img/.ima/.dsk, read with mtools). Members still MS-compressed in the
# old SZDD "SZ " variant (.EX_/.EX$) are expanded on the way in.
#
# WHICH BUILD IS RIGHT IS DECIDED BY THE FILE HEADER, never by the directory it came
# from - and an NE signature does not settle it either, because the PDS tools are
# BOUND executables holding both builds in one file. See kind() in lib/pds_layout.py.
set -euo pipefail
cd "$(dirname "$0")/.."

dialect="${1:-}"
source_path="${2:-}"
case "$dialect" in
  pds70|pds71) ;;
  *) echo "usage: scripts/stage-pds.sh <pds70|pds71> <archive-or-directory>"; exit 2;;
esac
[ -e "$source_path" ] || { echo "::error::no such source: $source_path"; exit 2; }

command -v mcopy >/dev/null 2>&1 || echo "::warning::mtools not found - floppy images will be skipped"

work="build/stage-$dialect"
rm -rf "$work"; mkdir -p "$work/harvest"

# --- 1. get everything into one flat directory ------------------------------
if [ -f "$source_path" ]; then
  echo "extracting $(basename "$source_path") ..."
  7z x -y -o"$work/unpacked" "$source_path" >/dev/null || { echo "::error::could not extract $source_path"; exit 1; }
  scan_root="$work/unpacked"
else
  scan_root="$source_path"
fi

# The three tools, plus every library and object the runtime is built from.
wanted='^(BC|LINK|LIB)\.(EXE|EX_|EX\$)$|\.(LIB|LI_|LI\$|OBJ|OB_|OB\$)$'

harvest_from_dir() { # $1 = directory tree of already-extracted files
  local f base
  while IFS= read -r f; do
    base=$(basename "$f" | tr '[:lower:]' '[:upper:]')
    printf '%s' "$base" | grep -qE "$wanted" || continue
    # Keep the containing directory in the name: two builds of one tool must not
    # collide, and BIN vs BINB is exactly the distinction worth seeing in the log.
    cp -f "$f" "$work/harvest/$(basename "$(dirname "$f")" | tr '[:lower:]' '[:upper:]')_$base" 2>/dev/null || true
  done < <(find "$1" -type f 2>/dev/null)
}

harvest_from_image() { # $1 = floppy image
  # The whole image is copied out and then harvested as an ordinary directory.
  # Parsing `mdir` output would mean handling its 8.3 columns, its per-directory
  # headers and its localisation; recursive mcopy has none of that surface.
  local img="$1" out
  out="$work/images/$(basename "$img" | tr '[:upper:] ' '[:lower:]_')"
  mkdir -p "$out"
  MTOOLS_SKIP_CHECK=1 mcopy -s -n -i "$img" "::/*" "$out" >/dev/null 2>&1 || true
  harvest_from_dir "$out"
}

echo "scanning $scan_root ..."
harvest_from_dir "$scan_root"
if command -v mcopy >/dev/null 2>&1; then
  while IFS= read -r img; do
    echo "  reading image $(basename "$img")"
    harvest_from_image "$img"
  done < <(find "$scan_root" -type f \( -iname "*.img" -o -iname "*.ima" -o -iname "*.dsk" -o -iname "*.vfd" \) 2>/dev/null)
fi

count=$(find "$work/harvest" -type f | wc -l)
[ "$count" -gt 0 ] || { echo "::error::found no toolchain files under $scan_root"; exit 1; }
echo "harvested $count candidate file(s)"

# --- 2. expand anything still SZDD-compressed -------------------------------
python3 scripts/expand-szdd.py "$work/harvest" "$work/expanded" >/dev/null
echo "expanded where needed"

# --- 3. classify, then lay out BC7/BIN and BC7/LIB --------------------------
target="tools/$dialect/BC7"
rm -rf "$target"; mkdir -p "$target/BIN" "$target/LIB"
python3 scripts/lib/pds_layout.py "$work/expanded" "$target"

for tool in BC LINK LIB; do
  [ -f "$target/BIN/$tool.EXE" ] || { echo "::error::no DOS build of $tool.EXE found under $scan_root"; exit 1; }
done

echo "staged into $target"
echo
echo "next:  PB_TOOLCHAIN_KEY=... bash scripts/pack-toolchains.sh $dialect"
echo "then:  bash scripts/run-diff-tests.sh"
