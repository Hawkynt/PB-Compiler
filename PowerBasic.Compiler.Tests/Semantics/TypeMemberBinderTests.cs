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

  private const string _box =
    "TYPE Box\n" +
    "  N AS INTEGER\n" +
    "  SUB Reset()\n" +
    "    THIS.N = 0\n" +
    "  END SUB\n" +
    "  PROPERTY SET Value(BYVAL v AS INTEGER)\n" +
    "    THIS.N = v\n" +
    "  END PROPERTY\n" +
    "END TYPE\n";

  [Test]
  public void Bind_GivenStatementMethodCall_WhenBound_ThenDesugarsToCallWithReceiver() {
    var model = Bind(_box + "DIM b AS Box\nb.Reset\n");
    var stmt = model.DesugaredStatements.Keys.OfType<MemberCallStmt>().Single();
    var call = (CallStmt)model.DesugaredStatements[stmt];
    Assert.Multiple(() => {
      Assert.That(call.Name, Is.EqualTo("Box.Reset"));
      Assert.That(call.Arguments, Has.Count.EqualTo(1), "the receiver is the only argument");
    });
  }

  [Test]
  public void Bind_GivenPropertySet_WhenBound_ThenDesugarsToSetterCall() {
    var model = Bind(_box + "DIM b AS Box\nb.Value = 7\n");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single();
    var call = (CallStmt)model.DesugaredStatements[assign];
    Assert.Multiple(() => {
      Assert.That(call.Name, Is.EqualTo("Box.set_Value"));
      Assert.That(call.Arguments, Has.Count.EqualTo(2), "receiver and the assigned value");
    });
  }

  [Test]
  public void Bind_GivenAutoProperty_WhenBound_ThenSynthesizesBackingFieldAndTrivialAccessors() {
    // an auto property gets a hidden backing field and trivial get_/set_ procedures (the optimizer
    // inlines those trivial bodies away later - the binder still produces them)
    var model = Bind(
      "TYPE P\n  PROPERTY GET X() AS INTEGER\n  PROPERTY SET X(BYVAL v AS INTEGER)\nEND TYPE\nDIM p AS P\np.X = 7\ny% = p.X\n");
    var udt = model.Udts["P"];
    Assert.Multiple(() => {
      Assert.That(udt.FindField("$X"), Is.Not.Null, "an auto property has a hidden backing field");
      Assert.That(udt.FindField("$X")!.Type, Is.EqualTo(PbType.Integer));
      Assert.That(udt.FindField("X"), Is.Null, "the property name is not itself a field");
      Assert.That(model.Procedures.ContainsKey("P.get_X") && model.Procedures.ContainsKey("P.set_X"), Is.True,
        "trivial accessors are ordinary procedures (inlined by the optimizer, not a binder special case)");
    });
  }

  [Test]
  public void Bind_GivenAnonymousProperty_WhenBound_ThenSynthesizesAutoGetAndSetOverOneField() {
    // PROPERTY Count AS LONG (no GET/SET) -> a backing field with both a trivial getter and setter
    var model = Bind("TYPE Box\n  PROPERTY Count AS LONG\nEND TYPE\nDIM b AS Box\nb.Count = 3\nz& = b.Count\n");
    var udt = model.Udts["Box"];
    Assert.Multiple(() => {
      Assert.That(udt.FindField("$Count"), Is.Not.Null);
      Assert.That(udt.FindField("$Count")!.Type, Is.EqualTo(PbType.Long));
      Assert.That(model.Procedures.ContainsKey("Box.get_Count") && model.Procedures.ContainsKey("Box.set_Count"), Is.True,
        "the anonymous property synthesizes both accessors over the one field");
    });
  }

  [Test]
  public void Bind_GivenConstructorCall_WhenBound_ThenRunsTypeNamedSubWithReceiver() {
    // p = Point(3, 4) calls the constructor (a SUB named like the TYPE) with the target as BYREF THIS
    var model = Bind(
      "TYPE Point\n  x AS LONG\n  y AS LONG\n  SUB Point(BYVAL px AS LONG, BYVAL py AS LONG)\n    THIS.x = px\n    THIS.y = py\n  END SUB\nEND TYPE\n" +
      "DIM p AS Point\np = Point(3, 4)\n");
    Assert.That(model.Procedures.ContainsKey("Point.Point"), Is.True, "the constructor lifts to Point.Point");
    var assign = model.DesugaredStatements.Keys.OfType<AssignStmt>().Single(a => a.Value is CallOrIndexExpr { Name: "Point" });
    var call = (CallStmt)model.DesugaredStatements[assign];
    Assert.Multiple(() => {
      Assert.That(call.Name, Is.EqualTo("Point.Point"));
      Assert.That(call.Arguments, Has.Count.EqualTo(3), "receiver THIS plus the two constructor arguments");
    });
  }

  [Test]
  public void Bind_GivenReadonlyTypeFieldWriteOutsideConstructor_WhenBound_ThenRejected() {
    var unit = Parser.Parse(Lexer.Tokenize(
      "TYPE Point READONLY\n  x AS LONG\n  SUB Point(BYVAL px AS LONG)\n    THIS.x = px\n  END SUB\nEND TYPE\n" +
      "DIM p AS Point\np = Point(5)\np.x = 9\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("READONLY TYPE Point")), Is.True,
      "writing a readonly field outside the constructor is rejected");
  }

  [Test]
  public void Bind_GivenReadonlyTypeFieldWriteInsideConstructor_WhenBound_ThenAllowed() {
    var model = Bind(
      "TYPE Point READONLY\n  x AS LONG\n  SUB Point(BYVAL px AS LONG)\n    THIS.x = px\n  END SUB\nEND TYPE\n" +
      "DIM p AS Point\np = Point(5)\n");
    Assert.That(model.Success, Is.True, "the constructor may set readonly fields");
  }

  [Test]
  public void Bind_GivenFieldAndValueKeywords_WhenBound_ThenResolveToBackingFieldAndValueParameter() {
    // PROPERTY SET Size() => FIELD = 2 * VALUE : FIELD is the backing field, VALUE the injected value param
    var model = Bind(
      "TYPE W\n  PROPERTY GET Size() AS INTEGER\n  PROPERTY SET Size() => FIELD = 2 * VALUE\nEND TYPE\nDIM w AS W\n");
    Assert.Multiple(() => {
      Assert.That(model.Udts["W"].FindField("$Size"), Is.Not.Null);
      var setter = model.Procedures["W.set_Size"];
      Assert.That(setter.Parameters.Select(p => p.Name), Does.Contain("VALUE"), "a value parameter is injected for VALUE");
      // FIELD on the assignment target desugared the body's store to the backing field
      Assert.That(model.DesugaredStatements.Values.OfType<AssignStmt>()
        .Any(s => s.Target is MemberExpr { Member: "$Size" }), Is.True, "FIELD = ... writes the backing field");
    });
  }
}
