using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 type aliases: <c>TYPE Handle AS DWORD</c> (single line, no END TYPE) names an existing
/// type. The alias is fully resolved at bind time - zero runtime cost, and the decompilation
/// substitutes the underlying type everywhere.
/// </summary>
[TestFixture]
public sealed class TypeAliasBinderTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);
    return Binder.Bind(unit, dialect);
  }

  private static PbType TypeOf(SemanticModel model, string variable)
    => model.ModuleVariables.Values.Single(v => v.Name.Equals(variable, System.StringComparison.OrdinalIgnoreCase)).Type;

  [Test]
  public void Bind_GivenScalarAlias_WhenUsedInDim_ThenResolvesToUnderlyingType() {
    var model = Bind("TYPE Handle AS DWORD\nDIM h AS Handle\n");
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      Assert.That(TypeOf(model, "h"), Is.EqualTo(PbType.Dword));
    });
  }

  [Test]
  public void Bind_GivenAliasToUdt_WhenMemberAccessed_ThenBindsThroughAlias() {
    var model = Bind("TYPE Point\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nTYPE Coord AS Point\nDIM p AS Coord\np.X = 3\nPRINT p.X\n");
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      Assert.That(TypeOf(model, "p"), Is.InstanceOf<UdtType>());
      Assert.That(((UdtType)TypeOf(model, "p")).Name, Is.EqualTo("Point"));
    });
  }

  [Test]
  public void Bind_GivenChainedAliases_WhenUsed_ThenResolveTransitively() {
    var model = Bind("TYPE Handle AS DWORD\nTYPE FileHandle AS Handle\nDIM f AS FileHandle\n");
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      Assert.That(TypeOf(model, "f"), Is.EqualTo(PbType.Dword));
    });
  }

  [Test]
  public void Bind_GivenAliasUsedAsTypeField_WhenBound_ThenFieldHasUnderlyingType() {
    var model = Bind("TYPE Handle AS DWORD\nTYPE Rec\n  h AS Handle\nEND TYPE\nDIM r AS Rec\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var udt = (UdtType)TypeOf(model, "r");
    Assert.That(udt.FindField("h")!.Type, Is.EqualTo(PbType.Dword));
  }

  [Test]
  public void Bind_GivenCircularAliases_WhenUsed_ThenErrorNotHang() {
    var model = Bind("TYPE A AS B\nTYPE B AS A\nDIM x AS A\n");
    Assert.That(model.Errors, Is.Not.Empty, "a circular alias chain must be a bind error");
  }

  [Test]
  public void Bind_GivenAliasToUnknownType_WhenUsed_ThenError() {
    var model = Bind("TYPE H AS Nonexistent\nDIM x AS H\n");
    Assert.That(model.Errors, Is.Not.Empty);
  }

  [Test]
  public void Bind_GivenAliasDuplicatingUdtName_WhenBound_ThenError() {
    var model = Bind("TYPE Point\n  X AS INTEGER\nEND TYPE\nTYPE Point AS LONG\nDIM p AS Point\n");
    Assert.That(model.Errors, Is.Not.Empty, "an alias may not reuse an existing type name");
  }

  [Test]
  public void Parse_GivenAliasBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("TYPE Handle AS DWORD\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }
}
