using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class DrawMacroStateTests {
  // PREFIX.TXT captured with QB 4.50 BC.EXE + BCOM45.LIB under DOSBox Staging 0.83.
  // BC SHA-256 DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA.
  // BCOM45 SHA-256 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996.
  private const string _PREFIX_SOURCE = """
    SCREEN 13
    OPEN "PREFIX.TXT" FOR OUTPUT AS #1
    PSET (10,20),0
    DRAW "N C9 R5 D3"
    PRINT #1,POINT(14,20);POINT(10,23);POINT(15,23)
    PSET (10,40),0
    DRAW "B C9 R5 D3"
    PRINT #1,POINT(14,40);POINT(10,43);POINT(15,43)
    PSET (10,60),0
    DRAW "BN C9 R5 D3"
    PRINT #1,POINT(14,60);POINT(10,63);POINT(15,63)
    PSET (10,80),0
    DRAW "NB C9 R5 D3"
    PRINT #1,POINT(14,80);POINT(10,83);POINT(15,83)
    CLOSE #1
    SCREEN 0
    END
    """;

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenDrawPrefixesAcrossColour_ThenKeepsBothUntilTheNextMovement(bool optimize, bool com) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_PREFIX_SOURCE, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("PREFIX.TXT"),
      Is.EqualTo(" 9  9  0 \r\n 0  0  9 \r\n 0  9  0 \r\n 0  9  0 \r\n"u8.ToArray()));
  }
}
