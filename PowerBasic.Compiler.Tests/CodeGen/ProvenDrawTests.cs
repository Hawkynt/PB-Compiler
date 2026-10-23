using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class ProvenDrawTests {
  // DRAW.TXT captured with QB 4.50 BC.EXE + BCOM45.LIB under DOSBox Staging 0.83.
  // BC SHA-256 DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA.
  // BCOM45 SHA-256 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996.
  [TestCase("\"C9 \" + \"R5\"", false)]
  [TestCase("\"C9 \" + \"R5\"", true)]
  [TestCase("UCASE$(\"c9 r5\")", false)]
  [TestCase("UCASE$(\"c9 r5\")", true)]
  [TestCase("\"C9 \" + UCASE$(\"r5\")", false)]
  [TestCase("\"C9 \" + UCASE$(\"r5\")", true)]
  public void Execute_GivenDrawTextProvenByStringFolding_ThenPlotsItsCommands(string expression, bool com) {
    var source = $"""
      SCREEN 13
      PSET (10,10),0
      DRAW {expression}
      OPEN "DRAW.TXT" FOR OUTPUT AS #1
      PRINT #1,POINT(15,10);POINT(16,10)
      CLOSE #1
      SCREEN 0
      END
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = true, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.Length, Is.GreaterThan(2));
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("DRAW.TXT"), Is.EqualTo(" 9  0 \r\n"u8.ToArray()));
  }
}
