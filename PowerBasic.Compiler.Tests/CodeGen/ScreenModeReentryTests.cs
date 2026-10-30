using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
[NonParallelizable]
public sealed class ScreenModeReentryTests {
  private static IEnumerable<TestCaseData> GetConfigurations() {
    foreach (var dialect in new[] { Dialect.Pb36, Dialect.Qb45, Dialect.Gw })
      foreach (var isOptimized in new[] { false, true })
        foreach (var isCom in new[] { false, true })
          yield return new TestCaseData(dialect, isOptimized, isCom);
  }

  private static (string Source, string Expected) CreateProgram(Dialect dialect) {
    int[] modes = dialect == Dialect.Gw ? [1, 2] : [1, 2, 7, 8, 9, 11, 12, 13];
    var lines = new List<string> { "OPEN \"RESULT.TXT\" FOR OUTPUT AS #1" };
    var expected = new StringBuilder();
    foreach (var mode in modes) {
      lines.AddRange([
        $"SCREEN {mode}", "PSET (10,20),1", "PRINT #1,\"before\";POINT(10,20)",
        $"SCREEN {mode}", "PRINT #1,\"same\";POINT(10,20)",
        "SCREEN 0", $"SCREEN {mode}", "PRINT #1,\"changed\";POINT(10,20)",
      ]);
      expected.Append("before 1 \r\nsame 1 \r\nchanged 0 \r\n");
    }
    // POKE avoids stdout redirection: this character must actually occupy video memory.
    lines.AddRange([
      "SCREEN 0", "DEF SEG = &HB800", "POKE 0,90", "PRINT #1,\"text-before\";PEEK(0)",
      "SCREEN 0", "PRINT #1,\"text-same\";PEEK(0)", "CLOSE #1", "END",
    ]);
    expected.Append("text-before 90 \r\ntext-same 90 \r\n");
    if (dialect == Dialect.Gw)
      expected.Append('\x1A');
    var source = string.Join("\r\n", lines.Select((line, index) =>
      dialect == Dialect.Gw ? $"{(index + 1) * 10} {line}" : line));
    return (source, expected.ToString());
  }

  [TestCaseSource(nameof(GetConfigurations))]
  public void Execute_GivenCurrentScreenMode_ThenPreservesPixelsAndTextButChangesStillClear(
      Dialect dialect, bool isOptimized, bool isCom) {
    // Genuine QB 4.50 and GW-BASIC 3.23 preserve pixels on same-mode reentry;
    // Borland Turbo BASIC Owner's Handbook (1987), p. 333, specifies the same no-op.
    var program = CreateProgram(dialect);
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(program.Source, "T.BAS", dialect), "T.BAS", dialect),
      dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = isOptimized, Container = DosContainer.Exe };
    var image = isCom ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!isCom));
    var result = DosBoxRunner.RunWithFiles(image, ["RESULT.TXT"]);
    Assert.That(result.Files.GetValueOrDefault("RESULT.TXT"), Is.EqualTo(program.Expected),
      result.Files.GetValueOrDefault("RESULT.TXT"));
  }
}
