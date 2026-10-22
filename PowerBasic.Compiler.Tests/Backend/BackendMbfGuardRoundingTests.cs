using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbfGuardRoundingTests {

  [TestCase(Dialect.Gw, false)]
  [TestCase(Dialect.Gw, true)]
  [TestCase(Dialect.Basica, false)]
  [TestCase(Dialect.Basica, true)]
  public void Multiply_GivenDiscardedGuardBits_ThenMatchesTheInterpreterNotIeeeRounding(
      Dialect dialect, bool optimize) {
    const string source = """
      10 OPEN "MULGUARD.TXT" FOR OUTPUT AS #1
      20 A!=0:B!=0:P%=VARPTR(A!):Q%=VARPTR(B!)
      30 POKE P%,4:POKE P%+1,0:POKE P%+2,0:POKE P%+3,129
      40 POKE Q%,1:POKE Q%+1,0:POKE Q%+2,48:POKE Q%+3,129
      50 C!=A!*B!:P%=VARPTR(C!)
      60 FOR I%=0 TO 3:PRINT #1,PEEK(P%+I%);:NEXT I%:PRINT #1,
      70 CLOSE #1:SYSTEM
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    // Complete file captured from genuine GW-BASIC 3.23. IEEE nearest/even instead writes 7.
    Assert.That(Cpu8086.Run(image, exactFloatingPoint: true).FileContent("MULGUARD.TXT"),
      Is.EqualTo(" 6  0  48  129 \r\n\u001a"));
  }
}
