using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Smoke test against a real-world PowerBASIC 3.5 codebase (PB-SvgaLibrary).
/// Skipped when the sibling checkout is not present (e.g. on CI).
/// </summary>
[TestFixture]
public sealed class LexerCorpusTests {

  private static readonly string _corpusRoot = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "PB-SvgaLibrary");

  private static IEnumerable<string> CorpusFiles() {
    if (!Directory.Exists(_corpusRoot))
      yield break;
    foreach (var pattern in (string[])["*.SUB", "*.BAS", "*.BI", "*.INC"])
    foreach (var file in Directory.EnumerateFiles(_corpusRoot, pattern, SearchOption.AllDirectories))
      if (!file.Contains(".git") && !file.Contains(".TEMPLATE.", StringComparison.OrdinalIgnoreCase))
        yield return file; // .TEMPLATE. files hold generator placeholders, not PB source
  }

  [Test]
  public void Tokenize_GivenSvgaLibraryCorpus_WhenLexed_ThenEveryFileTokenizesWithoutError() {
    var files = CorpusFiles().ToList();
    Assume.That(files, Is.Not.Empty, "PB-SvgaLibrary checkout not found - corpus smoke skipped");

    var failures = new List<string>();
    var total = 0;
    foreach (var file in files)
      try {
        total += Lexer.Tokenize(File.ReadAllText(file), Path.GetFileName(file)).Count();
      } catch (LexerException e) {
        failures.Add($"{Path.GetFileName(file)}: {e.Message}");
      }

    Assert.That(failures, Is.Empty, string.Join("\n", failures));
    TestContext.Out.WriteLine($"{files.Count} files, {total} tokens");
  }
}
