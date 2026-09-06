using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Execution-level gate for stack-only non-default procedure conventions. Two parameters make the
/// right-to-left argument order observable; two calls plus a following PRINT make stack cleanup
/// observable as well (especially CDECL, whose caller rather than callee restores SP).
/// </summary>
[TestFixture]
public sealed class BackendStackConventionRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Source(string convention) => $$"""
    SUB S {{convention}} (BYVAL a%, BYVAL b%) NOINLINE
      PRINT a%; b%
    END SUB
    S 1, 2
    S 3, 4
    PRINT 99
    """;

  [TestCase("CDECL", false)]
  [TestCase("CDECL", true)]
  [TestCase("STDCALL", false)]
  [TestCase("STDCALL", true)]
  public void Procedure_GivenStackConvention_ThenRoutedAndDirectExecutionAgree(string convention, bool optimize) {
    var source = Source(convention);
    var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("S"), $"{convention} procedure did not route");

    var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));

    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }
}
