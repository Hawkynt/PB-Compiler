#!/usr/bin/env bash
# =============================================================================
# stage-pds.sh - stage the DOS tools of MS BASIC PDS 7.x into tools/<dialect>/
# =============================================================================
# The pds70 and pds71 slots ship everything EXCEPT a usable compiler: their
# BC.EXE, LINK.EXE and LIB.EXE are the OS/2 builds, because both were staged from
# a PDS BINB\ directory (protected mode) rather than BIN\ (DOS). The runtime
# libraries are already the right ones - LIB\ carries the real-mode set beside the
# protected-mode set - so replacing those three executables is the whole job.
#
#   scripts/stage-pds.sh pds71 ~/Downloads/pds71.7z        # an archive
#   scripts/stage-pds.sh pds71 ~/Downloads/pds71/          # a directory, or
#                                                          # loose floppy images
#
# Accepts an archive, a directory of extracted files, or a directory of floppy
# images (.img/.ima/.dsk, read with mtools). Members still MS-compressed in the
# old SZDD "SZ " variant (.EX_/.EX$) are expanded on the way in.
#
# WHICH COPY IS THE RIGHT ONE IS DECIDED BY THE FILE HEADER, never by the
# directory it came from. A PDS disk set holds both builds under names that differ
# by one letter, the wrong one is a valid executable that DOS will happily start
# and then die inside, and reading BINB as BIN is precisely how this slot came to
# be broken in the first place. So every candidate is classified and only a plain
# DOS MZ or a BOUND image is accepted - PDS ships its tools bound, one file holding
# both builds, and the DOS half is the whole compiler - while an image that can only
# run under OS/2 is reported and skipped.
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

# The three tools, under any of the spellings a PDS disk set uses for them.
wanted='^(BC|LINK|LIB)\.(EXE|EX_|EX\$)$'

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
[ "$count" -gt 0 ] || { echo "::error::found no BC/LINK/LIB candidates under $scan_root"; exit 1; }
echo "harvested $count candidate file(s)"

# --- 2. expand anything still SZDD-compressed -------------------------------
python3 scripts/expand-szdd.py "$work/harvest" "$work/expanded" >/dev/null
echo "expanded where needed"

# --- 3. classify and pick the DOS build -------------------------------------
mkdir -p "$work/chosen"
python3 - "$work/expanded" "$work/chosen" <<'PY'
import os, struct, shutil, sys
src, dst = sys.argv[1], sys.argv[2]

def kind(path):
    """DOS-runnable or not, decided by the DOS stub rather than the NE signature.

    An NE header does NOT mean "cannot run under DOS". The PDS 7.x tools are BOUND
    executables: one file holding both builds, where the MZ part is the entire DOS
    program and the NE part is the OS/2 one. BC.EXE 7.10 carries a 13.6 KB stub with
    the compiler's banner in it and runs under DOS perfectly well. A genuinely
    OS/2-only image instead has a stub of a few hundred bytes whose whole job is to
    print a complaint, so the stub's SIZE is what separates them.

    Searching the file for that complaint does not work either - the correctly
    expanded BC.EXE contains the string too, in its OS/2 half.
    """
    with open(path, "rb") as fh:
        d = fh.read()
    if d[:8] == b"SZ \x88\xf0'3\xd1":
        return "still-compressed", d
    if d[:2] != b"MZ" or len(d) < 0x40:
        return "not-an-executable", d
    lfanew = struct.unpack("<I", d[0x3c:0x40])[0]
    if not (0 < lfanew < len(d) - 2):
        return "DOS", d
    sig = d[lfanew:lfanew + 2]
    if sig not in (b"NE", b"PE", b"LE", b"LX"):
        return "DOS", d
    pages, last = struct.unpack("<H", d[4:6])[0], struct.unpack("<H", d[2:4])[0]
    stub = (pages - 1) * 512 + last if pages else 0
    if stub >= 4096:
        return "DOS", d                       # bound: the stub is a real program
    return ("OS/2" if sig == b"NE" and d[lfanew + 54] == 1 else sig.decode()), d

picked, rejected = {}, []
for name in sorted(os.listdir(src)):
    path = os.path.join(src, name)
    if not os.path.isfile(path):
        continue
    tool = next((t for t in ("BC", "LINK", "LIB") if name.upper().rstrip().endswith(t + ".EXE")), None)
    if tool is None:
        continue
    verdict, data = kind(path)
    if verdict == "DOS":
        # Prefer the largest DOS build: PDS ships a small loader stub of the same
        # name on some disks, and the compiler proper is the big one.
        if tool not in picked or len(data) > picked[tool][1]:
            picked[tool] = (path, len(data))
    else:
        rejected.append((name, verdict))

for tool, (path, size) in sorted(picked.items()):
    shutil.copyfile(path, os.path.join(dst, tool + ".EXE"))
    print(f"  {tool + '.EXE':10s} {size:>8} bytes  DOS  <- {os.path.basename(path)}")
for name, verdict in rejected:
    print(f"  skipped {name} ({verdict})")
missing = [t for t in ("BC", "LINK", "LIB") if t not in picked]
if missing:
    print("MISSING:" + ",".join(missing))
PY

for tool in BC LINK LIB; do
  [ -f "$work/chosen/$tool.EXE" ] || { echo "::error::no DOS build of $tool.EXE found - PDS keeps the DOS tools in BIN\\, the OS/2 ones in BINB\\"; exit 1; }
done

# --- 4. install ---------------------------------------------------------------
target="tools/$dialect/BC7/BIN"
mkdir -p "$target"
for tool in BC LINK LIB; do
  cp -f "$work/chosen/$tool.EXE" "$target/$tool.EXE"
done
echo "staged into $target"
echo
echo "next:  PB_TOOLCHAIN_KEY=... bash scripts/pack-toolchains.sh $dialect"
echo "then:  bash scripts/diff-one.sh tests/diff/$dialect/DIFF01.BAS"
