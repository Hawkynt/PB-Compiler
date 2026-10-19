using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbf32BoundsTests {
  private static readonly object[] _dialectsAndModes = [
    new object[] { Dialect.Basica, false },
    new object[] { Dialect.Basica, true },
    new object[] { Dialect.Gw, false },
    new object[] { Dialect.Gw, true },
  ];

  private static string Run(string source, Dialect dialect, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the MBF32 body fell back");
    return string.Join('|', Cpu8086.Run(image, exactFloatingPoint: true).Output
      .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenSingleLiteralsBelowIeeeNormalRange_ThenMatchesGwBasicBytes(
      Dialect dialect, bool optimize) {
    // Complete SGLIT.TXT captured from genuine GW-BASIC 3.23. E literals are SINGLE at the source
    // boundary; narrowing them through IEEE subnormals discards bits that MBF32 can represent.
    const string source = """
      10 OPEN "SGLIT.TXT" FOR OUTPUT AS #1
      20 X!=1E-38:GOSUB 500
      30 X!=3E-39:GOSUB 500
      40 X!=-1E-38:GOSUB 500
      50 X!=9E-39:GOSUB 500
      60 CLOSE #1:SYSTEM
      500 P%=VARPTR(X!)
      510 PRINT #1,PEEK(P%);PEEK(P%+1);PEEK(P%+2);PEEK(P%+3)
      520 RETURN
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "SGLIT.BAS", dialect), "SGLIT.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    var actual = Cpu8086.Run(image, exactFloatingPoint: true).FileContent("SGLIT.TXT");
    Assert.That(actual, Is.EqualTo(" 221  199  89  2 \r\n 30  171  2  1 \r\n"
      + " 221  199  217  2 \r\n 173  0  68  2 \r\n\u001a"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Widen_GivenTinySingleAndDoubleLiterals_ThenPreservesTheirDistinctSourcePrecisions(
      Dialect dialect, bool optimize) {
    // Complete SGLWID.TXT captured from genuine GW-BASIC 3.23. Widening an E literal preserves its
    // 24-bit value, while the corresponding D literal retains MBF64's 56-bit value.
    const string source = """
      10 OPEN "SGLWID.TXT" FOR OUTPUT AS #1
      20 X#=1E-38:GOSUB 500
      30 X#=1D-38:GOSUB 500
      40 X#=3E-39:GOSUB 500
      50 X#=3D-39:GOSUB 500
      60 X#=1E-16:GOSUB 500
      70 X#=1D-16:GOSUB 500
      80 CLOSE #1:SYSTEM
      500 P%=VARPTR(X#)
      510 PRINT #1,PEEK(P%);PEEK(P%+1);PEEK(P%+2);PEEK(P%+3);PEEK(P%+4);PEEK(P%+5);PEEK(P%+6);PEEK(P%+7)
      520 RETURN
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "SGLWID.BAS", dialect), "SGLWID.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    var actual = Cpu8086.Run(image, exactFloatingPoint: true).FileContent("SGLWID.TXT");
    var expected = " 0  0  0  0  221  199  89  2 \r\n 34  199  83  237  220  199  89  2 \r\n"
      + " 0  0  0  0  30  171  2  1 \r\n 21  17  255  39  30  171  2  1 \r\n"
      + " 0  0  0  0  149  149  102  75 \r\n 225  77  196  190  148  149  102  75 \r\n\u001a";
    Assert.That(actual, Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Load_GivenTinyStoredMbf32_ThenWidensWithoutLosingItsSignificand(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X!=1D-38:Y#=X!
      20 P%=VARPTR(Y#)
      30 FOR I%=0 TO 7:PRINT PEEK(P%+I%):NEXT I%
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("0|0|0|0|221|199|89|2"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Load_GivenEveryNonzeroMbf32Exponent_ThenWidensBothSignsExactly(Dialect dialect, bool optimize) {
    // GW-BASIC 3.23's SWPNZ.TXT reports zero mismatches for the same raw-cell sweep. Exponent-zero
    // cells with stray mantissa bits are a separate representation case, not part of this sweep.
    var output = Run("""
      10 X!=0:Y#=0:P%=VARPTR(X!):Q%=VARPTR(Y#):DIM V%(7):F%=0:N%=0
      20 FOR S%=0 TO 128 STEP 128
      30 FOR E%=1 TO 255
      40 POKE P%,91:POKE P%+1,167:POKE P%+2,45+S%:POKE P%+3,E%
      50 FOR I%=0 TO 7:V%(I%)=0:NEXT I%
      60 V%(4)=91:V%(5)=167:V%(6)=45+S%:V%(7)=E%
      70 Y#=X!
      75 N%=N%+1
      80 FOR I%=0 TO 7:IF PEEK(Q%+I%)<>V%(I%) THEN F%=F%+1
      90 NEXT I%
      100 NEXT E%:NEXT S%
      110 PRINT N%:PRINT F%
      120 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("510|0"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Load_GivenARawMinimumExponentCell_ThenWidensEveryBit(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X!=0:Y#=0:P%=VARPTR(X!):Q%=VARPTR(Y#)
      20 POKE P%,91:POKE P%+1,167:POKE P%+2,45:POKE P%+3,1
      30 Y#=X!
      40 FOR I%=0 TO 7:PRINT PEEK(Q%+I%):NEXT I%
      50 END
      """, dialect, optimize);
    Assert.That(output, Is.EqualTo("0|0|0|0|91|167|45|1"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueAboveTheMbf32Range_ThenRaisesOverflow(Dialect dialect, bool optimize) {
    var output = Run("""
      10 ON ERROR GOTO 50
      20 X! = 1.7014118D38
      30 PRINT "missed"
      40 END
      50 PRINT ERR
      60 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("6"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenTheSameValueAsDouble_ThenItFitsMbf64(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X# = 1.7014118D38
      20 P% = VARPTR(X#)
      30 PRINT PEEK(P% + 7)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("255"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenARepresentableLargeValue_ThenKeepsTheMbf32Exponent(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D38
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P% + 3)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("255"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAnIeeeSubnormalWithinMbf32Range_ThenKeepsTheMbfBytes(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D-38
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P%)
      40 PRINT PEEK(P% + 1)
      50 PRINT PEEK(P% + 2)
      60 PRINT PEEK(P% + 3)
      70 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("221|199|89|2"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenANegativeIeeeSubnormalWithinMbf32Range_ThenKeepsTheMbfSign(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = -1D-38
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P%)
      40 PRINT PEEK(P% + 1)
      50 PRINT PEEK(P% + 2)
      60 PRINT PEEK(P% + 3)
      70 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("221|199|217|2"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Load_GivenAnMbf32ValueInTheIeeeSubnormalRange_ThenKeepsItsMagnitude(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D-38
      20 IF X! > 0 THEN PRINT "nonzero" ELSE PRINT "zero"
      30 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("nonzero"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenTheSmallestMbf32Magnitude_ThenKeepsTheMinimumExponent(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 2.93873587705572D-39
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P% + 3)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("1"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueJustBelowMbf32Minimum_ThenFlushesBeforeRounding(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 2.9387358D-39
      20 P% = VARPTR(X!)
      30 PRINT PEEK(P% + 3)
      40 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("0"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Store_GivenAValueBelowTheMbf32Range_ThenStoresCanonicalZero(Dialect dialect, bool optimize) {
    var output = Run("""
      10 X! = 1D-40
      20 P% = VARPTR(X!)
      30 S% = 0
      40 FOR I% = 0 TO 3
      50 S% = S% + PEEK(P% + I%)
      60 NEXT I%
      70 PRINT S%
      80 END
      """, dialect, optimize);

    Assert.That(output, Is.EqualTo("0"));
  }
}
