#!/usr/bin/env bash
# Regenerates docs/images/pbc-help.svg from `pbc --help`.
#
# The README shows the compiler's own help as its opening picture, so the picture has to come FROM
# the compiler rather than from a screenshot someone took once: a switch added or renamed would
# otherwise leave the front page describing a CLI that no longer exists. Run this after touching
# Driver.cs's option list, the same way gen-decompilation.sh is run after a pb36 feature lands.
#
# SVG rather than PNG on purpose - it stays sharp at any zoom, weighs a few KB, and a diff of it is
# readable, so a regeneration shows what actually changed in the help text.
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="docs/images/pbc-help.svg"
mkdir -p "$(dirname "$OUT")"

HELP="$(mktemp)"
trap 'rm -f "$HELP"' EXIT
# Build first and capture only the program's own output. `dotnet run` writes MSBuild's progress to
# the same stream, and a 300-column restore line silently became the widest "help" line here once.
DOTNET_ROLL_FORWARD=Major dotnet build pbc -c Release >/dev/null
DOTNET_ROLL_FORWARD=Major dotnet run --project pbc -c Release --no-build -- --help > "$HELP" 2>/dev/null

# Layout. The advance width is the one number that has to be generous rather than exact: the viewer
# picks its own monospace face, and a frame sized to the narrowest one clips the widest.
FONT=13
CHARW=7.85
LINEH=19
PADX=22
PADY=16
TITLEH=34

COLS="$(awk '{ if (length($0) > m) m = length($0) } END { print m }' "$HELP")"
ROWS="$(wc -l < "$HELP")"
# +1 row for the prompt line drawn above the output
W="$(awk -v c="$COLS" -v w="$CHARW" -v p="$PADX" 'BEGIN { printf "%.0f", c * w + 2 * p }')"
H="$(awk -v r="$ROWS" -v l="$LINEH" -v p="$PADY" -v t="$TITLEH" 'BEGIN { printf "%.0f", t + 2 * p + (r + 2) * l }')"

esc() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

{
  cat <<SVG
<svg xmlns="http://www.w3.org/2000/svg" width="$W" height="$H" viewBox="0 0 $W $H" role="img"
     xml:space="preserve" aria-label="pbc --help, the PB-Compiler command line">
  <title>pbc --help</title>
  <style>
    .bg   { fill: #0f111a; }
    .bar  { fill: #1b1e2b; }
    .t    { font-family: "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Menlo", "Consolas", monospace;
            font-size: ${FONT}px; fill: #c7ccd9; }
    .name { font-family: "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Menlo", "Consolas", monospace;
            font-size: ${FONT}px; fill: #8a90a6; }
    .hdr  { fill: #e8ecf5; font-weight: 600; }
    .sect { fill: #f0c674; }
    .flag { fill: #7fd1e8; }
    .ps1  { fill: #9ece6a; }
  </style>
  <rect class="bg" x="0" y="0" width="$W" height="$H" rx="9"/>
  <path class="bar" d="M0 9a9 9 0 0 1 9-9h$((W - 18))a9 9 0 0 1 9 9v$((TITLEH - 9))H0z"/>
  <circle cx="20" cy="17" r="5.5" fill="#ed6a5f"/>
  <circle cx="39" cy="17" r="5.5" fill="#f4bf50"/>
  <circle cx="58" cy="17" r="5.5" fill="#61c554"/>
  <text class="name" x="$((W / 2))" y="21" text-anchor="middle">pbc --help</text>
SVG

  y=$((TITLEH + PADY + LINEH))
  printf '  <text class="t" x="%s" y="%s"><tspan class="ps1">$</tspan> pbc --help</text>\n' "$PADX" "$y"

  line_no=0
  while IFS= read -r raw || [ -n "$raw" ]; do
    line_no=$((line_no + 1))
    y=$((y + LINEH))
    safe="$(printf '%s' "$raw" | esc)"
    if [ -z "$raw" ]; then
      continue
    elif [ "$line_no" = 1 ]; then
      printf '  <text class="t hdr" x="%s" y="%s">%s</text>\n' "$PADX" "$y" "$safe"
    elif printf '%s' "$raw" | grep -qE '^[A-Za-z][A-Za-z -]*:$'; then
      printf '  <text class="t sect" x="%s" y="%s">%s</text>\n' "$PADX" "$y" "$safe"
    elif printf '%s' "$raw" | grep -qE '^  (-{1,2}[A-Za-z0-9][A-Za-z0-9-]*)'; then
      # the switch itself in colour, its description in the body tone
      # a switch may be a comma-separated pair (-h, --help); colour the whole pair, not just the first
      flag="$(printf '%s' "$raw" | sed -E 's/^(  -{1,2}[A-Za-z0-9][A-Za-z0-9-]*(, *-{1,2}[A-Za-z0-9][A-Za-z0-9-]*)?).*/\1/' | esc)"
      rest="$(printf '%s' "$raw" | sed -E 's/^  -{1,2}[A-Za-z0-9][A-Za-z0-9-]*(, *-{1,2}[A-Za-z0-9][A-Za-z0-9-]*)?//' | esc)"
      printf '  <text class="t" x="%s" y="%s"><tspan class="flag">%s</tspan>%s</text>\n' \
        "$PADX" "$y" "$flag" "$rest"
    else
      printf '  <text class="t" x="%s" y="%s">%s</text>\n' "$PADX" "$y" "$safe"
    fi
  done < "$HELP"

  printf '</svg>\n'
} > "$OUT"

printf '%s: %s lines, %s cols -> %sx%s, %s bytes\n' \
  "$OUT" "$ROWS" "$COLS" "$W" "$H" "$(wc -c < "$OUT")"
