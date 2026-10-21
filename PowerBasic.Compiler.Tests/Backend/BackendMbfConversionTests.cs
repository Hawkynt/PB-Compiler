using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbfConversionTests {

  private static readonly object[] _dialectsAndModes = [
    new object[] { Dialect.Basica, false },
    new object[] { Dialect.Basica, true },
    new object[] { Dialect.Gw, false },
    new object[] { Dialect.Gw, true },
  ];

  private static string? RunFile(string source, string file, Dialect dialect, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image, exactFloatingPoint: true).FileContent(file);
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Narrow_GivenHalfwayDoubleValues_ThenRoundsAwayFromZero(Dialect dialect, bool optimize) {
    // Complete CNVTIES.TXT captured from genuine GW-BASIC 3.23. Conversion is not SINGLE addition.
    var actual = RunFile("""
      10 OPEN "CNVTIES.TXT" FOR OUTPUT AS #1
      20 X#=0:Y!=0:P%=VARPTR(X#):Q%=VARPTR(Y!)
      30 FOR S%=0 TO 128 STEP 128
      40 FOR E%=0 TO 1
      50 FOR I%=0 TO 7:POKE P%+I%,0:NEXT I%
      60 POKE P%+3,128:POKE P%+4,E%:POKE P%+6,S%:POKE P%+7,129
      70 Y!=X#:GOSUB 500
      80 Y!=CSNG(X#):GOSUB 500
      90 NEXT E%:NEXT S%
      100 X#=1D0+2D0^(-24):Y!=X#:GOSUB 500
      110 Y!=1D0+2D0^(-24):GOSUB 500
      120 CLOSE #1:SYSTEM
      500 FOR I%=0 TO 3:PRINT #1,PEEK(Q%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """, "CNVTIES.TXT", dialect, optimize);
    var expected = string.Concat(Enumerable.Repeat(" 1  0  0  129 \r\n", 2))
      + string.Concat(Enumerable.Repeat(" 2  0  0  129 \r\n", 2))
      + string.Concat(Enumerable.Repeat(" 1  0  128  129 \r\n", 2))
      + string.Concat(Enumerable.Repeat(" 2  0  128  129 \r\n", 2))
      + string.Concat(Enumerable.Repeat(" 1  0  0  129 \r\n", 2)) + "\u001a";
    Assert.That(actual, Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Narrow_GivenTieNeighborsAndMantissaCarry_ThenMatchesGwBasicFile(Dialect dialect, bool optimize) {
    // Complete CNVEDGE.TXT captured from genuine GW-BASIC 3.23.
    var actual = RunFile("""
      10 OPEN "CNVEDGE.TXT" FOR OUTPUT AS #1
      20 X#=1D0+2D0^(-24)-2D0^(-55):Y!=X#:GOSUB 500
      30 X#=1D0+2D0^(-24)+2D0^(-55):Y!=X#:GOSUB 500
      40 X#=2D0-2D0^(-24):Y!=X#:GOSUB 500
      50 X#=-2D0+2D0^(-24):Y!=X#:GOSUB 500
      60 CLOSE #1:SYSTEM
      500 P%=VARPTR(Y!)
      510 FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      520 RETURN
      """, "CNVEDGE.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(" 0  0  0  129 \r\n 1  0  0  129 \r\n"
      + " 0  0  0  130 \r\n 0  0  128  130 \r\n\u001a"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Parse_GivenHalfwaySingleLiterals_ThenMatchesGwBasicConversion(Dialect dialect, bool optimize) {
    // Complete SGLHALF.TXT captured from genuine GW-BASIC 3.23.
    var actual = RunFile("""
      10 OPEN "SGLHALF.TXT" FOR OUTPUT AS #1
      20 X!=1.000001E8:GOSUB 500
      30 X!=1.000001D8:GOSUB 500
      40 X!=CSNG(1.000001E8):GOSUB 500
      50 X!=CSNG(1.000001D8):GOSUB 500
      60 X!=-1.000001E8:GOSUB 500
      70 X!=-1.000001D8:GOSUB 500
      80 CLOSE #1:SYSTEM
      500 P%=VARPTR(X!)
      510 FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      520 RETURN
      """, "SGLHALF.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(string.Concat(Enumerable.Repeat(" 45  188  62  155 \r\n", 4))
      + string.Concat(Enumerable.Repeat(" 45  188  190  155 \r\n", 2)) + "\u001a"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Convert_GivenMbfRangeAndPrecisionBoundaries_ThenMatchesGwBasicFile(Dialect dialect, bool optimize) {
    // Complete CNVRANGE.TXT captured from genuine GW-BASIC 3.23.
    var actual = RunFile("""
      10 OPEN "CNVRANGE.TXT" FOR OUTPUT AS #1
      20 X!=CSNG(1D-38):GOSUB 500
      30 X!=CSNG(3D-39):GOSUB 500
      40 X!=CSNG(-1D-38):GOSUB 500
      50 X!=CSNG(1D-40):GOSUB 500
      60 Y#=1.0000000000000001D0:Z#=CDBL(Y#):P%=VARPTR(Z#)
      70 FOR I%=0 TO 7:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      80 ON ERROR GOTO 900
      90 PRINT #1,CSNG(1.7014118D38)
      100 CLOSE #1:SYSTEM
      500 P%=VARPTR(X!)
      510 FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      520 RETURN
      900 PRINT #1,"ERROR";ERR:CLOSE #1:SYSTEM
      """, "CNVRANGE.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(" 221  199  89  2 \r\n 30  171  2  1 \r\n 221  199  217  2 \r\n"
      + " 0  0  0  0 \r\n 4  0  0  0  0  0  0  129 \r\nERROR 6 \r\n\u001a"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Add_GivenHalfwayArithmeticResults_ThenRoundsToEven(Dialect dialect, bool optimize) {
    // Complete MBFTIES.TXT captured from genuine GW-BASIC 3.23.
    var actual = RunFile("""
      10 OPEN "MBFTIES.TXT" FOR OUTPUT AS #1
      20 B#=0D0:P%=VARPTR(B#):POKE P%+7,73
      30 A#=1D0:X#=A#+B#:GOSUB 500
      40 A#=-1D0:X#=A#-B#:GOSUB 500
      50 B!=0!:P%=VARPTR(B!):POKE P%+3,105
      60 A!=1!:X!=A!+B!:GOSUB 600
      70 A!=-1!:X!=A!-B!:GOSUB 600
      80 CLOSE #1:SYSTEM
      500 P%=VARPTR(X#)
      510 PRINT #1,PEEK(P%);PEEK(P%+6);PEEK(P%+7)
      520 RETURN
      600 P%=VARPTR(X!)
      610 PRINT #1,PEEK(P%);PEEK(P%+2);PEEK(P%+3)
      620 RETURN
      """, "MBFTIES.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(string.Concat(Enumerable.Repeat(" 0  0  129 \r\n 0  128  129 \r\n", 2))
      + "\u001a"));
  }
}
