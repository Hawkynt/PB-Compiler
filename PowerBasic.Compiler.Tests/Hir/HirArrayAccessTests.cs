using PowerBasic.Compiler.Hir;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Hir;

[TestFixture]
public sealed class HirArrayAccessTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect),
      dialect);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  private static CallOrIndexExpr ArrayAccess(SemanticModel model, string name)
    => model.VariableBindings.Keys
      .OfType<CallOrIndexExpr>()
      .Single(expression =>
        expression.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        && model.VariableBindings[expression].Type is ArrayType);

  [Test]
  public void StaticAccess_GivenOptionBase_ThenHirCarriesResolvedIdentityAndFoldedBounds() {
    var model = Bind("""
      OPTION BASE 1
      DIM a%(9)
      PRINT a%(2)
      END
      """);
    var expression = ArrayAccess(model, "a");

    Assert.That(HirArrayAccessBuilder.TryBuild(
      model, expression, checkBounds: true, out var access, out var error), Is.True, error);

    var symbol = model.VariableBindings[expression];
    Assert.Multiple(() => {
      Assert.That(access.Target, Is.SameAs(symbol));
      Assert.That(access.Type, Is.SameAs(symbol.Type));
      Assert.That(access.ArrayClass, Is.EqualTo(symbol.ArrayClass));
      Assert.That(access.BoundsSource, Is.EqualTo(HirArrayBoundsSource.Static));
      Assert.That(access.StaticBounds, Is.EqualTo(new[] { new HirStaticArrayBound(1, 9) }));
      Assert.That(access.Subscripts.Single(), Is.SameAs(expression.Arguments.Single()));
      Assert.That(access.CheckBounds, Is.True);
    });
  }

  [Test]
  public void DynamicAccess_GivenRuntimeBounds_ThenHirNamesDescriptorBackedSemantics() {
    var model = Bind("""
      $DYNAMIC
      DIM a%(1 TO 9)
      PRINT a%(2)
      END
      """);
    var expression = ArrayAccess(model, "a");

    Assert.That(HirArrayAccessBuilder.TryBuild(
      model, expression, checkBounds: true, out var access, out var error), Is.True, error);

    Assert.Multiple(() => {
      Assert.That(access.Type.IsDynamic, Is.True);
      Assert.That(access.BoundsSource, Is.EqualTo(HirArrayBoundsSource.Descriptor));
      Assert.That(access.StaticBounds, Is.Empty);
      Assert.That(access.CheckBounds, Is.True);
    });
  }

  [Test]
  public void PagedAccess_GivenBoundsCheckingRequested_ThenHirRecordsTheEffectiveUncheckedContract() {
    var model = Bind("""
      DIM HUGE h(0 TO 100) AS LONG
      PRINT h(1)
      END
      """);
    var expression = ArrayAccess(model, "h");

    Assert.That(HirArrayAccessBuilder.TryBuild(
      model, expression, checkBounds: true, out var access, out var error), Is.True, error);

    Assert.Multiple(() => {
      Assert.That(access.ArrayClass, Is.EqualTo(ArrayClass.Huge));
      Assert.That(access.BoundsSource, Is.EqualTo(HirArrayBoundsSource.Descriptor));
      Assert.That(access.CheckBounds, Is.False,
        "the direct/routed HUGE path does not apply $ERROR BOUNDS, so HIR must record that exception explicitly");
    });
  }

  [Test]
  public void IrLowering_GivenCheckedStaticAndDynamicAccesses_ThenBothLowerThroughHirWithoutChangingTrapSemantics() {
    foreach (var source in new[] {
      """
      $ERROR BOUNDS ON
      DIM a%(1 TO 3)
      PRINT a%(2)
      END
      """,
      """
      $ERROR BOUNDS ON
      $DYNAMIC
      DIM a%(1 TO 3)
      PRINT a%(2)
      END
      """,
    }) {
      var model = Bind(source);
      var module = IrLowering.TryLowerModule(model, out var reason);

      Assert.That(module, Is.Not.Null, reason);
      var printed = IrPrinter.Print(module!);
      Assert.Multiple(() => {
        Assert.That(printed, Does.Contain("@rt_error"),
          "checked element HIR must still lower to the observable Error 9 trap");
        Assert.That(IrVerifier.Verify(module!), Is.Empty);
      });
    }
  }
}
