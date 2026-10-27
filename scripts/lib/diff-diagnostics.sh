# Preserve text evidence before the next differential case reuses its directory.
# Never copy oracle executables, objects, libraries, or arbitrary guest files.
diff_capture_failure() { # $1 = capture directory, $2 = guest directory, $3 = config file
  local capture="$1" guest="$2" conf="$3" file
  mkdir -p "$capture"
  for file in T.BAS RT.BAS RESULT.TXT DONE.TXT PBCOUT.TXT BCLOG.TXT LINKLOG.TXT \
      pbcout.txt rtemit.txt rtcomp.txt; do
    if [ -f "$guest/$file" ] && [ ! -L "$guest/$file" ]; then
      cp "$guest/$file" "$capture/$file"
    fi
  done
  if [ -f "$conf" ]; then
    cp "$conf" "$capture/DOSBOX.CONF"
  fi
  if [ -f "${conf}.log" ]; then
    cp "${conf}.log" "$capture/DOSBOX.LOG"
  fi
  # Names and sizes distinguish an IDE stall from a generated program stall
  # without publishing proprietary toolchain bytes.
  (cd "$guest" && LC_ALL=C find . -maxdepth 1 -type f -printf '%f\t%s bytes\n' | LC_ALL=C sort) \
    > "$capture/FILES.TXT"
  echo "      failure evidence: $capture"
  sed 's/^/      /' "$capture/FILES.TXT"
  if [ -f "$capture/DOSBOX.LOG" ]; then
    tail -n 30 "$capture/DOSBOX.LOG" | sed 's/^/      /'
  fi
}
