using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Acceptance gate: the parser must fully parse the real-world PB-SvgaLibrary corpus
/// (umbrella include plus every test suite). Skipped when the checkout is absent.
/// </summary>
[TestFixture]
public sealed class ParserCorpusTests {

  private static readonly string _corpusRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "PB-SvgaLibrary"));

  [Test]
  public void Parse_GivenSvgaUmbrellaInclude_WhenParsed_ThenWholeLibraryParses() {
    var main = Path.Combine(_corpusRoot, "SVGA.SUB");
    Assume.That(File.Exists(main), "PB-SvgaLibrary checkout not found - corpus gate skipped");

    var unit = Parser.Parse(Preprocessor.Expand(main, new FileSourceProvider()), main);

    var total = CountStatements(unit.Statements);
    TestContext.Out.WriteLine($"{unit.Statements.Count} top-level statements, {total} total");
    Assert.Multiple(() => {
      Assert.That(unit.Statements, Has.Count.GreaterThan(100), "umbrella should yield a substantial module");
      Assert.That(total, Is.GreaterThan(1000), "umbrella bodies should be fully parsed");
    });
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
  public void Parse_GivenEveryTestSuite_WhenParsed_ThenNoParserErrors() {
    var testsDir = Path.Combine(_corpusRoot, "tests");
    Assume.That(Directory.Exists(testsDir), "PB-SvgaLibrary checkout not found - corpus gate skipped");

    var failures = new List<string>();
    var suites = 0;
    var total = 0L;
    foreach (var suite in Directory.EnumerateFiles(testsDir, "*.BAS")) {
      ++suites;
      try {
        var unit = Parser.Parse(Preprocessor.Expand(suite, new SvgaBuildDirProvider(testsDir, _corpusRoot)), suite);
        var count = CountStatements(unit.Statements);
        total += count;
        if (count == 0)
          failures.Add($"{Path.GetFileName(suite)}: parsed to zero statements");
      } catch (ParserException e) {
        failures.Add($"{Path.GetFileName(suite)}: {e.Message}");
      }
    }

    TestContext.Out.WriteLine($"{suites} suites, {total} statements total");
    Assert.That(failures, Is.Empty, string.Join("\n", failures));
  }

  /// <summary>Counts statements recursively through every nested body the AST has.</summary>
  private static int CountStatements(IEnumerable<Statement> statements) {
    var count = 0;
    foreach (var statement in statements) {
      ++count;
      count += statement switch {
        SubDecl s => CountStatements(s.Body),
        FunctionDecl f => CountStatements(f.Body),
        DefFnDecl d when d.BlockBody != null => CountStatements(d.BlockBody),
        IfStmt i => CountStatements(i.Then)
          + i.ElseIfs.Sum(e => CountStatements(e.Body))
          + (i.Else == null ? 0 : CountStatements(i.Else)),
        SelectStmt s => s.Arms.Sum(a => CountStatements(a.Body)),
        ForStmt f => CountStatements(f.Body),
        DoLoopStmt d => CountStatements(d.Body),
        _ => 0,
      };
    }
    return count;
  }
}
