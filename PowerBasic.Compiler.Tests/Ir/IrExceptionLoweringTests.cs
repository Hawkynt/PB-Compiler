using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrExceptionLoweringTests {

  private static IrModule Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var reason);
    Assert.That(module, Is.Not.Null, reason);
    return module!;
  }

  [TestCase("""
    TRY
      ERROR 5
    CATCH
      PRINT ERR
    END TRY
    """)]
  [TestCase("""
    DEFER PRINT "cleanup"
    PRINT "body"
    """)]
  public void HandlerEntry_GivenNonLocalRuntimeJump_ThenLoweredCfgStillPassesSsaVerification(string source) {
    var module = Lower(source);

    var errors = IrVerifier.Verify(module);

    Assert.That(errors, Is.Empty, string.Join("; ", errors));
  }
}
