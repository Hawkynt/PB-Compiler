using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// Full-pipeline front-end gate: every PB-SvgaLibrary test suite must
/// preprocess, parse AND bind without a single error. Skipped without the
/// sibling checkout.
/// </summary>
[TestFixture]
public sealed class BinderCorpusTests {

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
    foreach (var suite in Directory.EnumerateFiles(testsDir, "*.BAS").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
      // the genuine compiler rejects these too - see CorpusCompileTests._rejectedByTheOracle
      if (Path.GetFileNameWithoutExtension(suite).Equals("VESA", StringComparison.OrdinalIgnoreCase))
        continue;
      yield return new(suite) { TestName = $"Bind_GivenSuite_{Path.GetFileNameWithoutExtension(suite)}_WhenBound_ThenNoErrors" };
    }
  }

  [TestCaseSource(nameof(Suites))]
  public void Bind_GivenSvgaSuite_WhenBound_ThenNoErrors(string suite) {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    var tokens = Preprocessor.Expand(suite, new SvgaBuildDirProvider(testsDir, _corpusRoot));
    var unit = Parser.Parse(tokens, suite);
    var model = Binder.Bind(unit);
    Assert.That(model.Errors, Is.Empty, string.Join("\n", model.Errors.Take(10)));
  }

  [Test]
  public void Bind_GivenSvgaUmbrella_WhenBound_ThenNoErrorsAndAllModulesPresent() {
    var main = Path.Combine(_corpusRoot, "SVGA.SUB");
    Assume.That(File.Exists(main), "PB-SvgaLibrary checkout not found - corpus gate skipped");

    var tokens = Preprocessor.Expand(main, new FileSourceProvider());
    var unit = Parser.Parse(tokens, main);
    var model = Binder.Bind(unit);

    Assert.That(model.Errors, Is.Empty, string.Join("\n", model.Errors.Take(10)));
    Assert.That(model.Procedures.Count, Is.GreaterThan(100), "umbrella should define the whole library API");
  }
}
