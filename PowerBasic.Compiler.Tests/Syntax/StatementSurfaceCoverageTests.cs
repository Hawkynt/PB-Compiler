using System.Text;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Whether the statement surface really is the whole statement surface.
///
/// <see cref="StatementSurface"/> is hand-written, and a hand-written list of "every statement" is
/// exactly the kind of thing that is complete on the day it is written and quietly stops being so.
/// The parser already knows the answer - <see cref="Parser.StatementKeywords"/> is the set of words
/// that can begin a statement - so the two are compared instead of trusted, and a keyword the parser
/// gained without anyone writing a form for it fails here rather than going untested.
/// </summary>
[TestFixture]
public sealed class StatementSurfaceCoverageTests {

  /// <summary>
  /// Keywords that begin no statement of their own and so cannot have a form.
  ///
  /// The block closers are the interesting group: NEXT, LOOP, WEND, CASE, ELSE and ELSEIF all appear
  /// in the parser's set so that a label may not be spelled with one of them, and each is dispatched
  /// only to raise "NEXT without FOR". They are covered by the forms of the statements they close.
  /// The rest are clause words - TO, AS and the like - reached only inside another statement.
  /// </summary>
  private static readonly HashSet<string> _notStatementsOfTheirOwn = new(StringComparer.OrdinalIgnoreCase) {
    "NEXT", "LOOP", "WEND", "CASE", "ELSE", "ELSEIF",
  };

  [Test]
  public void Surface_GivenTheParsersKeywords_ThenEveryOneIsExercisedByAForm() {
    // A keyword counts as exercised only when it OPENS a statement - the first word of some line of
    // some form. The looser reading, "appears as a word anywhere in a form", passes for the wrong
    // reason: SHARED is not tested by `DIM x AS SHARED INTEGER`, nor USING by `PRINT USING`, nor BASE
    // by `OPTION BASE 1`, yet each of those would count it covered. Mentioning a word proves the
    // lexer produced a token; only opening a statement proves the parser dispatches on it.
    var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var form in StatementSurface.All)
      foreach (var line in (form.Preamble + "\n" + form.Body).Split('\n'))
        if (Words(line).FirstOrDefault() is { } opener)
          covered.Add(opener);

    var missing = Parser.StatementKeywords
      .Where(k => !_notStatementsOfTheirOwn.Contains(k) && !covered.Contains(k))
      .OrderBy(k => k, StringComparer.Ordinal)
      .ToList();

    var report = new StringBuilder()
      .AppendLine($"statement keywords: {Parser.StatementKeywords.Count}, exercised by a surface form: "
        + $"{Parser.StatementKeywords.Count - missing.Count - _notStatementsOfTheirOwn.Count}")
      .AppendLine($"not statements of their own (block closers): {string.Join(", ", _notStatementsOfTheirOwn.OrderBy(k => k, StringComparer.Ordinal))}");
    foreach (var keyword in missing)
      report.AppendLine($"  UNTESTED  {keyword}");
    TestContext.Out.Write(report.ToString());

    Assert.That(missing, Is.Empty,
      "the parser dispatches statements the surface table has no form for:\n" + report);
  }

  /// <summary>Splits BASIC source into the bare identifier words it contains.</summary>
  private static IEnumerable<string> Words(string source) {
    var word = new StringBuilder();
    foreach (var c in source + " ") {
      if (char.IsLetter(c) || c == '_' || (word.Length > 0 && char.IsDigit(c))) {
        word.Append(c);
        continue;
      }
      if (word.Length > 0)
        yield return word.ToString();
      word.Clear();
    }
  }
}
