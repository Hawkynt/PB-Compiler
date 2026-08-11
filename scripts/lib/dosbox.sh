# Locating a DOSBox and starting it in a way that works headless.
#
# Sourced by run-diff-tests.sh, run-dos-tests.sh and diff-one.sh. Sets:
#
#   DOSBOX          the emulator, or empty when none was found
#   DOSBOX_FLAVOR   its self-reported version line (the autotype gate reads this)
#   DOSBOX_PREFIX   a command prefix to launch it with, possibly empty
#   DOSBOX_TICKS    how many 0.2s ticks to wait for a program to finish
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
#     usual headless recipe may be exactly what kills it, and it then needs a real X
#     server even though nothing is ever displayed.
#
# Which SDL reads that driver setting is not fixed either. SDL2 reads SDL_VIDEODRIVER
# and SDL3 reads SDL_VIDEO_DRIVER, and an SDL2 emulator is no longer evidence of SDL2:
# sdl2-compat re-implements the SDL2 ABI over SDL3, so a distribution can swap the
# library underneath an unchanged binary. Setting only the SDL2 name then selects
# nothing, the emulator opens the real backend, finds no GLX visual and aborts before
# the autoexec - no output file, and every program reporting as though the ORACLE had
# refused it. Both names are set; whichever library is underneath, one of them lands.

_DOSBOX_DUMMY_ENV="SDL_VIDEODRIVER=dummy SDL_VIDEO_DRIVER=dummy"

# Whether the emulator gets past starting its video, probed with `-c exit`.
#
# It is the ABORT that is being watched for, not the exit: an emulator that starts properly
# may well sit there afterwards - dosbox-staging holds its window open when a command
# finishes too quickly - so outliving the `timeout` counts as success. The bound is part of
# the test, not a safety net around it.
#
# Captured, not piped into grep: the probe aborts on purpose when it fails, and under
# `set -o pipefail` the pipeline reports that abort rather than the match - a successful
# detection would read as no detection at all.
_dosbox_starts_cleanly() { # $@ = env assignments to launch under
  local probe
  probe=$(timeout 15 env "$@" "$DOSBOX" -c exit </dev/null 2>&1 || true)
  ! printf '%s' "$probe" | grep -qiE "ABORT|Could not initialize video"
}

# How to start the emulator here, established by trying the ways in cost order.
# Probed rather than matched against the version string: which build needs what is
# a property of how it was compiled, and the name is only a guess about that. Each way
# is probed UNDER THE ENVIRONMENT IT WOULD LAUNCH WITH - a probe that inherits a
# different environment than the launch answers a question nobody asked.
dosbox_detect_prefix() {
  DOSBOX_PREFIX=""
  # 120 seconds is ample for vanilla DOSBox and marginal under xvfb, which runs the
  # whole battery about four times slower. A slow ORACLE that gets killed mid-write
  # is the worst outcome available: it leaves a truncated RESULT.TXT, which is not
  # reported as "the oracle did not run" but as an output difference - a fidelity
  # divergence that is not one. Two qb20 programs failed exactly that way in a full
  # run and passed on their own immediately after.
  DOSBOX_TICKS=600

  # The dummy driver is tried FIRST, and not only because it is the cheapest way that can work.
  # Where DISPLAY is set - a developer machine rather than CI - starting as-is also works, and puts
  # a real window on the desktop for every program in the battery.
  # shellcheck disable=SC2086  # deliberately word-split: these are separate assignments
  if _dosbox_starts_cleanly $_DOSBOX_DUMMY_ENV; then
    DOSBOX_PREFIX="env $_DOSBOX_DUMMY_ENV"
    echo "$DOSBOX_FLAVOR needs SDL's dummy video driver: running it with $_DOSBOX_DUMMY_ENV"
  elif _dosbox_starts_cleanly -u SDL_VIDEODRIVER -u SDL_VIDEO_DRIVER; then
    return 0
  elif command -v xvfb-run >/dev/null 2>&1; then
    # The dummy driver is dropped along the way: handing SDL the driver that draws
    # nowhere, inside the very X server provided because it needs one, puts back the
    # failure the X server is here to fix.
    DOSBOX_PREFIX="env -u SDL_VIDEODRIVER -u SDL_VIDEO_DRIVER xvfb-run -a"
    DOSBOX_TICKS=3000
    echo "$DOSBOX_FLAVOR needs a display: running it under xvfb-run (10 minute per-program limit)"
  else
    echo "::error::$DOSBOX_FLAVOR cannot start headless and xvfb-run is not installed"
    exit 1
  fi
}

# Stop a launched emulator AND everything it started.
#
# xvfb-run is a SHELL SCRIPT: it starts an X server, runs the command, and cleans the
# server up when it exits normally. Killing the wrapper skips that entirely, leaving
# both the X server and the emulator orphaned - and nothing ever reaps them. One full
# battery leaked about 2000 Xvfb processes and 90 emulators, drove the load average
# past 400, and the machine was then slow enough that ORACLES began missing their
# deadline and reporting as fidelity differences. The leak diagnosed itself as a
# compiler bug in another dialect, which is the expensive kind of wrong.
dosbox_kill() { # $1 = pid of the launched job
  local pid="$1" signal
  for signal in TERM KILL; do
    kill -"$signal" -- "-$pid" 2>/dev/null || true   # the process GROUP (see setsid at the call site)
    pkill -"$signal" -P "$pid" 2>/dev/null || true   # its children, when the group did not cover them
    kill -"$signal" "$pid" 2>/dev/null || true
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.3
  done
}

# An absolute config path, since the emulator may not share our working directory.
dosbox_conf_path() { # $1 = config file
  case "$1" in
    /*) printf '%s' "$1";;
    *) printf '%s/%s' "$PWD" "$1";;
  esac
}
