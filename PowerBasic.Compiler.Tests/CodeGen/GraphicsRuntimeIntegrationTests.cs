using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class GraphicsRuntimeIntegrationTests {
  // SPANS.TXT captured with QB45 BC /O and BCOM45.LIB under DOSBox Staging 0.83.
  private const string _SOURCE = """
    SCREEN 13
    OPEN "SPANS.TXT" FOR OUTPUT AS #1
    PSET (160,100),1
    PSET (161,100),2
    PSET (162,100),3
    PSET (163,100),4
    PRINT #1,POINT(160,100);POINT(161,100);POINT(162,100);POINT(163,100)
    DEF SEG = &HA000
    POKE 32170,5
    PSET (171,100),6
    PRINT #1,POINT(170,100);POINT(171,100)
    LINE (319,199)-(0,199),10
    LINE -(0,197),6
    PRINT #1,POINT(319,199);POINT(0,199);POINT(0,198);POINT(0,197);POINT(1,198)
    LINE (319,190)-(0,189),11,BF
    PRINT #1,POINT(0,189);POINT(319,190);POINT(0,188)
    LINE (10,10)-(16,12),4,B
    PSET (11,11),0
    DRAW "C3 P15,4 BR10 R2"
    PRINT #1,POINT(11,11);POINT(15,11);POINT(16,11);POINT(22,11)
    READ tail$
    PSET (10,30),0
    DRAW "C9 R5;"+tail$+" L1"
    PRINT #1,POINT(14,30);POINT(18,32);POINT(17,32);POINT(16,32)
    CLOSE #1
    SCREEN 0
    END
    DATA "R3 D2"
    """;

  [TestCase(8086, false, false)]
  [TestCase(8086, true, false)]
  [TestCase(8086, false, true)]
  [TestCase(8086, true, true)]
  [TestCase(80386, false, false)]
  [TestCase(80386, true, false)]
  [TestCase(80386, false, true)]
  [TestCase(80386, true, true)]
  public void Execute_GivenCombinedGraphicsRuntimePaths_ThenMatchesOriginalPixels(int cpu, bool optimize, bool com) {
    var source = $"$CPU {cpu}\n" + _SOURCE;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("SPANS.TXT"), Is.EqualTo(System.Text.Encoding.ASCII.GetBytes(
      " 1  2  3  4 \r\n 5  6 \r\n 10  6  6  6  0 \r\n 11  11  0 \r\n 15  15  4  15 \r\n 9  9  9  0 \r\n")));
  }
}
