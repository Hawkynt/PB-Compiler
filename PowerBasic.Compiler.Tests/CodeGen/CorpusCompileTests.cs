using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Full-pipeline backend gate: every PB-SvgaLibrary test suite must compile
/// through preprocess, parse, bind AND codegen without a single error.
/// Skipped without the sibling checkout.
/// </summary>
[TestFixture]
public sealed class CorpusCompileTests {

  private static readonly string _corpusRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "PB-SvgaLibrary"));

  /// <summary>Mirrors the SVGA harness: SVGAENG.SUB = SVGA.SUB minus its $INCLUDE lines.</summary>
  private sealed class SvgaBuildDirProvider(string testsDir, string libRoot) : ISourceProvider {
    private readonly SearchPathSourceProvider _inner = new(testsDir, libRoot);

    public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
      if (!Path.GetFileName(name).Equals("SVGAENG.SUB", StringComparison.OrdinalIgnoreCase))
        return this._inner.TryReadSource(name, includedFrom, out text, out resolvedName);

      var lines = File.ReadAllLines(Path.Combine(libRoot, "SVGA.SUB")).Where(l => !l.TrimStart().StartsWith("$INCLUDE", StringComparison.OrdinalIgnoreCase));
      (text, resolvedName) = (string.Join("\r\n", lines), "SVGAENG.SUB");
      return true;
    }
  }

  public static IEnumerable<TestCaseData> Suites() {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    if (!Directory.Exists(testsDir))
      yield break;
    foreach (var suite in Directory.EnumerateFiles(testsDir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      yield return new(suite) { TestName = $"Compile_GivenSuite_{Path.GetFileNameWithoutExtension(suite)}_WhenGenerated_ThenNoErrors" };
  }

  internal static byte[] CompileSuite(string suite, out List<Diagnostic> errors) {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    var tokens = Preprocessor.Expand(suite, new SvgaBuildDirProvider(testsDir, _corpusRoot));
    var unit = Parser.Parse(tokens, suite);
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, string.Join("\n", model.Errors.Take(10)));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    errors = generator.Errors;
    return exe;
  }

  [TestCaseSource(nameof(Suites))]
  public void Compile_GivenSvgaSuite_WhenGenerated_ThenNoErrors(string suite) {
    CompileSuite(suite, out var errors);
    Assert.That(errors, Is.Empty, $"{errors.Count} errors:\n" + string.Join("\n", errors.Select(e => e.ToString()).Distinct().Take(40)));
  }

  [Test, Explicit("diagnostic histogram over the whole corpus")]
  public void Histogram() {
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var data in Suites()) {
      var suite = (string)data.Arguments[0]!;
      var exe = CompileSuite(suite, out var errors);
      TestContext.Out.WriteLine($"size {exe.Length,7}  {Path.GetFileNameWithoutExtension(suite)}");
      foreach (var e in errors) {
        var key = e.Message + " @" + Path.GetFileNameWithoutExtension(suite);
        counts[key] = counts.GetValueOrDefault(key) + 1;
      }
    }
    foreach (var (message, count) in counts.OrderByDescending(p => p.Value))
      TestContext.Out.WriteLine($"{count,6}  {message}");
  }
}
