using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbfCopyTests {

  private static readonly object[] _dialectsAndModes = [
    new object[] { Dialect.Basica, false },
    new object[] { Dialect.Basica, true },
    new object[] { Dialect.Gw, false },
    new object[] { Dialect.Gw, true },
  ];

  private static string RunFile(string source, string file, Dialect dialect, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    var output = Cpu8086.Run(image, exactFloatingPoint: true).FileContent(file);
    Assert.That(output, Is.Not.Null, "the program did not create " + file);
    return output!;
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Copy_GivenZeroExponentSingleCells_ThenPreservesTheirRawBytes(Dialect dialect, bool optimize) {
    // Rows from genuine GW-BASIC 3.23's RAWZERO.TXT, excluding its separate narrowing operation.
    var actual = RunFile("""
      10 OPEN "RAWZERO.TXT" FOR OUTPUT AS #1
      20 X!=0:A!=0:Y#=0:Z#=0:P%=VARPTR(X!)
      30 FOR S%=0 TO 128 STEP 128
      40 POKE P%,91:POKE P%+1,167:POKE P%+2,45+S%:POKE P%+3,0
      50 A!=X!:Q%=VARPTR(A!):N%=3:GOSUB 500
      60 Y#=X!:Q%=VARPTR(Y#):N%=7:GOSUB 500
      70 Z#=Y#:Q%=VARPTR(Z#):N%=7:GOSUB 500
      80 NEXT S%
      90 CLOSE #1:SYSTEM
      500 FOR I%=0 TO N%:PRINT #1,PEEK(Q%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """, "RAWZERO.TXT", dialect, optimize);
    var expected = " 91  167  45  0 \r\n"
      + string.Concat(Enumerable.Repeat(" 0  0  0  0  91  167  45  0 \r\n", 2))
      + " 91  167  173  0 \r\n"
      + string.Concat(Enumerable.Repeat(" 0  0  0  0  91  167  173  0 \r\n", 2)) + "\u001a";
    Assert.That(actual, Is.EqualTo(expected));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Copy_GivenZeroExponentDoubleCells_ThenPreservesTheirLowBits(Dialect dialect, bool optimize) {
    // The same-width rows of GW-BASIC 3.23's RAW64Z.TXT retain all seven significand/sign bytes.
    var actual = RunFile("""
      10 OPEN "RAW64Z.TXT" FOR OUTPUT AS #1
      20 X#=0:Y#=0:P%=VARPTR(X#)
      30 FOR S%=0 TO 128 STEP 128
      40 FOR I%=0 TO 3:POKE P%+I%,255:NEXT I%
      50 POKE P%+4,91:POKE P%+5,167:POKE P%+6,45+S%:POKE P%+7,0
      60 Y#=X#:Q%=VARPTR(Y#):GOSUB 500
      70 NEXT S%
      80 CLOSE #1:SYSTEM
      500 FOR I%=0 TO 7:PRINT #1,PEEK(Q%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """, "RAW64Z.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(" 255  255  255  255  91  167  45  0 \r\n"
      + " 255  255  255  255  91  167  173  0 \r\n\u001a"));
  }

  [TestCaseSource(nameof(_dialectsAndModes))]
  public void Copy_GivenStaticArrayCellsAndSelfAssignments_ThenPreservesTheirRawBytes(
      Dialect dialect, bool optimize) {
    // Complete RAWARR.TXT captured from genuine GW-BASIC 3.23.
    var actual = RunFile("""
      10 OPEN "RAWARR.TXT" FOR OUTPUT AS #1
      20 P%=0:S%=0:Q%=0:N%=0:I%=0:DIM A!(2),B#(2):P%=VARPTR(A!(0))
      30 FOR S%=0 TO 128 STEP 128
      40 POKE P%,91:POKE P%+1,167:POKE P%+2,45+S%:POKE P%+3,0
      50 A!(1)=A!(0):A!(1)=A!(1)
      60 Q%=VARPTR(A!(1)):N%=3:GOSUB 500
      70 B#(1)=A!(1):B#(2)=B#(1):B#(2)=B#(2)
      80 Q%=VARPTR(B#(2)):N%=7:GOSUB 500
      90 NEXT S%
      100 CLOSE #1:SYSTEM
      500 FOR I%=0 TO N%:PRINT #1,PEEK(Q%+I%);:NEXT I%:PRINT #1,
      510 RETURN
      """, "RAWARR.TXT", dialect, optimize);
    Assert.That(actual, Is.EqualTo(" 91  167  45  0 \r\n 0  0  0  0  91  167  45  0 \r\n"
      + " 91  167  173  0 \r\n 0  0  0  0  91  167  173  0 \r\n\u001a"));
  }
}
