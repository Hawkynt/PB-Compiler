using PowerBasic.Compiler.Hir;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Hir;

[TestFixture]
public sealed class HirDirectCallTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  private static CallOrIndexExpr BoundCall(SemanticModel model, string name)
    => model.CallBindings.Keys
      .OfType<CallOrIndexExpr>()
      .Single(call => call.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

  [Test]
  public void NamedArguments_AreFrozenInParameterOrderWithParameterSemantics() {
    var model = Bind("""
      DECLARE FUNCTION Box&(BYVAL w AS LONG, h AS LONG, BYVAL d AS LONG)
      PRINT Box&(2, d := 5, h := 3)
      FUNCTION Box&(BYVAL w AS LONG, h AS LONG, BYVAL d AS LONG)
        Box& = w * h * d
      END FUNCTION
      END
      """);
    var site = BoundCall(model, "Box");

    Assert.That(HirDirectCallBuilder.TryBuild(
      model, site, site.Arguments, out var call, out var error), Is.True, error);

    Assert.Multiple(() => {
      Assert.That(call.Target, Is.SameAs(model.CallBindings[site]));
      Assert.That(call.IsFunction, Is.True);
      Assert.That(call.ReturnType, Is.EqualTo(PbType.Long));
      Assert.That(call.CallConvention, Is.EqualTo(CallConvention.Basic));
      Assert.That(call.Arguments.Select(argument => argument.Parameter.Name),
        Is.EqualTo(new[] { "w", "h", "d" }));
      Assert.That(call.Arguments.Select(argument => ((IntegerLiteralExpr)argument.Value).Value),
        Is.EqualTo(new long[] { 2, 3, 5 }));
      Assert.That(call.Arguments.Select(argument => argument.ByValue),
        Is.EqualTo(new[] { true, false, true }));
      Assert.That(call.Arguments.All(argument => argument.ParameterType.Equals(PbType.Long)), Is.True);
    });
  }

  [Test]
  public void OmittedTrailingDefault_IsMaterializedAtTheHirBoundary() {
    var model = Bind("""
      FUNCTION Pay%(BYVAL x AS INTEGER, BYVAL y AS INTEGER = 10)
        Pay% = x + y
      END FUNCTION
      PRINT Pay%(5)
      END
      """);
    var site = BoundCall(model, "Pay");

    Assert.That(HirDirectCallBuilder.TryBuild(
      model, site, site.Arguments, out var call, out var error), Is.True, error);

    Assert.Multiple(() => {
      Assert.That(call.Arguments, Has.Count.EqualTo(2));
      Assert.That(((IntegerLiteralExpr)call.Arguments[0].Value).Value, Is.EqualTo(5));
      Assert.That(((IntegerLiteralExpr)call.Arguments[1].Value).Value, Is.EqualTo(10));
    });
  }

  [Test]
  public void Lowering_GivenNamedArguments_ThenSsaCallConsumesHirParameterOrder() {
    var model = Bind("""
      FUNCTION Box&(BYVAL w AS LONG, BYVAL h AS LONG, BYVAL d AS LONG)
        Box& = w * h * d
      END FUNCTION
      PRINT Box&(2, d := 5, h := 3)
      END
      """);

    var module = IrLowering.TryLowerModule(model, out var reason);

    Assert.That(module, Is.Not.Null, reason);
    var target = module!.Functions.Single(function => function.Name.Equals("Box", StringComparison.OrdinalIgnoreCase));
    var call = module.Functions
      .Where(function => !ReferenceEquals(function, target))
      .SelectMany(function => function.AllInstructions.OfType<IrCall>())
      .Single(candidate => ReferenceEquals(candidate.Callee, target));

    Assert.Multiple(() => {
      Assert.That(call.Args.Cast<IrConstantInt>().Select(argument => argument.Value),
        Is.EqualTo(new long[] { 2, 3, 5 }));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }
}
