using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendExtendedAbsTests {

  private const string _SOURCE = """
    DIM x AS EXT
    DIM y AS EXT
    p% = VARPTR(x)
    FOR s% = 0 TO 128 STEP 128
      FOR n% = 0 TO 4
        FOR i% = 0 TO 9: POKE p% + i%, 0: NEXT i%
        h% = 0
        SELECT CASE n%
          CASE 1
            POKE p% + 7, 128: POKE p% + 8, 1
          CASE 2
            POKE p%, 1: POKE p% + 7, 128: POKE p% + 8, 255: h% = 63
          CASE 3
            POKE p%, 1: POKE p% + 7, 128: POKE p% + 8, 254: h% = 127
          CASE 4
            POKE p%, 1
        END SELECT
        POKE p% + 9, h% + s%
        y = ABS(x)
        q% = VARPTR(y)
        FOR i% = 0 TO 9: PRINT PEEK(q% + i%): NEXT i%
      NEXT n%
    NEXT s%
    END
    """;

  private const string _EXPECTED = "0|0|0|0|0|0|0|0|0|0"
    + "|0|0|0|0|0|0|0|128|1|0"
    + "|1|0|0|0|0|0|0|128|255|63"
    + "|1|0|0|0|0|0|0|128|254|127"
    + "|1|0|0|0|0|0|0|0|0|0";

  private static IEnumerable<object[]> GetDialectModes() {
    var dialects = new[] { Dialect.Tb10, Dialect.Tb11, Dialect.Pb20, Dialect.Pb21, Dialect.Pb30,
      Dialect.Pb31, Dialect.Pb32, Dialect.Pb35, Dialect.Pb36 };
    foreach (var dialect in dialects)
      foreach (var optimize in new[] { false, true })
        yield return [dialect, optimize];
  }

  private static SemanticModel Bind(string source, Dialect dialect) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  private static byte[] Compile(string source, Dialect dialect, bool optimize) {
    var generator = new CodeGenerator(Bind(source, dialect)) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    Assert.That(image.Length, Is.GreaterThan(2));
    Assert.That(image[0] == 'M' && image[1] == 'Z', Is.EqualTo(!optimize),
      "these standalone sources exercise MZ without optimization and automatic COM with optimization");
    return image;
  }

  private static void RunAndCheck(string source, Dialect dialect, bool optimize, string expected) {
    var image = Compile(source, dialect, optimize);
    var output = string.Join('|', Cpu8086.Run(image, exactFloatingPoint: true).Output
      .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
    Assert.That(output, Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(GetDialectModes))]
  public void Abs_GivenExtendedSignRangeAndLowBits_ThenPreservesEveryOtherBit(Dialect dialect, bool optimize) =>
    RunAndCheck(_SOURCE, dialect, optimize, _EXPECTED + "|" + _EXPECTED);

  [TestCase("SPEED")]
  [TestCase("SIZE")]
  public void Abs_GivenAnOptimizationObjective_ThenPreservesExtendedBits(string objective) =>
    RunAndCheck("$OPTIMIZE " + objective + "\n" + _SOURCE, Dialect.Pb36, true, _EXPECTED + "|" + _EXPECTED);

  [TestCase(false)]
  [TestCase(true)]
  public void Abs_GivenTwoExtendedResultsInOneExpression_ThenKeepsX87ValuesOrdered(bool optimize) =>
    RunAndCheck("""
      $OPTIMIZE SPEED
      DIM x AS EXT
      DIM y AS EXT
      p% = VARPTR(x)
      FOR i% = 0 TO 9: POKE p% + i%, 0: NEXT i%
      POKE p%, 1: POKE p% + 7, 128: POKE p% + 8, 255: POKE p% + 9, 191
      y = ABS(x) + ABS(x)
      q% = VARPTR(y)
      PRINT PEEK(q%)
      PRINT PEEK(q% + 7)
      PRINT PEEK(q% + 8)
      PRINT PEEK(q% + 9)
      END
      """, Dialect.Pb35, optimize, "1|128|0|64");

  [TestCase(Dialect.Basica, false)]
  [TestCase(Dialect.Basica, true)]
  [TestCase(Dialect.Gw, false)]
  [TestCase(Dialect.Gw, true)]
  public void Abs_GivenCanonicalMbfValues_ThenMatchesGwBasicBytes(Dialect dialect, bool optimize) {
    // Complete MBFABS.TXT captured from genuine GW-BASIC 3.23; raw noncanonical zeros are separate.
    var image = Compile("""
      10 OPEN "MBFABS.TXT" FOR OUTPUT AS #1
      20 X!=-1D-38:Y!=ABS(X!):P%=VARPTR(Y!):N%=3:GOSUB 500
      30 X!=-3D-39:Y!=ABS(X!):P%=VARPTR(Y!):N%=3:GOSUB 500
      40 X#=-1.0000000000000001D0:Y#=ABS(X#):P%=VARPTR(Y#):N%=7:GOSUB 500
      50 X#=-1D38:Y#=ABS(X#):P%=VARPTR(Y#):N%=7:GOSUB 500
      60 X!=0:Y!=ABS(X!):P%=VARPTR(Y!):N%=3:GOSUB 500
      61 Y!=ABS(-1E-38):P%=VARPTR(Y!):N%=3:GOSUB 500
      62 Y#=ABS(-1.0000000000000001D0):P%=VARPTR(Y#):N%=7:GOSUB 500
      63 X!=-3D-39:Y!=ABS(X!+0!):P%=VARPTR(Y!):N%=3:GOSUB 500
      64 X#=-1.0000000000000001D0:Y#=ABS(X#+0D0):P%=VARPTR(Y#):N%=7:GOSUB 500
      70 CLOSE #1:SYSTEM
      500 FOR I%=0 TO N%:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """, dialect, optimize);
    var actual = Cpu8086.Run(image, exactFloatingPoint: true).FileContent("MBFABS.TXT");
    Assert.That(actual, Is.EqualTo(" 221  199  89  2 \r\n 30  171  2  1 \r\n"
      + " 4  0  0  0  0  0  0  129 \r\n 137  13  181  80  153  118  22  255 \r\n"
      + " 0  0  0  0 \r\n 221  199  89  2 \r\n 4  0  0  0  0  0  0  129 \r\n"
      + " 30  171  2  1 \r\n 4  0  0  0  0  0  0  129 \r\n\u001a"));
  }

  [Test]
  public void Lower_GivenExtendedAbs_ThenKeepsTheOperationAndItsWidthInVerifiedIr() {
    var module = IrLowering.TryLowerModule(Bind(_SOURCE, Dialect.Pb35), out var reason);
    Assert.That(module, Is.Not.Null, reason);
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    Assert.That(LlvmEmitter.Emit(module!), Does.Contain("@llvm.fabs.f80"));
    Assert.That(CEmitter.Emit(module!), Does.Contain("fabsl("));
  }
}
