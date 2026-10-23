using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class PixelModeFallbackTests {
  // QB 4.50 BC.EXE + BCOM45.LIB, captured under DOSBox Staging 0.83.
  // BC SHA-256 DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA.
  // BCOM45 SHA-256 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996.
  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenDistinctPlanarPixels_ThenMatchesQuickBasicInsteadOfAliasingPackedOffsets(
      bool optimize, bool com) {
    const string source = """
      SCREEN 12
      PSET (320,0),3
      PSET (0,1),5
      OPEN "PIXELS.TXT" FOR OUTPUT AS #1
      PRINT #1,POINT(320,0);POINT(0,1)
      CLOSE #1
      SCREEN 0
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("PIXELS.TXT"),
      Is.EqualTo(System.Text.Encoding.ASCII.GetBytes(" 3  5 \r\n")));
  }
}
