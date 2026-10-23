using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendFloatingNegationTests {

  // Complete NEGZERO.TXT captured from QB 4.50 BC.EXE + BCOM45.LIB under DOSBox Staging 0.83.
  // BC.EXE SHA-256: DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA
  // BCOM45.LIB SHA-256: 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996
  private const string _SOURCE = """
    OPEN "NEGZERO.TXT" FOR OUTPUT AS #1
    X# = 0D0
    Y# = -0D0
    P% = VARPTR(Y#)
    FOR I% = 0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    CALL FLIP(X#, Y#)
    FOR I% = 0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    X# = CVD(STRING$(7, 0) + CHR$(128))
    CALL FLIP(X#, Y#)
    FOR I% = 0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    CLOSE #1
    END
    SUB FLIP(A#, B#) STATIC
      B# = -A#
    END SUB
    """;

  private const string _EXPECTED = " 0  0  0  0  0  0  0  128 \r\n"
    + " 0  0  0  0  0  0  0  128 \r\n 0  0  0  0  0  0  0  0 \r\n";

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Negate_GivenReducedArithmeticPrecision_ThenDoesNotRoundTheOperand(bool optimize, bool software) {
    var source = "$COMPILE EXE\n" + (software ? "$FLOAT EMULATE\n" : "$FLOAT NPX\n") + """
      DIM cw AS WORD, saved AS WORD
      DIM x AS DOUBLE, y AS DOUBLE
      x = CVD(CHR$(1) + STRING$(5, 0) + CHR$(240) + CHR$(63))
      ! FNSTCW saved
      cw = &H007F
      ! FLDCW cw
      y = -x
      ! FLDCW saved
      OPEN "NEGATION.BIN" FOR OUTPUT AS #1
      PRINT #1, MKD$(y);
      CLOSE #1
      END
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileBytes("NEGATION.BIN"),
      Is.EqualTo(new byte[] { 1, 0, 0, 0, 0, 0, 240, 191 }));
  }

  [TestCaseSource(typeof(FlatTargets), nameof(FlatTargets.Platforms))]
  public void Negate_GivenSignedZero_WhenRetargeted_ThenFlipsItsStoredSign(string platform) {
    const string source = """
      A# = 0#
      CALL FLIP(A#, B#)
      PRINT ASC(RIGHT$(MKD$(B#), 1))
      A# = CVD(STRING$(7, 0) + CHR$(128))
      CALL FLIP(A#, B#)
      PRINT ASC(RIGHT$(MKD$(B#), 1))
      END
      SUB FLIP(X#, Y#) STATIC
        Y# = -X#
      END SUB
      """;
    var output = FlatTargets.Run(platform, source);
    Assert.That(string.Join("\n", output.Replace("\r", "").Trim().Split('\n').Select(line => line.Trim())),
      Is.EqualTo("128\n0"));
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Negate_GivenSignedZero_ThenMatchesGenuineQuickBasic45(bool optimize, bool com) {
    var unit = Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45);
    var model = Binder.Bind(unit, Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("NEGZERO.TXT"), Is.EqualTo(_EXPECTED));
  }
}
