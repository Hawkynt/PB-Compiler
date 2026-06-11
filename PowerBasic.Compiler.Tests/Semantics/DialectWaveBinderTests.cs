using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

/// <summary>Binder semantics of the dialect wave: new suffix typing, QUAD, pointers, ASCIIZ, BCD deferrals.</summary>
[TestFixture]
public sealed class DialectWaveBinderTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS"), "TEST.BAS");
    return Binder.Bind(unit);
  }

  private static SemanticModel BindOk(string source) {
    var model = Bind(source);
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    return model;
  }

  #region suffix typing

  [TestCase("b? = 1", "b?", ScalarKind.Byte)]
  [TestCase("w?? = 1", "w??", ScalarKind.Word)]
  [TestCase("d??? = 1", "d???", ScalarKind.Dword)]
  [TestCase("q&& = 1", "q&&", ScalarKind.Quad)]
  public void Bind_GivenSuffixedVariable_WhenBound_ThenSuffixTypeAssigned(string source, string key, ScalarKind kind) {
    var model = BindOk(source);
    Assert.That(((ScalarType)model.ModuleVariables[key].Type).Kind, Is.EqualTo(kind));
  }

  [Test]
  public void Bind_GivenFixAndBcdSuffixes_WhenBound_ThenBcdTypesAssigned() {
    var model = BindOk("DIM f AS FIX\nDIM b AS BCD");
    Assert.That(model.ModuleVariables["f"].Type, Is.EqualTo(PbType.Fix));
    Assert.That(model.ModuleVariables["b"].Type, Is.EqualTo(PbType.Bcd));
    Assert.That(PbType.Fix.Size, Is.EqualTo(8));
    Assert.That(PbType.Bcd.Size, Is.EqualTo(10));
  }

  [Test]
  public void Bind_GivenDefQudRange_WhenBound_ThenQuadDefault() {
    var model = BindOk("DEFQUD Q\nqvar = 1");
    Assert.That(model.ModuleVariables["qvar"].Type, Is.EqualTo(PbType.Quad));
  }

  [Test]
  public void Bind_GivenQuadAsClause_WhenBound_ThenEightByteScalar() {
    var model = BindOk("DIM x AS QUAD");
    Assert.That(model.ModuleVariables["x"].Type, Is.EqualTo(PbType.Quad));
    Assert.That(PbType.Quad.Size, Is.EqualTo(8));
  }

  #endregion

  #region literal typing (boundaries)

  [TestCase("x% = 32767", 0)]
  [TestCase("x& = 32768", 0)]
  [TestCase("x& = 2147483647", 0)]
  public void Bind_GivenBoundaryLiterals_WhenBound_ThenNoErrors(string source, int _)
    => BindOk(source);

  [Test]
  public void Bind_GivenLiteralBeyondLongRange_WhenBound_ThenQuadTyped() {
    var model = BindOk("q&& = 9000000000");
    var assign = (AssignStmt)model.MainBody.Single(s => s is AssignStmt);
    Assert.That(model.TypeOf(assign.Value), Is.EqualTo(PbType.Quad));
  }

  #endregion

  #region pointers

  [Test]
  public void Bind_GivenPointerDim_WhenBound_ThenPointerTypeWithTarget() {
    var model = BindOk("DIM p AS INTEGER PTR");
    var type = (PointerType)model.ModuleVariables["p"].Type;
    Assert.That(type.Target, Is.EqualTo(PbType.Integer));
    Assert.That(type.Size, Is.EqualTo(4));
  }

  [Test]
  public void Bind_GivenNestedPointer_WhenBound_ThenPointerToPointer() {
    var model = BindOk("DIM p AS INTEGER PTR PTR");
    var type = (PointerType)model.ModuleVariables["p"].Type;
    Assert.That(type.Target, Is.InstanceOf<PointerType>());
  }

  [Test]
  public void Bind_GivenDeref_WhenBound_ThenTargetTyped() {
    var model = BindOk("DIM p AS INTEGER PTR\nx% = @p");
    var assign = (AssignStmt)model.MainBody.Last(s => s is AssignStmt);
    Assert.That(model.TypeOf(assign.Value), Is.EqualTo(PbType.Integer));
  }

  [Test]
  public void Bind_GivenDerefOfNonPointer_WhenBound_ThenError() {
    var model = Bind("x% = 1\ny% = @x%");
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("PTR")));
  }

  [Test]
  public void Bind_GivenPointerAssignedFromVarPtr32_WhenBound_ThenAccepted()
    => BindOk("DIM p AS INTEGER PTR\nx% = 5\np = VARPTR32(x%)");

  [Test]
  public void Bind_GivenUdtPointerFieldAccess_WhenBound_ThenFieldType() {
    var model = BindOk("TYPE T\n  v AS LONG\nEND TYPE\nDIM q AS T PTR\nl& = @q.v");
    var assign = (AssignStmt)model.MainBody.Last(s => s is AssignStmt);
    Assert.That(model.TypeOf(assign.Value), Is.EqualTo(PbType.Long));
  }

  #endregion

  #region ASCIIZ

  [Test]
  public void Bind_GivenAsciizDim_WhenBound_ThenAsciizTypeWithLength() {
    var model = BindOk("DIM z AS ASCIIZ * 12");
    var type = (AsciizType)model.ModuleVariables["z"].Type;
    Assert.That(type.Length, Is.EqualTo(12));
    Assert.That(type.Size, Is.EqualTo(12));
  }

  [TestCase("DIM z AS ASCIIZ * 0")]
  [TestCase("DIM z AS ASCIIZ * 40000")]
  public void Bind_GivenAsciizLengthOutOfRange_WhenBound_ThenError(string source)
    => Assert.That(Bind(source).Errors, Is.Not.Empty);

  [Test]
  public void Bind_GivenAsciizStringAssignments_WhenBound_ThenBothDirectionsAccepted()
    => BindOk("DIM z AS ASCIIZ * 8\nz = \"abc\"\ns$ = z");

  [Test]
  public void Bind_GivenAsciizAssignedNumeric_WhenBound_ThenTypeMismatch()
    => Assert.That(Bind("DIM z AS ASCIIZ * 8\nz = 42").Errors, Is.Not.Empty);

  [Test]
  public void Bind_GivenAsciizFieldInType_WhenBound_ThenPackedOffsetKept() {
    var model = BindOk("TYPE R\n  tag AS ASCIIZ * 8\n  num AS INTEGER\nEND TYPE");
    var udt = model.Udts["R"];
    Assert.That(udt.Size, Is.EqualTo(10));
    Assert.That(udt.FindField("num")!.Offset, Is.EqualTo(8));
  }

  #endregion

  #region BCD arithmetic (computes as EXT on the x87 stack)

  [Test]
  public void Bind_GivenBcdArithmetic_WhenBound_ThenAccepted()
    => BindOk("DIM a AS FIX\nDIM b AS FIX\nx! = a + b");

  [Test]
  public void Bind_GivenBcdMixedAssignment_WhenBound_ThenAccepted()
    => BindOk("DIM a AS FIX\na = 1.5");

  [Test]
  public void Bind_GivenSameTypeBcdCopy_WhenBound_ThenAccepted()
    => BindOk("DIM a AS FIX\nDIM b AS FIX\na = b");

  #endregion

  #region UDT comparison

  [Test]
  public void Bind_GivenUdtEquality_WhenBound_ThenIntegerResult() {
    var model = BindOk("TYPE T\n  a AS INTEGER\nEND TYPE\nDIM x AS T\nDIM y AS T\nr% = x = y");
    Assert.That(model.Success, Is.True);
  }

  [Test]
  public void Bind_GivenUdtOrderingComparison_WhenBound_ThenRejected() {
    var model = Bind("TYPE T\n  a AS INTEGER\nEND TYPE\nDIM x AS T\nDIM y AS T\nr% = x < y");
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("only = and <>")));
  }

  [Test]
  public void Bind_GivenDifferentUdtComparison_WhenBound_ThenRejected() {
    var model = Bind("TYPE T\n  a AS INTEGER\nEND TYPE\nTYPE U\n  a AS INTEGER\nEND TYPE\nDIM x AS T\nDIM y AS U\nr% = x = y");
    Assert.That(model.Errors, Is.Not.Empty);
  }

  #endregion

  #region concat and new intrinsics

  [Test]
  public void Bind_GivenConcatOnNumbers_WhenBound_ThenRejected()
    => Assert.That(Bind("x% = 1 & 2").Errors, Is.Not.Empty);

  [Test]
  public void Bind_GivenConcatOnStrings_WhenBound_ThenStringResult()
    => BindOk("c$ = a$ & b$");

  [Test]
  public void Bind_GivenRndRange_WhenBound_ThenLongResult() {
    var model = BindOk("r& = RND(1, 6)");
    var assign = (AssignStmt)model.MainBody.Single(s => s is AssignStmt);
    Assert.That(model.TypeOf(assign.Value), Is.EqualTo(PbType.Long));
  }

  [Test]
  public void Bind_GivenSizeofOnDynamicString_WhenBound_ThenAccepted()
    => BindOk("n& = SIZEOF(s$)");

  [Test]
  public void Bind_GivenCodePtr32OfLabel_WhenBound_ThenDwordTyped() {
    var model = BindOk("Target:\ng??? = CODEPTR32(Target)");
    var assign = (AssignStmt)model.MainBody.Single(s => s is AssignStmt);
    Assert.That(model.TypeOf(assign.Value), Is.EqualTo(PbType.Dword));
  }

  #endregion
}
