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


  /// <summary>
  /// Whether this emulator refuses to start without a display, established by trying it once.
  ///
  /// Vanilla DOSBox is happy with SDL's dummy video driver. dosbox-staging builds an OpenGL
  /// context before it reads any setting and aborts under that driver, so it needs a real X
  /// server even though these tests never look at a window. Without this, pointing DOSBOX_EXE at
  /// staging does not skip and does not warn - every execution test simply fails, which reads as
  /// several hundred compiler regressions.
  ///
  /// Probed rather than matched against the version string: which build needs what is a property
  /// of how it was compiled, and the name is only a guess about that.
  /// </summary>
  private static readonly Lazy<bool> _needsDisplay = new(() => {
    if (Executable == null || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      return false;
    try {
      var probe = new ProcessStartInfo(Executable, "-c exit") {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      using var process = Process.Start(probe)!;
      var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
      if (!process.WaitForExit(30000)) {
        process.Kill(entireProcessTree: true);
        return false;
      }
      return text.Contains("ABORT", StringComparison.OrdinalIgnoreCase)
          || text.Contains("Could not initialize video", StringComparison.OrdinalIgnoreCase);
    } catch {
      return false;
    }
  });

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
  private static int Deadline(int timeoutMs) => _needsDisplay.Value ? timeoutMs * 5 : timeoutMs;

  /// <summary>
  /// A start-info for the emulator, wrapped in a virtual X server when it needs one.
  /// Every launch of DOSBox in the test suite must go through here - three fixtures used to build
  /// their own ProcessStartInfo and were the only ones still failing after this was introduced.
  /// </summary>
  public static ProcessStartInfo Launch(string arguments) {
    if (!_needsDisplay.Value)
      return new ProcessStartInfo(Executable!, arguments) { UseShellExecute = false };

    Assume.That(HasXvfb, Is.True,
      $"{Executable} cannot start headless and xvfb-run is not installed - execution test skipped");
    var psi = new ProcessStartInfo("/usr/bin/xvfb-run", $"-a \"{Executable}\" {arguments}") {
      UseShellExecute = false,
    };
    // Dropped deliberately: callers set it to "dummy" for vanilla DOSBox, and keeping it would
    // hand SDL the driver with no OpenGL inside the very X server provided to supply one.
    psi.Environment.Remove("SDL_VIDEODRIVER");
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
      var finished = File.Exists(sentinel) || process.HasExited;
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

      var finished = File.Exists(sentinel) || process.HasExited;
      if (!process.HasExited) {
        if (finished)
          Thread.Sleep(200); // let the redirection handles settle
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      if (!finished)
        Assert.Fail("DOSBox run timed out - generated program probably hangs");

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
