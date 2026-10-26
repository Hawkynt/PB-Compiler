using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class DrawRuntimeErrorTests {
  // Original QB45 /O/E/X capture, DOSBox Staging 0.83; fingerprints are in DrawMacroNumericStateTests.
  private const string _RANGE_SOURCE = """
    OPEN "RANGE.TXT" FOR OUTPUT AS #1
    ON ERROR GOTO caught
    FOR probeIndex%=1 TO 10
    READ picture$
    IF probeIndex%=5 THEN SCREEN 13
    IF probeIndex%>=5 THEN PSET (10,20),0:DRAW "A0 S4 C9"
    errnum%=0
    DRAW picture$
    pixel%=0
    IF probeIndex%>=5 THEN pixel%=POINT(12,20)
    PRINT #1,probeIndex%;errnum%;pixel%
    NEXT
    CLOSE #1
    SCREEN 0
    END
    caught:
    errnum%=ERR
    RESUME NEXT
    DATA "","S4","A0","C9","TA361 R2","TA-361 R2","TA360 R2","TA-360 R2","X a$","R5Z"
    """;

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenModeAngleAndGrammarErrors_ThenMatchesOriginalErrorsAndCompletedCommands(
      bool optimize, bool com) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_RANGE_SOURCE, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).FileBytes("RANGE.TXT"), Is.EqualTo(
      (" 1  5  0 \r\n 2  5  0 \r\n 3  5  0 \r\n 4  5  0 \r\n 5  5  0 \r\n"
        + " 6  5  0 \r\n 7  0  9 \r\n 8  0  9 \r\n 9  5  9 \r\n 10  5  9 \r\n")
      .Select(character => (byte)character).ToArray()));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenRepeatedErrorsInACallee_ThenReleasesTheOwnedTextBeforeResuming(bool optimize, bool com) {
    const string source = """
      DECLARE SUB bad (tail$)
      ON ERROR GOTO caught
      READ picture$
      DATA "R1"
      FOR probeIndex%=1 TO 1500
      CALL bad(picture$)
      NEXT
      PRINT count%;wrong%
      END
      caught:
      count%=count%+1
      IF ERR<>5 THEN wrong%=wrong%+1
      RESUME NEXT
      SUB bad(tail$)
      DRAW "C9 R5;"+tail$
      END SUB
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("1500  0"));
  }
}
