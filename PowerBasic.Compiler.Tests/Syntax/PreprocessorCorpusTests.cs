using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Expands real-world entry points from PB-SvgaLibrary (umbrella include of the
/// whole library plus every test suite). Skipped when the checkout is absent.
/// </summary>
[TestFixture]
public sealed class PreprocessorCorpusTests {

  private static readonly string _corpusRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "PB-SvgaLibrary"));

  [Test]
  public void Expand_GivenSvgaUmbrellaInclude_WhenExpanded_ThenWholeLibrarySplices() {
    var main = Path.Combine(_corpusRoot, "SVGA.SUB");
    Assume.That(File.Exists(main), "PB-SvgaLibrary checkout not found - corpus smoke skipped");

    var tokens = Preprocessor.Expand(main, new FileSourceProvider()).ToList();

    Assert.That(tokens.Count(t => t.Kind == TokenKind.EndOfFile), Is.EqualTo(1));
    Assert.That(tokens.Count(t => t.Kind == TokenKind.MetaCommand && t.Text == "INCLUDE"), Is.Zero, "all $INCLUDEs must be resolved");
    var files = tokens.Select(t => t.Position.File).Distinct().Count();
    TestContext.Out.WriteLine($"{tokens.Count} tokens from {files} files");
    Assert.That(files, Is.GreaterThan(20), "umbrella should splice the whole library");
  }

  /// <summary>
  /// The SVGA harness synthesizes SVGAENG.SUB (= SVGA.SUB with its $INCLUDE lines stripped)
  /// into the build directory; emulate that on top of the normal search-path lookup.
  /// </summary>
  private sealed class SvgaBuildDirProvider(string testsDir, string libRoot) : ISourceProvider {
    private readonly SearchPathSourceProvider _inner = new(testsDir, libRoot);

    public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
      if (!Path.GetFileName(name).Equals("SVGAENG.SUB", StringComparison.OrdinalIgnoreCase))
        return this._inner.TryReadSource(name, includedFrom, out text, out resolvedName);

      var umbrella = Path.Combine(libRoot, "SVGA.SUB");
      var lines = File.ReadAllLines(umbrella).Where(l => !l.TrimStart().StartsWith("$INCLUDE", StringComparison.OrdinalIgnoreCase));
      (text, resolvedName) = (string.Join("\r\n", lines), "SVGAENG.SUB");
      return true;
    }
  }

  [Test]
  public void Expand_GivenEveryTestSuite_WhenExpanded_ThenNoPreprocessorErrors() {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    Assume.That(Directory.Exists(testsDir), "PB-SvgaLibrary checkout not found - corpus smoke skipped");

    // suites are compiled from a build dir holding all .SUB/.BI files flat, so includes
    // like "TESTLIB.BI" resolve next to the suite; module includes live one level up
    var failures = new List<string>();
    foreach (var suite in Directory.EnumerateFiles(testsDir, "*.BAS")) {
      try {
        _ = Preprocessor.Expand(suite, new SvgaBuildDirProvider(testsDir, _corpusRoot)).Count();
      } catch (PreprocessorException e) {
        failures.Add($"{Path.GetFileName(suite)}: {e.Message}");
      }
    }

    Assert.That(failures, Is.Empty, string.Join("\n", failures));
  }
}
