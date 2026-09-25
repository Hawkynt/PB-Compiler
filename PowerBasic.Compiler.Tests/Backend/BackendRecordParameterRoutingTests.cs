using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Execution-level gate for BYREF record parameters, the ABI class whose routing is what lets the
/// "record parameter" row leave the declining list. A record crosses the call as one near pointer,
/// so the two things worth making observable are that members are read at the right offsets and that
/// the pointer really is the caller's storage: the callee writes through it and the caller prints the
/// result afterwards. A second member past the first proves the offsets rather than only the base.
/// </summary>
[TestFixture]
public sealed class BackendRecordParameterRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private const string _SOURCE = """
    TYPE T
      a AS INTEGER
      b AS INTEGER
    END TYPE
    FUNCTION Sum(p AS T) AS INTEGER NOINLINE
      Sum = p.a + p.b
    END FUNCTION
    SUB Bump(p AS T) NOINLINE
      p.a = p.a + 10
      p.b = p.b + 20
    END SUB
    DIM q AS T
    q.a = 2
    q.b = 3
    PRINT Sum(q)
    Bump q
    PRINT q.a; q.b
    PRINT Sum(q)
    """;

  [TestCase(false)]
  [TestCase(true)]
  public void Procedure_GivenByrefRecordParameter_ThenMembersAreReadAndWrittenInTheCallersRecord(bool optimize) {
    var routed = new CodeGenerator(Bind(_SOURCE)) { Optimize = optimize};
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Sum"), "the record-taking function did not route");
    Assert.That(routed.BackendRoutedNames, Does.Contain("Bump"), "the record-mutating sub did not route");

    var actual = Cpu8086.Run(routedImage);
    // 2 + 3, then Bump adds 10 and 20 through the pointer, and the sum is read again: 12 + 23
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((" 5 \r\n 12  23 \r\n 35 \r\n", 0)));
  }
}
