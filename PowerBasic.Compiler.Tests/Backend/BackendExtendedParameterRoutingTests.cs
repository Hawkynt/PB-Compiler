using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Definition-side ABI gate for PowerBASIC EXT parameters/results. EXT is the x87 80-bit format: a
/// BYVAL parameter occupies ten stack bytes and a function result returns in ST(0). The routed call
/// selector does not yet stage a ten-byte argument, so the module body intentionally remains on the
/// direct path here; that makes this a focused mixed-boundary proof of the routed callee itself.
/// </summary>
[TestFixture]
public sealed class BackendExtendedParameterRoutingTests {

  private const string _SOURCE = """
    FUNCTION Blend(BYVAL a##, BYVAL b##) AS EXT NOINLINE
      Blend = a## * 2 + b##
    END FUNCTION
    DIM x##, y##
    x## = 1.25
    y## = 2.5
    PRINT Blend(x##, y##)
    """;

  private static SemanticModel Bind() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_SOURCE, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Procedure_GivenTwoExtParametersAndExtResult_ThenRoutedDefinitionMatchesDirectExecution(bool optimize) {
    var routed = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = true };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Blend"), "the EXT-taking function did not route");

    var direct = new CodeGenerator(Bind()) { Optimize = optimize, UseExperimentalBackend = false };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));

    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }
}
