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
# refused it.
#
# The two names are ALTERNATIVES and setting both is not a safe way to cover both:
# sdl2-compat honours the SDL2 name by selecting SDL2's dummy driver, which has no
# OpenGL and aborts. Belt and braces is a WORSE configuration than either belt alone,
# so each is a candidate of its own and the working one is found by trying. Each
# candidate also UNSETS the other name, so an exported leftover cannot interfere.

# Every candidate also unsets DISPLAY, and that is the design rather than tidiness. "Headless"
# cannot be established by looking for an error message, because the way this goes wrong is that
# the emulator starts PERFECTLY - on the user's desktop. A probe reading stderr for an abort cannot
# tell that apart from success, and twice reported a configuration as working while it opened a
# window per program on a machine somebody was using. With DISPLAY unset no window is POSSIBLE, so
# "it ran" is proof of the property being claimed.
_DOSBOX_CANDIDATES=(
  "-u DISPLAY -u SDL_VIDEO_DRIVER SDL_VIDEODRIVER=dummy"      # SDL2's spelling
  "-u DISPLAY -u SDL_VIDEODRIVER SDL_VIDEO_DRIVER=dummy"      # SDL3's spelling
  "-u DISPLAY SDL_VIDEODRIVER=offscreen SDL_VIDEO_DRIVER=offscreen"
  "-u DISPLAY -u SDL_VIDEO_DRIVER -u SDL_VIDEODRIVER"         # no setting at all
)

# Whether the emulator RAN A PROGRAM, established by making it write a file.
#
# The absence of an abort is not the same question, and answering that one instead has a
# false positive that costs a whole battery. Under `xvfb-run`, dosbox-staging 0.82.2 neither
# aborts nor complains about video - it starts, reaches gallium, and then hangs BEFORE
# `[autoexec]` runs at all (measured 2026-08-27: 60s bound, nothing written). An abort-watching
# probe scores that as working. Every program then sits until its tick limit and yields a
# truncated or absent RESULT.TXT, which this harness reports as an output difference - a
# fidelity divergence that is not one, in whichever dialect happened to be running.
#
# So the probe asks the emulator to DO something. A candidate passes only when the file its
# autoexec writes comes back with the expected contents, which no amount of starting-and-hanging
# can fake. Outliving the bound is still permitted - staging holds its window open when a command
# finishes too quickly - but only if the file is there, so the timeout is no longer evidence of
# anything on its own.
#
# The mount is the probe directory itself, so nothing is written inside the repo.
_dosbox_starts_cleanly() { # $@ = env assignments to launch under
  local dir conf
  dir=$(mktemp -d) || return 1
  conf="$dir/probe.conf"
  printf '[autoexec]\nmount c "%s"\nc:\necho headless > PROBE.TXT\nexit\n' "$dir" > "$conf"
  # In a subshell with its stderr closed: a candidate that fails does so by SIGABRT, and the
  # shell announces a killed job on ITS stderr, which redirecting the command alone leaves visible.
  # The probe is expected to fail for most candidates, so that notice is noise on every run.
  ( timeout 20 env "$@" "$DOSBOX" -conf "$conf" </dev/null >/dev/null 2>&1 || true ) 2>/dev/null
  local ok=1
  grep -qi headless "$dir/PROBE.TXT" 2>/dev/null && ok=0
  rm -rf "$dir"
  return $ok
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

  local candidate
  for candidate in "${_DOSBOX_CANDIDATES[@]}"; do
    # shellcheck disable=SC2086  # deliberately word-split: separate -u flags and assignments
    if _dosbox_starts_cleanly $candidate; then
      DOSBOX_PREFIX="env $candidate"
      echo "$DOSBOX_FLAVOR starts headless with: env $candidate"
      return 0
    fi
  done

  # No headless way is a missing capability of this HOST, and it does not degrade into using the
  # desktop: a battery of several hundred programs would then open a window each on the machine of
  # whoever ran it, as an unannounced consequence of a library upgrade. xvfb-run is not a fallback
  # either - it is only worth trying if it actually works, so it is a candidate like any other.
  if [ "${PBC_ALLOW_DISPLAY:-}" = "1" ]; then
    echo "::warning::$DOSBOX_FLAVOR has no headless mode here - using the desktop (PBC_ALLOW_DISPLAY=1)"
    return 0
  fi
  echo "::error::$DOSBOX_FLAVOR has no way to run without a display on this host."
  echo "::error::Set PBC_ALLOW_DISPLAY=1 to let it use the desktop, or point DOSBOX_EXE at a build"
  echo "::error::that runs headless (vanilla DOSBox does; dosbox-staging needs GLX)."
  exit 1
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
