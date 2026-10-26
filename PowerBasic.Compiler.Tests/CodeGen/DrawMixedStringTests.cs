using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class DrawMixedStringTests {
  // Captured with genuine QB 4.50 BC /O and BCOM45.LIB under DOSBox Staging 0.83.
  private const string _SOURCE = """
    SCREEN 13
    OPEN "MIXED.TXT" FOR OUTPUT AS #1
    DRAW "A0 S4 C15"
    READ tail$
    PSET (10,20),0
    DRAW "C9 R5;"+tail$+" L1"
    PRINT #1,POINT(14,20);POINT(18,22);POINT(17,22);POINT(16,22)
    READ tail$
    PSET (10,40),0
    DRAW "C3 R"+tail$+" D2"
    PRINT #1,POINT(14,40);POINT(15,42);POINT(16,40)
    READ tail$
    PSET (10,60),0
    DRAW "C7 R2;"+tail$+" C11 R3 D2"
    PRINT #1,POINT(11,60);POINT(14,60);POINT(15,62)
    READ tail$
    PSET (10,80),0
    DRAW "C12 R2;"+tail$+" R3"
    PRINT #1,POINT(12,75);POINT(12,74);POINT(14,80)
    DRAW "A0 S4"
    READ tail$
    PSET (10,100),0
    DRAW "N C9 R5;"+tail$
    PRINT #1,POINT(14,100);POINT(10,102);POINT(15,102)
    CLOSE #1
    SCREEN 0
    END
    DATA "R3 D2","5","B","S8 A1","D2"
    """;

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenKnownPrefixesAndDynamicTails_ThenPreservesGrammarAndSharedState(bool optimize, bool com) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    Assert.That(Cpu8086.Run(image).FileBytes("MIXED.TXT"), Is.EqualTo(
      " 9  9  9  0 \r\n 3  3  0 \r\n 7  0  11 \r\n 12  0  0 \r\n 9  9  0 \r\n"u8.ToArray()));
  }
}
