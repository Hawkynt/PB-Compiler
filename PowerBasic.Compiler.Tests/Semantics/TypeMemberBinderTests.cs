using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// Binding of PB 3.6 TYPE members: each lifts to a procedure mangled with the type
/// name and a BYREF <c>THIS</c> first parameter, and a member access desugars to a
/// call on that procedure with the receiver as the first argument.
/// </summary>
[TestFixture]
public sealed class TypeMemberBinderTests {

  private const string _counter =
    "TYPE Counter\n" +
    "  N AS INTEGER\n" +
    "  FUNCTION Bump() AS INTEGER\n" +
    "    INCR THIS.N\n" +
    "    Bump = THIS.N\n" +
    "  END FUNCTION\n" +
    "  PROPERTY GET Value() AS INTEGER\n" +
    "    Value = THIS.N\n" +
    "  END PROPERTY\n" +
    "END TYPE\n";

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Bind_GivenTypeMembers_WhenBound_ThenLiftedToManglledProceduresWithThis() {
    var model = Bind(_counter + "DIM c AS Counter\n");
    Assert.Multiple(() => {
      Assert.That(model.Procedures.ContainsKey("Counter.Bump"), Is.True, "FUNCTION member lifts to Counter.Bump");
      Assert.That(model.Procedures.ContainsKey("Counter.get_Value"), Is.True, "PROPERTY GET lifts to Counter.get_Value");
      var bump = model.Procedures["Counter.Bump"];
      Assert.That(bump.Parameters, Has.Count.EqualTo(1), "the implicit THIS receiver is the only parameter");
      Assert.That(bump.Parameters[0].Name, Is.EqualTo("THIS"));
      Assert.That(bump.Parameters[0].Type, Is.InstanceOf<UdtType>());
    });
  }

  [Test]
  public void Bind_GivenMethodCall_WhenBound_ThenDesugarsToCallWithReceiverFirst() {
    var model = Bind(_counter + "DIM c AS Counter\nx% = c.Bump()\n");
    var call = model.ExpressionTypes.Keys.OfType<IndexExpr>()
      .Single(ix => ix.Target is MemberExpr { Member: "Bump" });
    Assert.That(model.Desugared.TryGetValue(call, out var desugared), Is.True, "o.Bump() desugars");
    var lowered = (CallOrIndexExpr)desugared!;
    Assert.Multiple(() => {
      Assert.That(lowered.Name, Is.EqualTo("Counter.Bump"));
      Assert.That(lowered.Arguments, Has.Count.EqualTo(1), "the receiver is passed as the first argument");
      Assert.That(model.TypeOf(call), Is.EqualTo(PbType.Integer), "the call's type is the member return type");
    });
  }

  [Test]
  public void Bind_GivenPropertyGet_WhenBound_ThenDesugarsToGetterCall() {
    var model = Bind(_counter + "DIM c AS Counter\ny% = c.Value\n");
    var access = model.ExpressionTypes.Keys.OfType<MemberExpr>().Single(m => m.Member == "Value");
    Assert.That(model.Desugared.TryGetValue(access, out var desugared), Is.True, "o.Value desugars to a getter call");
    Assert.That(((CallOrIndexExpr)desugared!).Name, Is.EqualTo("Counter.get_Value"));
  }
}
