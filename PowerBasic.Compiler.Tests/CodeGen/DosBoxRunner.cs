using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Runs a generated DOS executable under DOSBox (headless-ish) and captures the
/// output the program wrote to stdout via DOS redirection. Tests using this
/// helper are skipped when no DOSBox is available.
/// </summary>
public static class DosBoxRunner {

  public static string? Executable { get; } = Locate();

  private static string? Locate() {
    var env = Environment.GetEnvironmentVariable("DOSBOX_EXE");
    if (!string.IsNullOrEmpty(env) && File.Exists(env))
      return env;

    var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var local = Path.Combine(repoRoot, "tools", "dosbox");
    if (Directory.Exists(local))
      foreach (var candidate in Directory.EnumerateFiles(local, "dosbox.exe", SearchOption.AllDirectories))
        return candidate;

    return null;
  }


  /// <summary>How this emulator can be started without anyone looking at it.</summary>
  private enum Headless {
    /// <summary>Starts as it is - a real display, or a build that does not need one.</summary>
    AsIs,
    /// <summary>Starts once SDL is told to draw nowhere. Cheapest: no X server involved.</summary>
    DummyDriver,
    /// <summary>Needs a real X server to exist, even though nothing is ever displayed.</summary>
    VirtualDisplay,
  }

  /// <summary>
  /// SDL's "draw nowhere" driver, under BOTH spellings of the variable that selects it.
  ///
  /// SDL2 reads SDL_VIDEODRIVER and SDL3 reads SDL_VIDEO_DRIVER, and an SDL2 program is no longer
  /// evidence that SDL2 is what answers: sdl2-compat re-implements the SDL2 ABI on top of SDL3, so
  /// a distribution can switch the library underneath an unchanged emulator binary. Setting only
  /// the SDL2 name then stops selecting anything - SDL3 ignores it, opens the real video backend,
  /// finds no GLX visual and aborts before the autoexec. Setting both costs nothing and does not
  /// care which library is underneath.
  /// </summary>
  private static readonly (string Name, string Value)[] _dummyDriver =
    [("SDL_VIDEODRIVER", "dummy"), ("SDL_VIDEO_DRIVER", "dummy")];

