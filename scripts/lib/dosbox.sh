# Locating a DOSBox and starting it in a way that works headless.
#
# Sourced by run-diff-tests.sh, run-dos-tests.sh and diff-one.sh. Sets:
#
#   DOSBOX          the emulator, or empty when none was found
#   DOSBOX_FLAVOR   its self-reported version line (the autotype gate reads this)
#   DOSBOX_PREFIX   a command prefix to launch it with, possibly empty
#
# Two things differ between vanilla DOSBox and dosbox-staging, and both of them
# fail SILENTLY - the emulator boots to a bare DOS prompt or dies before it, the
# harness sees no output file, and every test reports as though the oracle had
# refused the program:
#
#   * staging chdir's to its own install directory before reading -conf, so a
#     RELATIVE config path is simply not found. dosbox_conf_path fixes that.
#   * staging builds an OpenGL context at startup, before it consults any setting,
#     and aborts when there is none. SDL's dummy video driver has no OpenGL, so the
#     usual headless recipe is exactly what kills it; it needs a real X server even
#     though nothing is ever displayed.

# Whether the emulator can start without a display, established by trying it.
# Probed rather than matched against the version string: which build needs what is
# a property of how it was compiled, and the name is only a guess about that.
dosbox_detect_prefix() {
  DOSBOX_PREFIX=""
  local probe
  # Captured, not piped into grep: the probe aborts on purpose when it fails, and
  # under `set -o pipefail` the pipeline reports that abort rather than the match -
  # a successful detection would read as no detection at all.
  probe=$(timeout 30 "$DOSBOX" -c exit </dev/null 2>&1 || true)
  printf '%s' "$probe" | grep -qiE "ABORT|Could not initialize video" || return 0

  if command -v xvfb-run >/dev/null 2>&1; then
    # SDL_VIDEODRIVER is dropped along the way: callers set it to "dummy" for
    # vanilla DOSBox, and keeping it would hand SDL the driver with no OpenGL
    # inside the very X server provided to supply one.
    DOSBOX_PREFIX="env -u SDL_VIDEODRIVER xvfb-run -a"
    echo "$DOSBOX_FLAVOR needs a display: running it under xvfb-run"
  else
    echo "::error::$DOSBOX_FLAVOR cannot start headless and xvfb-run is not installed"
    exit 1
  fi
}

# An absolute config path, since the emulator may not share our working directory.
dosbox_conf_path() { # $1 = config file
  case "$1" in
    /*) printf '%s' "$1";;
    *) printf '%s/%s' "$PWD" "$1";;
  esac
}
