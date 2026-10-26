using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class DrawMacroNumericStateTests {
  // DIAGS4.BAS, QB 4.50 BC.EXE + BCOM45.LIB, DOSBox Staging 0.83 (/O/E/X).
  // BC SHA-256 DE21556C94B588BFCA06388A45F6E22362A7C9E153571F5A27C4F95B6740F5FA.
  // BCOM45 SHA-256 5B1C7A6FBB102E3E47ACAA38349EFA9BF1DAE674E57F4D86D170E920086C8996.
  private const string _SOURCE = """
    SCREEN 13
    DRAW "A0 S4 C15"
    OPEN "SCALE.TXT" FOR OUTPUT AS #1
    OPEN "ERROR.TXT" FOR OUTPUT AS #2
    ON ERROR GOTO caught
    PSET (80,20),0
    probeIndex%=1
    DRAW "BR32767 C9 R2"
    PRINT #1,POINT(79,20);POINT(80,20);POINT(81,20)
    PSET (80,40),0
    probeIndex%=2
    DRAW "BL32767 C9 R2"
    PRINT #1,POINT(79,40);POINT(81,40);POINT(83,40)
    PSET (80,60),0
    probeIndex%=3
    DRAW "BR65536 C9 R2"
    PRINT #1,POINT(79,60);POINT(80,60);POINT(82,60)
    PSET (80,80),0
    probeIndex%=4
    DRAW "BM+8192,+0 BR8191 C9 R2"
    PRINT #1,POINT(79,80);POINT(80,80);POINT(81,80)
    PSET (80,100),0
    probeIndex%=5
    DRAW "BE32767 C9 R2"
    PRINT #1,POINT(79,101);POINT(81,101);POINT(80,100)
    CLOSE #1
    CLOSE #2
    SCREEN 0
    END
    caught:
    PRINT #2,probeIndex%;ERR
    RESUME NEXT
    """;

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(false, true)]
  [TestCase(true, true)]
  public void Execute_GivenLargeCountsAndExplicitDrawState_ThenMatchesNativeErrorsAndPixels(bool optimize, bool com) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45),
      Dialect.Qb45);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = com ? generator.EmitCom() : generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(image.Length, Is.GreaterThan(2));
    Assert.That(image.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.EqualTo(!com));
    var execution = Cpu8086.Run(image);
    Assert.That(execution.FileBytes("SCALE.TXT"), Is.EqualTo(
      " 0  0  0 \r\n 0  0  0 \r\n 0  0  0 \r\n 9  9  9 \r\n 0  0  0 \r\n"u8.ToArray()));
    Assert.That(execution.FileBytes("ERROR.TXT"),
      Is.EqualTo(" 1  5 \r\n 2  5 \r\n 3  5 \r\n 5  5 \r\n"u8.ToArray()));
  }
}
