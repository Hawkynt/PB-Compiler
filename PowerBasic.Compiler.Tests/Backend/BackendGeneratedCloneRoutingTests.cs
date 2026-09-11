using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// End-to-end O0283 coverage for the case that exposed definition-side ABI loss: specialization makes
/// a private IR definition that has no ProcedureSymbol, and the routed backend must still emit and call
/// it with the source procedure's stack convention.
/// </summary>
[TestFixture]
public sealed class BackendGeneratedCloneRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Source(string convention) {
    // SPEED inlines callees up to 256 IR instructions. Keep F above that threshold with observable
    // runtime calls so the first inliner cannot erase the call graph before O0283 sees Hot vs Cold,
    // and the post-specialization inliner cannot absorb the generated definition either.
    var prints = string.Join('\n', Enumerable.Repeat("  PRINT n%;", 260));
    return $$"""
      SUB F {{convention}} (BYVAL n%)
      {{prints}}
        PRINT
      END SUB

      SUB Hot() NOINLINE
        F 7
      END SUB

      SUB Cold(BYVAL n%) NOINLINE
        F n%
      END SUB

      Hot
      Cold 9
      PRINT 99
      """;
  }

  [TestCase("CDECL")]
  [TestCase("STDCALL")]
  public void Execute_GivenSpecializedStackConvention_WhenCloneSurvivesInlining_ThenGeneratedBodyRoutesAndAgrees(
      string convention) {
    var source = Source(convention);

    var routed = new CodeGenerator(Bind(source)) {
      Optimize = true,
      OptimizeSpeed = true,
      UseExperimentalBackend = true,
    };
    var routedImage = routed.EmitExecutable();
    Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
    Assert.That(routed.BackendGeneratedRoutedNames,
      Has.Some.StartsWith("F__o0283_ctx"), "O0283 clone was not emitted by the routed backend");

    var direct = new CodeGenerator(Bind(source)) {
      Optimize = true,
      OptimizeSpeed = true,
      UseExperimentalBackend = false,
    };
    var directImage = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));

    var expected = Cpu8086.Run(directImage);
    var actual = Cpu8086.Run(routedImage);
    Assert.That((actual.Output, actual.ExitCode), Is.EqualTo((expected.Output, expected.ExitCode)));
  }
}
