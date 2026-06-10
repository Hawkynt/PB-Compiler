using System.Text.RegularExpressions;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Backend run gate: selected PB-SvgaLibrary suites are compiled (with the
/// auto-generated SUB Test_* driver, exactly like the SVGA harness) and run
/// under DOSBox; UNITTEST.LOG must show the suite completing with zero [FAIL].
/// The remaining suites run advisory via the Explicit "RunAll" test.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed partial class CorpusRunTests {

  private static readonly string _corpusRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "PB-SvgaLibrary"));

  [GeneratedRegex(@"^[ \t]*SUB[ \t]+(Test_[A-Za-z0-9_]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
  private static partial Regex TestSubPattern();

  /// <summary>Appends the driver main (Setup, BeginSuite, Test_* calls, EndSuite, Teardown) unless the suite has its own.</summary>
  internal static string WithDriver(string source, string suiteName) {
    if (!source.Contains("SUB Test_", StringComparison.OrdinalIgnoreCase)
        || source.Contains("Test_BeginSuite", StringComparison.OrdinalIgnoreCase) && !source.Contains("SUB Test_BeginSuite", StringComparison.OrdinalIgnoreCase))
      return source;

    var subs = TestSubPattern().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var cases = subs.Where(s => !s.Equals("Test_Setup", StringComparison.OrdinalIgnoreCase) && !s.Equals("Test_Teardown", StringComparison.OrdinalIgnoreCase)).ToList();
    if (cases.Count == 0)
      return source;

    var driver = "\r\n' === auto-generated test driver ===\r\n";
    if (subs.Any(s => s.Equals("Test_Setup", StringComparison.OrdinalIgnoreCase)))
      driver += "CALL Test_Setup\r\n";
    driver += $"CALL Test_BeginSuite(\"{suiteName}\")\r\n";
    foreach (var name in cases)
      driver += $"CALL {name}\r\n";
    driver += $"CALL Test_EndSuite(\"{suiteName}\")\r\n";
    if (subs.Any(s => s.Equals("Test_Teardown", StringComparison.OrdinalIgnoreCase)))
      driver += "CALL Test_Teardown\r\n";
    driver += "END\r\n";
    return source + driver;
  }

  /// <summary>SVGA build-dir provider that additionally serves the driver-amended main file.</summary>
  private sealed class DriverSourceProvider(string mainPath, string mainText, string testsDir, string libRoot) : ISourceProvider {
    private readonly SearchPathSourceProvider _inner = new(testsDir, libRoot);

    public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
      if (Path.GetFullPath(name).Equals(Path.GetFullPath(mainPath), StringComparison.OrdinalIgnoreCase)) {
        (text, resolvedName) = (mainText, mainPath);
        return true;
      }
      if (Path.GetFileName(name).Equals("SVGAENG.SUB", StringComparison.OrdinalIgnoreCase)) {
        var lines = File.ReadAllLines(Path.Combine(libRoot, "SVGA.SUB")).Where(l => !l.TrimStart().StartsWith("$INCLUDE", StringComparison.OrdinalIgnoreCase));
        (text, resolvedName) = (string.Join("\r\n", lines), "SVGAENG.SUB");
        return true;
      }
      return this._inner.TryReadSource(name, includedFrom, out text, out resolvedName);
    }
  }

  internal static byte[] CompileSuiteWithDriver(string suiteName) {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    var path = Path.Combine(testsDir, suiteName + ".BAS");
    Assume.That(File.Exists(path), "PB-SvgaLibrary checkout not found - run gate skipped");

    var amended = WithDriver(File.ReadAllText(path), suiteName);
    var tokens = Preprocessor.Expand(path, new DriverSourceProvider(path, amended, testsDir, _corpusRoot));
    var unit = Parser.Parse(tokens, path);
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors.Take(10)));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors.Select(e => e.ToString()).Distinct().Take(20)));
    return exe;
  }

  private static string RunSuite(string suiteName, int timeoutMs = 120000) {
    var exe = CompileSuiteWithDriver(suiteName);
    var (_, files) = DosBoxRunner.RunWithFiles(exe, ["UNITTEST.LOG"], timeoutMs);
    Assert.That(files, Does.ContainKey("UNITTEST.LOG"), "the suite must write its log");
    var log = DosBoxRunner.Normalize(files["UNITTEST.LOG"]);
    TestContext.Out.WriteLine(log);
    return log;
  }

  [TestCase("FILEUTIL")]
  [TestCase("GRAPHICS")]
  [TestCase("MODETEXT")]
  [TestCase("TIMER")]
  [TestCase("MEMORY")]
  [TestCase("PORTGLUE")]
  public void Run_GivenSuite_WhenRunUnderDosBox_ThenLogShowsNoFailures(string suiteName) {
    var log = RunSuite(suiteName);
    Assert.Multiple(() => {
      Assert.That(log, Does.Contain($"[SUITE] {suiteName}"));
      Assert.That(log, Does.Not.Contain("[FAIL]"));
      Assert.That(log, Does.Contain($"[RESULT] {suiteName}"), "suite must run to completion");
      Assert.That(log, Does.Contain("failed= 0"));
    });
  }

  [Test, Explicit("advisory: run all 31 corpus suites and report per-suite results")]
  public void RunAll() {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    Assume.That(Directory.Exists(testsDir), "corpus missing");
    var report = new List<string>();
    foreach (var path in Directory.EnumerateFiles(testsDir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      var name = Path.GetFileNameWithoutExtension(path);
      try {
        var exe = CompileSuiteWithDriver(name);
        var (_, files) = DosBoxRunner.RunWithFiles(exe, ["UNITTEST.LOG"], 180000);
        if (!files.TryGetValue("UNITTEST.LOG", out var log)) {
          report.Add($"{name,-10} NO LOG");
          continue;
        }
        var fails = log.Split("[FAIL]").Length - 1;
        var passes = log.Split("[PASS]").Length - 1;
        var skips = log.Split("[SKIP]").Length - 1;
        var finished = log.Contains("[RESULT]");
        report.Add($"{name,-10} pass={passes} fail={fails} skip={skips}{(finished ? "" : " INCOMPLETE")}");
      } catch (Exception exception) {
        report.Add($"{name,-10} ERROR {exception.Message.Split('\n')[0]}");
      }
    }
    TestContext.Out.WriteLine(string.Join("\n", report));
  }
}
