using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendEarlyQuickBasicMbfTests {

  private const string _SOURCE = """
    OPEN "NUMERIC.TXT" FOR OUTPUT AS #1
    X!=1.25:P%=VARPTR(X!)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    X#=1.25:P%=VARPTR(X#)
    FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    S$=MKS$(1.25)
    FOR I%=1 TO 4:PRINT #1,ASC(MID$(S$,I%,1));:NEXT I%:PRINT #1,
    S$=MKD$(1.25D0)
    FOR I%=1 TO 8:PRINT #1,ASC(MID$(S$,I%,1));:NEXT I%:PRINT #1,
    S$=CHR$(0)+CHR$(0)+CHR$(32)+CHR$(129):PRINT #1,CVS(S$)
    S$=CHR$(0)+CHR$(0)+CHR$(0)+CHR$(0)+S$:PRINT #1,CVD(S$)
    X#=1.0000000000000001D0:P%=VARPTR(X#)
    FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    X#=1D0+2D0^(-24):Y!=X#:P%=VARPTR(Y!)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    Y!=CSNG(X#)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    X!=1:Y!=2!^(-24):Z!=X!+Y!:P%=VARPTR(Z!)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    Y!=1.000001E8:P%=VARPTR(Y!)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    CLOSE #1
    END
    """;

  private const string _EXPECTED = " 0  0  32  129 \r\n 0  0  0  0  0  0  32  129 \r\n"
    + " 0  0  32  129 \r\n 0  0  0  0  0  0  32  129 \r\n 1.25 \r\n 1.25 \r\n"
    + " 4  0  0  0  0  0  0  129 \r\n 0  0  0  129 \r\n 0  0  0  129 \r\n"
    + " 0  0  0  129 \r\n 44  188  62  155 \r\n\u001a";

  private const string _GW_EXPECTED = " 0  0  32  129 \r\n 0  0  0  0  0  0  32  129 \r\n"
    + " 0  0  32  129 \r\n 0  0  0  0  0  0  32  129 \r\n 1.25 \r\n 1.25 \r\n"
    + " 4  0  0  0  0  0  0  129 \r\n 1  0  0  129 \r\n 1  0  0  129 \r\n"
    + " 0  0  0  129 \r\n 45  188  62  155 \r\n\u001a";

  private const string _BYREF_SOURCE = """
    OPEN "BYREF.TXT" FOR OUTPUT AS #1
    X!=1.25:Y#=1.25D0
    CALL STEPUP(X!,Y#)
    DIM A!(1)
    A!(0)=X!:A!(1)=2.5
    CALL STEPUP(A!(1),Y#)
    P%=VARPTR(X!)
    FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    P%=VARPTR(Y#)
    FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    P%=VARPTR(A!(0))
    FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
    CLOSE #1
    END
    SUB STEPUP(A!,B#) STATIC
    A!=A!+1
    B#=B#+1
    END SUB
    """;

  private const string _BYREF_EXPECTED = " 0  0  16  130 \r\n 0  0  0  0  0  0  80  130 \r\n"
    + " 0  0  16  130  0  0  96  130 \r\n\u001a";

  private static string Render(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return PowerBasic35Emitter.Render(model, unit);
  }

  private static byte[] Compile(string source, Dialect dialect, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    return image;
  }

  [TestCase(Dialect.Basica, false, false)]
  [TestCase(Dialect.Basica, true, false)]
  [TestCase(Dialect.Basica, false, true)]
  [TestCase(Dialect.Basica, true, true)]
  [TestCase(Dialect.Gw, false, false)]
  [TestCase(Dialect.Gw, true, false)]
  [TestCase(Dialect.Gw, false, true)]
  [TestCase(Dialect.Gw, true, true)]
  public void Math_GivenInterpreterDoubleArguments_ThenUsesTheDefaultSinglePrecision(
      Dialect dialect, bool optimize, bool roundTrip) {
    const string source = """
      10 OPEN "MATHGW.TXT" FOR OUTPUT AS #1
      20 X#=1.0000000000000001D0:Y#=SQR(X#):P%=VARPTR(Y#):GOSUB 500
      30 Y#=SIN(X#):GOSUB 500
      40 Y#=LOG(X#):GOSUB 500
      50 CLOSE #1:SYSTEM
      500 FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """;
    var image = roundTrip
      ? Compile(Render(source, dialect), Dialect.Pb35, optimize)
      : Compile(source, dialect, optimize);
    // These rows were captured separately from GW-BASIC 3.23, without its optional /D mode.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("MATHGW.TXT"),
      Is.EqualTo(" 0  0  0  0  0  0  0  129 \r\n 0  0  0  0  165  106  87  128 \r\n"
        + " 0  0  0  0  0  0  0  0 \r\n\u001a"));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Math_GivenMbfOperands_ThenRetainsTheirArgumentAndDisplayPrecision(Dialect dialect, bool optimize) {
    var image = Compile("""
      OPEN "MATHMBF.TXT" FOR OUTPUT AS #1
      X#=1.0000000000000001D0:Y#=SQR(X#):P%=VARPTR(Y!):P%=VARPTR(Y#):GOSUB 500
      Y#=SIN(X#):GOSUB 500
      Y#=LOG(X#):GOSUB 500
      X!=1+2!^(-23)
      PRINT #1,ABS(X!);FIX(X!);INT(X!)
      PRINT #1,STR$(X!)
      X#=.99999999999999997D0
      PRINT #1,FIX(X#);INT(X#)
      CLOSE #1
      END
      500 FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      RETURN
      """, dialect, optimize);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("MATHMBF.TXT"),
      Is.EqualTo(" 2  0  0  0  0  0  0  129 \r\n 116  103  72  120  164  106  87  128 \r\n"
        + " 252  255  255  255  255  255  127  75 \r\n 1  1  1 \r\n 1\r\n 0  0 \r\n\u001a"));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Control_GivenMbfCountersConditionsAndFunctionResults_ThenMatchesEachOriginalCompiler(
      Dialect dialect, bool optimize) {
    var image = Compile("""
      DEF FNID#(A#)=A#
      OPEN "CONTROL.TXT" FOR OUTPUT AS #1
      C%=0
      FOR X!=0 TO 1 STEP .1:C%=C%+1:NEXT X!
      PRINT #1,C%;X!
      C%=0
      FOR Y#=0 TO .3D0 STEP .1D0:C%=C%+1:NEXT Y#
      PRINT #1,C%;Y#
      D#=-.5D0:C%=0
      FOR W#=2 TO 1 STEP D#:C%=C%+1:NEXT W#
      PRINT #1,C%;W#
      FOR Z=1 TO 2:NEXT Z
      PRINT #1,Z
      IF Z THEN PRINT #1,"TRUE"
      Z=0:IF Z THEN PRINT #1,"BAD" ELSE PRINT #1,"FALSE"
      Q#=FNID#(1.0000000000000001D0):P%=VARPTR(Q#)
      FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      CLOSE #1
      END
      """, dialect, optimize);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("CONTROL.TXT"),
      Is.EqualTo(" 10  1 \r\n 4  .4 \r\n 3  .5 \r\n 3 \r\nTRUE\r\nFALSE\r\n"
        + " 4  0  0  0  0  0  0  129 \r\n\u001a"));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void InputRead_GivenMbfVariablesAndArrays_ThenPreservesTheirStorage(Dialect dialect, bool optimize) {
    var image = Compile("""
      DEF FNT#(Q#)=Q#+1D0
      OPEN "INPUT.TXT" FOR OUTPUT AS #2
      PRINT #2,"1.25,2.5"
      CLOSE #2
      OPEN "INPUT.TXT" FOR INPUT AS #2
      INPUT #2,A!,B#
      CLOSE #2
      READ C!,D#
      DIM V!(1)
      RESTORE:READ V!(0),V!(1)
      E#=FNT#(1.25D0)
      OPEN "IOFLOAT.TXT" FOR OUTPUT AS #1
      PRINT #1,A!;B#;C!;D#;V!(0);V!(1);E#
      P%=VARPTR(A!)
      FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      P%=VARPTR(B#)
      FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      CLOSE #1
      END
      DATA 1.25,2.5
      """, dialect, optimize);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("IOFLOAT.TXT"),
      Is.EqualTo(" 1.25  2.5  1.25  2.5  1.25  2.5  2.25 \r\n"
        + " 0  0  32  129 \r\n 0  0  0  0  0  0  32  130 \r\n\u001a"));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Call_GivenMbfScalarsAndArrayElementsByReference_ThenMatchesEachOriginalCompiler(
      Dialect dialect, bool optimize) {
    var image = Compile(_BYREF_SOURCE, dialect, optimize);
    // Complete BYREF.TXT agrees across all three genuine compilers.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("BYREF.TXT"),
      Is.EqualTo(_BYREF_EXPECTED));
  }

  [TestCase(Dialect.Basica, false)]
  [TestCase(Dialect.Basica, true)]
  [TestCase(Dialect.Gw, false)]
  [TestCase(Dialect.Gw, true)]
  public void Decode_GivenAnUndersizedMbfString_ThenRaisesErrorFive(Dialect dialect, bool optimize) {
    var source = """
      10 ON ERROR GOTO 1000
      20 OPEN "CVSHORT.TXT" FOR OUTPUT AS #1
      30 PRINT #1,CVS("")
      40 PRINT #1,CVS("123")
      50 PRINT #1,CVD("1234567")
      60 CLOSE #1:SYSTEM
      1000 PRINT #1,ERR:RESUME NEXT
      """;
    var image = Compile(source, dialect, optimize);
    // Complete CVSHORT.TXT captured from GW-BASIC 3.23.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("CVSHORT.TXT"),
      Is.EqualTo(" 5 \r\n 5 \r\n 5 \r\n\u001a"));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Numeric_GivenEarlyQuickBasicMbfCellsAndConversions_ThenMatchesEachOriginalCompiler(
      Dialect dialect, bool optimize) {
    var image = Compile(_SOURCE, dialect, optimize);
    // Identical complete files captured from BASCOM 1.00, QB 2.00 and default QB 3.00 + BCOM30.LIB.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("NUMERIC.TXT"), Is.EqualTo(_EXPECTED));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Render_GivenAnEarlyQuickBasicMbfProgram_ThenPreservesStorageAndLiteralDigits(
      Dialect dialect, bool optimize) {
    var rendered = Render(_SOURCE, dialect);
    var image = Compile(rendered, Dialect.Pb35, optimize);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("NUMERIC.TXT"), Is.EqualTo(_EXPECTED));
  }

  [TestCase(Dialect.Qb10, false)]
  [TestCase(Dialect.Qb10, true)]
  [TestCase(Dialect.Qb20, false)]
  [TestCase(Dialect.Qb20, true)]
  [TestCase(Dialect.Qb30, false)]
  [TestCase(Dialect.Qb30, true)]
  public void Render_GivenMbfByRefProcedures_ThenPreservesTheirStorageAcrossCalls(Dialect dialect, bool optimize) {
    var image = Compile(Render(_BYREF_SOURCE, dialect), Dialect.Pb35, optimize);
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("BYREF.TXT"), Is.EqualTo(_BYREF_EXPECTED));
  }

  [TestCase(Dialect.Basica, false, false)]
  [TestCase(Dialect.Basica, true, false)]
  [TestCase(Dialect.Basica, false, true)]
  [TestCase(Dialect.Basica, true, true)]
  [TestCase(Dialect.Gw, false, false)]
  [TestCase(Dialect.Gw, true, false)]
  [TestCase(Dialect.Gw, false, true)]
  [TestCase(Dialect.Gw, true, true)]
  public void Numeric_GivenInterpreterConversionTies_ThenKeepsThemDistinctFromQuickBasic(
      Dialect dialect, bool optimize, bool roundTrip) {
    var source = string.Join('\n', _SOURCE.Split('\n').Select((line, i) =>
      $"{(i + 1) * 10} {(line == "END" ? "SYSTEM" : line)}"));
    var image = roundTrip
      ? Compile(Render(source, dialect), Dialect.Pb35, optimize)
      : Compile(source, dialect, optimize);
    // NUMERIC.TXT captured separately from genuine GW-BASIC: narrowing and literal ties go away.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("NUMERIC.TXT"), Is.EqualTo(_GW_EXPECTED));
  }
}
