using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>
/// pb36 compile-time checking: <c>$ASSERT cond [, "message"]</c> is evaluated by the binder (emits no
/// code), and the reflection pseudo-functions TYPEOF$/SIZEOF(type)/FIELDCOUNT/FIELDNAME$/FIELDOFFSET/
/// FIELDSIZE fold to literals at bind time so both $ASSERT and ordinary expressions can use them.
/// </summary>
[TestFixture]
public sealed class StaticAssertReflectionTests {

  private static SemanticModel Bind(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);
    return Binder.Bind(unit, dialect);
  }

  private const string _POINT = "TYPE Point\n  X AS INTEGER\n  Y AS LONG\nEND TYPE\n";

  #region $ASSERT

  [Test]
  public void Bind_GivenTrueAssertion_WhenBound_ThenNoErrors() {
    var model = Bind("$ASSERT 1 + 1 = 2\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenFalseAssertionWithMessage_WhenBound_ThenErrorCarriesMessage() {
    var model = Bind("$ASSERT 1 = 2, \"math broke\"\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("math broke")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenNonConstantAssertion_WhenBound_ThenError() {
    var model = Bind("DIM x AS INTEGER\n$ASSERT x = 1\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("compile-time")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenAssertionOverEquate_WhenBound_ThenFolds() {
    var model = Bind("%N = 4\n$ASSERT %N * 2 = 8\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
  }

  [Test]
  public void Parse_GivenAssertBelowPb36_WhenParsed_ThenRejected() {
    Assert.Throws<ParserException>(() =>
      Parser.Parse(Lexer.Tokenize("$ASSERT 1 = 1\n", "t.bas", Dialect.Pb35), "t.bas", Dialect.Pb35));
  }

  #endregion

  #region reflection folding

  private static long? FoldedInt(SemanticModel model) {
    var call = model.Desugared.Keys.OfType<CallOrIndexExpr>().FirstOrDefault(c => model.Desugared[c] is IntegerLiteralExpr);
    return call == null ? null : ((IntegerLiteralExpr)model.Desugared[call]).Value;
  }

  private static string? FoldedString(SemanticModel model) {
    var call = model.Desugared.Keys.OfType<CallOrIndexExpr>().FirstOrDefault(c => model.Desugared[c] is StringLiteralExpr);
    return call == null ? null : ((StringLiteralExpr)model.Desugared[call]).Value;
  }

  [Test]
  public void Bind_GivenSizeofTypeName_WhenBound_ThenFoldsToTypeSize() {
    var model = Bind(_POINT + "PRINT SIZEOF(Point)\n");
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      Assert.That(FoldedInt(model), Is.EqualTo(6), "INTEGER (2) + LONG (4)");
    });
  }

  [Test]
  public void Bind_GivenTypeofOnVariable_WhenBound_ThenFoldsToTypeName() {
    var model = Bind(_POINT + "DIM p AS Point\nPRINT TYPEOF$(p)\n");
    Assert.Multiple(() => {
      Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
      Assert.That(FoldedString(model), Is.EqualTo("Point"));
    });
  }

  [Test]
  public void Bind_GivenTypeofOnScalarExpression_WhenBound_ThenFoldsToScalarName() {
    var model = Bind("DIM n AS LONG\nPRINT TYPEOF$(n)\n");
    Assert.That(FoldedString(model), Is.EqualTo("LONG"));
  }

  [Test]
  public void Bind_GivenFieldCount_WhenBound_ThenFolds() {
    var model = Bind(_POINT + "PRINT FIELDCOUNT(Point)\n");
    Assert.That(FoldedInt(model), Is.EqualTo(2));
  }

  [Test]
  public void Bind_GivenFieldNameByIndex_WhenBound_ThenFoldsOneBased() {
    var model = Bind(_POINT + "PRINT FIELDNAME$(Point, 2)\n");
    Assert.That(FoldedString(model), Is.EqualTo("Y"));
  }

  [Test]
  public void Bind_GivenFieldOffsetByName_WhenBound_ThenFolds() {
    var model = Bind(_POINT + "PRINT FIELDOFFSET(Point, Y)\n");
    Assert.That(FoldedInt(model), Is.EqualTo(2), "Y sits after the 2-byte X");
  }

  [Test]
  public void Bind_GivenFieldSizeByName_WhenBound_ThenFolds() {
    var model = Bind(_POINT + "PRINT FIELDSIZE(Point, Y)\n");
    Assert.That(FoldedInt(model), Is.EqualTo(4));
  }

  [Test]
  public void Bind_GivenUnknownFieldSelector_WhenBound_ThenError() {
    var model = Bind(_POINT + "PRINT FIELDOFFSET(Point, Z)\n");
    Assert.That(model.Errors, Is.Not.Empty);
  }

  [Test]
  public void Bind_GivenAssertOverReflection_WhenBound_ThenFoldsThroughDesugar() {
    // the folder resolves the bind-time reflection desugar, so $ASSERT can consume it
    var model = Bind(_POINT + "$ASSERT SIZEOF(Point) = 6, \"layout drifted\"\n");
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenFailingAssertOverReflection_WhenBound_ThenReportsMessage() {
    var model = Bind(_POINT + "$ASSERT SIZEOF(Point) = 99, \"layout drifted\"\n");
    Assert.That(model.Errors.Any(e => e.Message.Contains("layout drifted")), Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenSizeofVariableShadowingNothing_WhenAliasNamed_ThenAliasResolves() {
    var model = Bind("TYPE Handle AS DWORD\nPRINT SIZEOF(Handle)\n");
    Assert.That(FoldedInt(model), Is.EqualTo(4), "the alias reflects as its underlying DWORD");
  }

  #endregion
}
