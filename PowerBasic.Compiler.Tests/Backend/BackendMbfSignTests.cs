using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class BackendMbfSignTests {

  private const string _SOURCE = """
    10 OPEN "MBFSGN.TXT" FOR OUTPUT AS #1
    20 X!=-1D-38:PRINT #1,SGN(X!)
    30 X!=0:PRINT #1,SGN(X!)
    40 X!=1D-38:PRINT #1,SGN(X!)
    50 X#=-1D38:PRINT #1,SGN(X#)
    60 X#=0:PRINT #1,SGN(X#)
    70 X#=1D38:PRINT #1,SGN(X#)
    80 X#=-1.0000000000000001D0:PRINT #1,SGN(X#+0D0)
    90 PRINT #1,SGN(-1E-38);SGN(1E-38);SGN(0!)
    100 CLOSE #1:SYSTEM
    """;

  [TestCase(Dialect.Basica, false)]
  [TestCase(Dialect.Basica, true)]
  [TestCase(Dialect.Gw, false)]
  [TestCase(Dialect.Gw, true)]
  public void Sgn_GivenCanonicalMbfCellsAndExpressions_ThenMatchesGwBasicFile(Dialect dialect, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", dialect), "T.BAS", dialect), dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };

    var image = generator.EmitExecutable();

    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    Assert.That(generator.BackendRoutedNames, Does.Contain("main"));
    Assert.That(generator.BackendModuleForTesting?.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    var output = Cpu8086.Run(image, exactFloatingPoint: true).FileContent("MBFSGN.TXT");
    // Complete output captured from genuine GW-BASIC 3.23, including whitespace and DOS EOF.
    Assert.That(output, Is.EqualTo("-1 \r\n 0 \r\n 1 \r\n-1 \r\n 0 \r\n 1 \r\n-1 \r\n-1  1  0 \r\n\u001a"));
  }
}
