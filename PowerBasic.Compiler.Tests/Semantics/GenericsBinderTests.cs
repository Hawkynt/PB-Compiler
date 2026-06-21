using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 compile-time generics (monomorphization): a generic <c>TYPE Name OF T</c> is a template
/// vivified per concrete instantiation into an ordinary TYPE named with an untypeable mangle
/// (<c>Name@LONG</c>); the binder resolves a generic use to that concrete type.
/// </summary>
[TestFixture]
public sealed class GenericsBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  private const string _box =
    "TYPE Box OF T\n  V AS T\n  FUNCTION GetIt() AS T\n    GetIt = THIS.V\n  END FUNCTION\nEND TYPE\n";

  [Test]
  public void Bind_GivenGenericTypeUsedAtOneType_WhenBound_ThenMonomorphizedUdtWithSubstitutedField() {
    var model = Bind(_box + "DIM b AS Box OF LONG\n");
    Assert.Multiple(() => {
      Assert.That(model.Udts.ContainsKey("Box@Long"), Is.True, "the generic use vivifies a concrete TYPE");
      Assert.That(model.Udts.ContainsKey("Box"), Is.False, "the template itself is not a concrete TYPE");
      Assert.That(model.Udts["Box@Long"].FindField("V")!.Type, Is.EqualTo(PbType.Long), "T was substituted with LONG");
      Assert.That(model.Procedures.ContainsKey("Box@Long.get_GetIt") || model.Procedures.ContainsKey("Box@Long.GetIt"), Is.True,
        "the member lifts under the mangled concrete type name");
    });
  }

  [Test]
  public void Bind_GivenTwoInstantiations_WhenBound_ThenTwoDistinctUdts() {
    var model = Bind(_box + "DIM a AS Box OF LONG\nDIM b AS Box OF INTEGER\nDIM c AS Box OF STRING\n");
    Assert.Multiple(() => {
      Assert.That(model.Udts["Box@Long"].FindField("V")!.Type, Is.EqualTo(PbType.Long));
      Assert.That(model.Udts["Box@Integer"].FindField("V")!.Type, Is.EqualTo(PbType.Integer));
      Assert.That(model.Udts["Box@String"].FindField("V")!.Type, Is.InstanceOf<StringType>());
    });
  }

  [Test]
  public void Bind_GivenInstantiationVariable_WhenBound_ThenTypedAsTheConcreteUdt() {
    var model = Bind(_box + "DIM b AS Box OF LONG\n");
    var dim = model.ModuleVariables.Values.Single(v => v.Name.Equals("b", System.StringComparison.OrdinalIgnoreCase));
    Assert.That(((UdtType)dim.Type).Name, Is.EqualTo("Box@Long"));
  }

  [Test]
  public void Bind_GivenWrongTypeArgumentCount_WhenBound_ThenRejectedClearly() {
    var unit = Parser.Parse(Lexer.Tokenize(
      "TYPE Box OF T\n  V AS T\nEND TYPE\nDIM x AS Box OF (LONG, INTEGER)\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("takes 1 type argument")), Is.True);
  }

  [Test]
  public void Bind_GivenGenericFunctionInferredAtTwoTypes_WhenBound_ThenTwoInstancesWithSubstitutedReturn() {
    var model = Bind(
      "FUNCTION Pick OF T (BYVAL a AS T, BYVAL b AS T) AS T\n  Pick = a\nEND FUNCTION\n" +
      "DIM x&\nx& = Pick(70000, 40000)\nDIM s$\ns$ = Pick(\"a\", \"b\")\n");
    Assert.Multiple(() => {
      Assert.That(model.Procedures.ContainsKey("Pick@Long"), Is.True, "inferred LONG instance");
      Assert.That(model.Procedures.ContainsKey("Pick@String"), Is.True, "inferred STRING instance");
      Assert.That(model.Procedures["Pick@Long"].ReturnType, Is.EqualTo(PbType.Long), "return type T -> LONG");
      Assert.That(model.Procedures["Pick@String"].ReturnType, Is.InstanceOf<StringType>());
      Assert.That(model.Procedures.ContainsKey("Pick"), Is.False, "the template itself is not callable");
    });
  }

  [Test]
  public void Bind_GivenGenericSubWithBodyLocalOfT_WhenBound_ThenInstantiatedPerCall() {
    var model = Bind(
      "SUB Keep OF T (a AS T, b AS T)\n  DIM t AS T\n  t = a\n  a = b\n  b = t\nEND SUB\n" +
      "DIM x&, y&\nKeep(x&, y&)\n");
    Assert.That(model.Procedures.ContainsKey("Keep@Long"), Is.True);
  }

  [Test]
  public void Bind_GivenExplicitTypeArguments_WhenBound_ThenInstantiatesEvenWhenReturnTypeOnly() {
    // a type parameter that appears only in the return type is supplied explicitly: Zero OF LONG()
    var model = Bind(
      "FUNCTION Zero OF T () AS T\n  Zero = 0\nEND FUNCTION\nDIM x&\nx& = Zero OF LONG ()\n");
    Assert.That(model.Procedures.ContainsKey("Zero@Long"), Is.True);
    Assert.That(model.Procedures["Zero@Long"].ReturnType, Is.EqualTo(PbType.Long));
  }

  [Test]
  public void Bind_GivenNestedGenericArgument_WhenBound_ThenTypeParameterInferred() {
    // T inferred from a Box OF T parameter given a Box OF LONG argument
    var model = Bind(
      "TYPE Box OF T\n  V AS T\nEND TYPE\nFUNCTION Unwrap OF T (b AS Box OF T) AS T\n  Unwrap = b.V\nEND FUNCTION\n" +
      "DIM bl AS Box OF LONG\nDIM r&\nr& = Unwrap(bl)\n");
    Assert.Multiple(() => {
      Assert.That(model.Procedures.ContainsKey("Unwrap@Long"), Is.True, "T inferred as LONG from Box OF LONG");
      Assert.That(model.Procedures["Unwrap@Long"].ReturnType, Is.EqualTo(PbType.Long));
    });
  }

  [Test]
  public void Bind_GivenUninferrableTypeParameter_WhenBound_ThenRejectedClearly() {
    // T appears only in the return type, so it cannot be inferred from arguments
    var unit = Parser.Parse(Lexer.Tokenize(
      "FUNCTION Zero OF T () AS T\nEND FUNCTION\nDIM x&\nx& = Zero()\n", "t.bas", Dialect.Pb36), "t.bas", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors.Any(e => e.Message.Contains("cannot infer type parameter")), Is.True);
  }

  [Test]
  public void Bind_GivenGenericsBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE Box OF T\n  V AS T\nEND TYPE\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }
}
