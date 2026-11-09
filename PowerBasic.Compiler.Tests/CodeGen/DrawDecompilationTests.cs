using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Compare the newly desugared DRAW state/paint commands against the original
/// runtime DRAW interpreter. This exercises cross-command cursor, scale,
/// rotation, colour and fill state rather than assuming lines are independent.
/// </summary>
[TestFixture]
public sealed class DrawDecompilationTests {
  private static Cpu8086 Execute(string source, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "DE-DRAW.BAS", Dialect.Qb45),
      "DE-DRAW.BAS", Dialect.Qb45), Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("M112,100")]
  [TestCase("S4 A0 R10 D4 L10")]
  [TestCase("S4 TA0 N R10 B D4")]
  [TestCase("S4 A0 M+10,+4")]
  [TestCase("S8 A0 C9 R7 D4 L7 U4")]
  [TestCase("C9 R7 B D2 P6,9")]
  [TestCase("C9 R7 D7 L7 U7 B R2 D2 P6,9")]
  [TestCase("A1 S8 R7 D4 A0 S4 R4")]
  [TestCase("TA-90 R7 TA360 D4")]
  [TestCase("TA180 N R8 B R4 U3")]
  [TestCase("C9 P6,9 C3 R8")]
  public void Execute_GivenConstantDrawCommands_ThenMatchesRuntimeInterpreter(string commands) {
    var source = $"""
      SCREEN 13
      LINE (70,70)-(145,138),9,B
      PSET (100,100),0
      DRAW "{commands}"
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      FOR y%=88 TO 120
        FOR x%=88 TO 133
          PRINT #1,POINT(x%,y%);
        NEXT
      NEXT
      CLOSE #1
      SCREEN 0
      END
      """;
    var baseline = Execute(source, false);
    var optimized = Execute(source, true);
    Assert.Multiple(() => {
      Assert.That(optimized.FileBytes("RESULT.TXT"), Is.EqualTo(baseline.FileBytes("RESULT.TXT")),
        "fully constant DRAW must have identical framebuffer output");
      Assert.That(optimized.VideoBiosCalls, Is.LessThanOrEqualTo(baseline.VideoBiosCalls));
    });
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenRepeatedConstantDraw_ThenEliminatesRuntimeParsingInstructions() {
    const string source = """
      SCREEN 13
      PSET (100,100),0
      FOR i%=1 TO 80
        DRAW "S4 A0 C9 R5 L5"
      NEXT
      PRINT POINT(100,100);POINT(105,100);POINT(106,100)
      END
      """;
    var baseline = Execute(source, false);
    var optimized = Execute(source, true);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions));
    });
    TestContext.Progress.WriteLine(
      $"80 constant DRAW strings: optimized {optimized.ExecutedInstructions} dispatches, "
      + $"{optimized.FetchedInstructionBytes} fetched instruction bytes; "
      + $"interpreter {baseline.ExecutedInstructions} dispatches, "
      + $"{baseline.FetchedInstructionBytes} fetched instruction bytes");
  }

  [TestCase("S8 A1 R5;")]
  [TestCase("TA-90 C9 R5;")]
  [TestCase("C9 P6,9 R5;")]
  public void Execute_GivenKnownPrefixAndUnknownTail_ThenPreservesRuntimeStateAndCursor(string prefix) {
    var source = $"""
      SCREEN 13
      LINE (70,70)-(145,138),9,B
      tail$="D3 R2"
      PSET (100,100),0
      DRAW "{prefix}"+tail$
      OPEN "RESULT.TXT" FOR OUTPUT AS #1
      FOR y%=80 TO 120
        FOR x%=85 TO 130
          PRINT #1,POINT(x%,y%);
        NEXT
      NEXT
      CLOSE #1
      SCREEN 0
      END
      """;
    var baseline = Execute(source, false);
    var optimized = Execute(source, true);
    Assert.That(optimized.FileBytes("RESULT.TXT"), Is.EqualTo(baseline.FileBytes("RESULT.TXT")));
  }
}