  /// <summary>
  /// Which of the three ways of starting works here, established by trying them in cost order.
  ///
  /// Probed rather than matched against the version string, and probed WITH the environment each
  /// way would actually launch under - a probe that inherits a different environment than the
  /// launch answers a question nobody asked. That was the bug this replaced: the probe ran with
  /// whatever SDL variables the caller had exported, concluded "needs a display" when they failed
  /// to apply, and sent every run through an X server that then could not give it a GLX visual.
  ///
  /// The way it fails matters more than the failure. The emulator aborts before the autoexec, so
  /// nothing runs, no output file appears, and several hundred execution tests report as though
  /// the generated programs were broken - a host library upgrade wearing the costume of a compiler
  /// regression.
  /// </summary>
  private static readonly Lazy<Headless> _headless = new(() => {
    // dosbox-staging on Windows quits before the autoexec under the dummy driver, so that host
    // takes itself out of the choice entirely.
    if (Executable == null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      return Headless.AsIs;
    // The dummy driver is tried FIRST, and not only because it is the cheapest. Where DISPLAY is
    // set - a developer machine rather than CI - starting as-is also works, and puts a real window
    // on the user's desktop for every one of several hundred execution tests.
    if (StartsCleanly(applyDummyDriver: true))
      return Headless.DummyDriver;
    if (StartsCleanly(applyDummyDriver: false))
      return Headless.AsIs;
    return Headless.VirtualDisplay;
  });

  /// <summary>
  /// Whether the emulator gets past starting its video, probed with `-c exit`.
  ///
  /// It is the ABORT that is being watched for, not the exit: an emulator that starts properly may
  /// well sit there afterwards - dosbox-staging holds the window open when a command finishes too
  /// quickly - so outliving the probe counts as success. Which makes the time bound part of the
  /// test rather than a safety net, and it has to bound the READ as well as the wait: reading both
  /// streams to the end first blocks until the emulator closes them, so a probe of an emulator
  /// that works never returns at all. That deadlock is invisible in a test run. It looks like a
  /// slow suite, right up until the run is killed with nothing to show.
  /// </summary>
  private static bool StartsCleanly(bool applyDummyDriver) {
    try {
      var probe = new ProcessStartInfo(Executable!, "-c exit") {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      foreach (var (name, value) in _dummyDriver)
        if (applyDummyDriver)
          probe.Environment[name] = value;
        else
          probe.Environment.Remove(name);

      var text = new System.Text.StringBuilder();
      using var process = Process.Start(probe)!;
      void Collect(object _, DataReceivedEventArgs e) {
        if (e.Data != null)
          lock (text)
            text.AppendLine(e.Data);
      }
      process.OutputDataReceived += Collect;
      process.ErrorDataReceived += Collect;
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();

      var exited = process.WaitForExit(15000);
      if (!exited) {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      lock (text) {
        var said = text.ToString();
        return !said.Contains("ABORT", StringComparison.OrdinalIgnoreCase)
            && !said.Contains("Could not initialize video", StringComparison.OrdinalIgnoreCase);
      }
    } catch {
      return false;
    }
  }

  private static bool HasXvfb => File.Exists("/usr/bin/xvfb-run");

  /// <summary>
  /// The wait a program gets, stretched when the emulator runs inside a virtual X server.
  ///
  /// 60 seconds is ample natively and tight under xvfb, which is several times slower - and the way
  /// it fails is not "this test timed out". A program killed part-way writes a TRUNCATED output
  /// file, and the assertion then reports a value that differs from the golden: a fidelity failure
  /// that is nothing of the kind. The shell harness hit exactly this and blamed an unrelated
  /// dialect for it, so the same allowance is made here.
  /// </summary>
  private static int Deadline(int timeoutMs) => _headless.Value == Headless.VirtualDisplay ? timeoutMs * 5 : timeoutMs;

  /// <summary>
  /// A start-info for the emulator, started the way this host was found to accept.
  /// Every launch of DOSBox in the test suite must go through here - three fixtures used to build
  /// their own ProcessStartInfo and were the only ones still failing after this was introduced.
  /// </summary>
  public static ProcessStartInfo Launch(string arguments) {
    var psi = _headless.Value == Headless.VirtualDisplay
      ? new ProcessStartInfo("/usr/bin/xvfb-run", $"-a \"{Executable}\" {arguments}")
      : new ProcessStartInfo(Executable!, arguments);
    psi.UseShellExecute = false;

    switch (_headless.Value) {
      case Headless.DummyDriver:
        foreach (var (name, value) in _dummyDriver)
          psi.Environment[name] = value;
        break;
      case Headless.VirtualDisplay:
        Assume.That(HasXvfb, Is.True,
          $"{Executable} cannot start headless and xvfb-run is not installed - execution test skipped");
        // Dropped deliberately: handing SDL the driver that draws nowhere, inside the very X
        // server provided because it needs one, puts back the failure the X server is here to fix.
        foreach (var (name, _) in _dummyDriver)
          psi.Environment.Remove(name);
        break;
    }
    return psi;
  }

  /// <summary>Runs <paramref name="exeBytes"/> in DOSBox; returns the redirected stdout text.</summary>
  public static string Run(byte[] exeBytes, int timeoutMs = 60000)
    => RunWithFiles(exeBytes, [], timeoutMs).Output;

  /// <summary>
  /// R1/R2 screen-capture oracle: runs <paramref name="exeBytes"/> WITHOUT stdout redirection
  /// (so console output actually reaches the BIOS/video path), then runs the supplied capture
  /// program (a compiled BASIC helper that PEEKs B800 text memory into SCREEN.TXT) and returns
  /// that file's content - the observable screen, comparable across build variants.
  /// </summary>
  public static string RunWithScreenCapture(byte[] exeBytes, byte[] captureExe, int timeoutMs = 60000) {
    Assume.That(Executable, Is.Not.Null, "DOSBox not found - execution test skipped");
    var dir = Path.Combine(Path.GetTempPath(), "pbc-scr-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      File.WriteAllBytes(Path.Combine(dir, "T.EXE"), exeBytes);
      File.WriteAllBytes(Path.Combine(dir, "CAP.EXE"), captureExe);
      var conf = Path.Combine(dir, "dosbox.conf");
      File.WriteAllText(conf, $"""
        [sdl]
        window_position = 9000,9000
        [dosbox]
        ems=true
        [autoexec]
        mount c "{dir}"
        c:
        cls
        T.EXE
        CAP.EXE
        echo ok > DONE.TXT
        exit
        """);
      var psi = Launch($"-conf \"{conf}\"");
      using var process = Process.Start(psi)!;
      var sentinel = Path.Combine(dir, "DONE.TXT");
      var deadline = Environment.TickCount64 + Deadline(timeoutMs);
      var minimized = false;
      while (!File.Exists(sentinel) && !process.HasExited && Environment.TickCount64 < deadline) {
        if (!minimized)
          minimized = TryHideWindow(process);
        Thread.Sleep(50);
      }
      var finished = File.Exists(sentinel);   // the sentinel alone, for the reason given below
      if (!process.HasExited) {
        if (finished)
          Thread.Sleep(200);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      if (!finished)
        Assert.Fail("DOSBox screen-capture run timed out");
      var screen = Path.Combine(dir, "SCREEN.TXT");
      return File.Exists(screen) ? File.ReadAllText(screen) : "";
    } finally {
      try {
        Directory.Delete(dir, recursive: true);
      } catch (IOException) {
      }
    }
  }

  /// <summary>
  /// Runs <paramref name="exeBytes"/> in DOSBox and additionally retrieves the
  /// named files the program created in its working directory (e.g. UNITTEST.LOG).
  /// Optional <paramref name="stdinText"/> is redirected into the program;
  /// <paramref name="extraFiles"/> are placed beside it before the run.
  /// </summary>
  public static (string Output, Dictionary<string, string> Files) RunWithFiles(byte[] exeBytes, IReadOnlyList<string> fetchFiles, int timeoutMs = 60000, string? stdinText = null, IReadOnlyDictionary<string, string>? extraFiles = null) {
    Assume.That(Executable, Is.Not.Null, "DOSBox not found - execution test skipped");

    var dir = Path.Combine(Path.GetTempPath(), "pbc-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      // DONE.TXT is the completion sentinel: dosbox-staging blocks the autoexec
      // `exit` when the program finishes "too quickly" (its anti-vanish UX), so
      // the runner waits for the sentinel and then kills the emulator itself.
      File.WriteAllBytes(Path.Combine(dir, "T.EXE"), exeBytes);
      if (stdinText != null)
        File.WriteAllText(Path.Combine(dir, "IN.TXT"), stdinText);
      if (extraFiles != null)
        foreach (var (name, content) in extraFiles)
          File.WriteAllText(Path.Combine(dir, name), content);
      var run = stdinText != null ? "T.EXE < IN.TXT > T.OUT" : "T.EXE > T.OUT";
      var conf = Path.Combine(dir, "dosbox.conf");
      // window_position is best-effort (dosbox-staging 0.82.2 clamps off-screen
      // values, and rejects the old 'windowposition' spelling); the window is
      // actually hidden by minimizing it via the OS once it appears (below).
      File.WriteAllText(conf, $"""
        [sdl]
        window_position = 9000,9000
        [dosbox]
        ems=true
        [autoexec]
        mount c "{dir}"
        c:
        {run}
        echo ok > DONE.TXT
        exit
        """);

      // No stream redirection: an undrained stdout pipe deadlocks DOSBox once it
      // fills. SDL_VIDEODRIVER=dummy is also avoided - dosbox-staging on Windows
      // quits before the autoexec runs with it; CI uses classic dosbox on Linux.
      // CreateNoWindow makes dosbox-staging hang before the autoexec; instead
      // the [sdl] windowposition above parks the window off-screen (dosbox-x)
      // so local runs do not disturb the desktop.
      var psi = Launch($"-conf \"{conf}\"");

      using var process = Process.Start(psi)!;
      var sentinel = Path.Combine(dir, "DONE.TXT");
      var deadline = Environment.TickCount64 + Deadline(timeoutMs);
      var minimized = false;
      while (!File.Exists(sentinel) && !process.HasExited && Environment.TickCount64 < deadline) {
        if (!minimized)
          minimized = TryHideWindow(process);
        Thread.Sleep(50);
      }

      // The SENTINEL is the only evidence the program ran to the end. Counting a bare process exit
      // as "finished" too was a hole: an emulator that quit before its autoexec - which happens
      // under load, and happened once in a full 4300-test run - left an empty output file that the
      // caller then compared against the expected text and reported as a wrong ANSWER. A run that
      // did not complete is not an answer, so it says so instead.
      var completed = File.Exists(sentinel);
      if (!process.HasExited) {
        if (completed)
          Thread.Sleep(200); // let the redirection handles settle
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      if (!completed)
        Assert.Fail(process.HasExited
          ? "DOSBox exited without running the program to completion (no DONE.TXT) - the emulator "
            + "quit early rather than the program answering wrongly"
          : "DOSBox run timed out - generated program probably hangs");

      var outFile = Path.Combine(dir, "T.OUT");
      var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var name in fetchFiles) {
        var path = Path.Combine(dir, name);
        if (File.Exists(path))
          files[name] = File.ReadAllText(path);
      }
      return (File.Exists(outFile) ? File.ReadAllText(outFile) : "", files);
    } finally {
      try {
        Directory.Delete(dir, recursive: true);
      } catch (IOException) {
        // best effort - DOSBox occasionally holds the dir a moment longer
      }
    }
  }

  /// <summary>
  /// dosbox-staging on Windows always shows its SDL window (off-screen positioning is
  /// clamped), so minimize it as soon as it appears - the tests read the redirected
  /// output file, never the screen. Returns true once handled (or on non-Windows,
  /// where CI runs headless under Xvfb and there is nothing to hide); false means the
  /// window has not been created yet, so the caller retries on the next poll.
  /// </summary>
  private static bool TryHideWindow(Process process) {
    if (!OperatingSystem.IsWindows())
      return true;
    process.Refresh();
    var handle = process.MainWindowHandle;
    if (handle == IntPtr.Zero)
      return false;
    ShowWindow(handle, SW_SHOWMINNOACTIVE);
    return true;
  }

  private const int SW_SHOWMINNOACTIVE = 7; // minimize without stealing focus

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

  /// <summary>Normalizes CRLF and trailing whitespace per line for golden comparison.</summary>
  public static string Normalize(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
}
