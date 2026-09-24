using PowerBasic.Compiler.Hir;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Hir;

[TestFixture]
public sealed class HirArrayLifetimeTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Redim_GivenBoundArray_ThenHirCarriesIdentityEffectiveBoundsAndPreserveIntent() {
    var model = Bind("""
      $DYNAMIC
      OPTION BASE 1
      DIM a%(9)
      REDIM PRESERVE a%(19)
      ERASE a%
      END
      """);
    var redim = model.MainBody.OfType<RedimStmt>().Single();

    Assert.That(HirArrayLifetimeBuilder.TryBuild(model, redim, out var operations, out var error),
      Is.True, error);
    var operation = operations.Single();
    var boundSymbol = model.RedimBindings[redim.Variables.Single()];

    Assert.Multiple(() => {
      Assert.That(operation.Target, Is.SameAs(boundSymbol));
      Assert.That(operation.Type, Is.SameAs(boundSymbol.Type));
      Assert.That(operation.ArrayClass, Is.EqualTo(boundSymbol.ArrayClass));
      Assert.That(operation.Preserve, Is.True);
      Assert.That(operation.Bounds, Has.Count.EqualTo(1));
      Assert.That(operation.Bounds[0].Lower, Is.TypeOf<IntegerLiteralExpr>());
      Assert.That(((IntegerLiteralExpr)operation.Bounds[0].Lower!).Value, Is.EqualTo(1),
        "OPTION BASE is a binding-time fact and must already be explicit in HIR");
      Assert.That(operation.Bounds[0].Upper, Is.SameAs(redim.Variables[0].ArrayBounds![0].Upper));
    });
  }

  [Test]
  public void Erase_GivenBoundArray_ThenHirCarriesResolvedArrayIdentity() {
    var model = Bind("""
      $DYNAMIC
      DIM a%(0 TO 9)
      ERASE a%
      END
      """);
    var erase = model.MainBody.OfType<EraseStmt>().Single();

    Assert.That(HirArrayLifetimeBuilder.TryBuild(model, erase, out var operations, out var error),
      Is.True, error);
    var operation = operations.Single();
    var boundSymbol = model.VariableBindings[erase.Arrays.Single()];

    Assert.Multiple(() => {
      Assert.That(operation.Target, Is.SameAs(boundSymbol));
      Assert.That(operation.Type, Is.SameAs(boundSymbol.Type));
      Assert.That(operation.ArrayClass, Is.EqualTo(boundSymbol.ArrayClass));
    });
  }

  [Test]
  public void IrLowering_GivenRedimAndErase_ThenLowersThroughTheHirContractWithoutChangingRuntimeSemantics() {
    var model = Bind("""
      $DYNAMIC
      DIM a%(0 TO 3)
      REDIM PRESERVE a%(0 TO 7)
      ERASE a%
      END
      """);

    var module = IrLowering.TryLowerModule(model, out var reason);

    Assert.That(module, Is.Not.Null, reason);
    var printed = IrPrinter.Print(module!);
    Assert.Multiple(() => {
      Assert.That(printed, Does.Contain("@rt_arr_realloc"));
      Assert.That(printed, Does.Contain("@rt_arr_free"));
      Assert.That(IrVerifier.Verify(module!), Is.Empty);
    });
  }
}
