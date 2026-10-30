using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.CodeGen;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class FarByteWideningTests {
  private const string _SOURCE = """
    SCREEN 0
    DEF SEG = &HB800
    DIM value%, wide&
    OPEN "RESULT.TXT" FOR OUTPUT AS #1
    POKE 0,127
    value% = SCREEN(1,1)
    wide& = SCREEN(1,1)
    PRINT #1,"low";SCREEN(1,1);value%;wide&
    POKE 0,128
    value% = SCREEN(1,1)
    wide& = SCREEN(1,1)
    PRINT #1,"high";SCREEN(1,1);value%;wide&
    POKE 0,255
    value% = SCREEN(1,1)
    wide& = SCREEN(1,1)
    PRINT #1,"max";SCREEN(1,1);value%;wide&
    POKE 1,0
    PRINT #1,"attr-zero";SCREEN(1,1,1)
    POKE 1,127
    PRINT #1,"attr-low";SCREEN(1,1,1)
    POKE 1,128
    PRINT #1,"attr-high";SCREEN(1,1,1)
    POKE 1,255
    PRINT #1,"attr-max";SCREEN(1,1,1)
    CLOSE #1
    END
    """;

  // Complete RESULT.TXT captured from genuine QuickBASIC 4.50, BC /O, BCOM45.LIB.
  // Character zero is deliberately excluded: that oracle normalizes it to a space.
  private const string _EXPECTED = "low 127  127  127 \r\nhigh 128  128  128 \r\nmax 255  255  255 \r\n"
    + "attr-zero 0 \r\nattr-low 127 \r\nattr-high 128 \r\nattr-max 255 \r\n";

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Widen_GivenFarByteLoads_WhenSegmentUsesAx_ThenMatchesUnsignedOracle(bool isOptimized, bool isCom) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Qb45),
      "T.BAS", Dialect.Qb45), Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = isOptimized, Container = DosContainer.Exe };
    var image = isCom ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!isCom));
    Assert.That(Cpu8086.Run(image).FileContent("RESULT.TXT"), Is.EqualTo(_EXPECTED));
    Assert.That(DosBoxRunner.RunWithFiles(image, ["RESULT.TXT"]).Files.GetValueOrDefault("RESULT.TXT"),
      Is.EqualTo(_EXPECTED));
  }
}
