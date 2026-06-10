using System.Diagnostics;

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

  /// <summary>Runs <paramref name="exeBytes"/> in DOSBox; returns the redirected stdout text.</summary>
  public static string Run(byte[] exeBytes, int timeoutMs = 60000)
    => RunWithFiles(exeBytes, [], timeoutMs).Output;

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
      File.WriteAllText(conf, $"""
        [sdl]
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
      // CreateNoWindow makes dosbox-staging hang before the autoexec, so the
      // emulator window briefly flashes during local runs - harmless.
      var psi = new ProcessStartInfo(Executable!, $"-conf \"{conf}\"") {
        UseShellExecute = false,
      };

      using var process = Process.Start(psi)!;
      var sentinel = Path.Combine(dir, "DONE.TXT");
      var deadline = Environment.TickCount64 + timeoutMs;
      while (!File.Exists(sentinel) && !process.HasExited && Environment.TickCount64 < deadline)
        Thread.Sleep(100);

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

  /// <summary>Normalizes CRLF and trailing whitespace per line for golden comparison.</summary>
  public static string Normalize(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
}
