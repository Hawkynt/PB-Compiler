using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.Semantics;

[TestFixture]
public sealed class BinderTests {

  private static readonly SourcePosition _pos = new("TEST.BAS", 1, 1);

  private static CompilationUnit Unit(params Statement[] statements) => new("TEST.BAS", statements);
  private static NameExpr Name(string name, TypeSuffix suffix = TypeSuffix.None) => new(_pos, name, suffix);
  private static IntegerLiteralExpr Int(long v) => new(_pos, v, TypeSuffix.None);
  private static AssignStmt Assign(Expression target, Expression value) => new(_pos, target, value);

  #region implicit typing

  [Test]
  public void Bind_GivenSuffixedAssignment_WhenBound_ThenVariableCreatedWithSuffixType() {
    var model = Binder.Bind(Unit(Assign(Name("x", TypeSuffix.Long), Int(1))));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.ModuleVariables["x&"].Type, Is.EqualTo(PbType.Long));
  }

  [Test]
  public void Bind_GivenNoSuffixNoDefType_WhenBound_ThenSingleByDefault() {
    var model = Binder.Bind(Unit(Assign(Name("x"), Int(1))));
    Assert.That(model.ModuleVariables["x"].Type, Is.EqualTo(PbType.Single));
  }

  [Test]
  public void Bind_GivenDefIntRange_WhenBound_ThenLetterRangeTypesInteger() {
    var model = Binder.Bind(Unit(
      new DefTypeStmt(_pos, BuiltinType.Integer, [('i', 'n')]),
      Assign(Name("index"), Int(1)),
      Assign(Name("other"), Int(1))));
    Assert.That(model.ModuleVariables["index"].Type, Is.EqualTo(PbType.Integer));
    Assert.That(model.ModuleVariables["other"].Type, Is.EqualTo(PbType.Single));
  }

  [Test]
  public void Bind_GivenSameNameDifferentSuffixes_WhenBound_ThenDistinctVariables() {
    var model = Binder.Bind(Unit(
      Assign(Name("a", TypeSuffix.Integer), Int(1)),
      Assign(Name("a", TypeSuffix.String), new StringLiteralExpr(_pos, "x"))));
    Assert.That(model.Success, Is.True);
    Assert.That(model.ModuleVariables.Keys, Does.Contain("a%").And.Contain("a$"));
  }

  #endregion

  #region declarations

  [Test]
  public void Bind_GivenDimAsWord_WhenReferencedBare_ThenWordType() {
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [new(_pos, "w", TypeSuffix.None, null, new(_pos, BuiltinType.Word))]);
    var use = Assign(Name("w"), Int(1));
    var model = Binder.Bind(Unit(dim, use));
    Assert.That(model.Success, Is.True);
    Assert.That(model.ModuleVariables["w"].Type, Is.EqualTo(PbType.Word));
  }

  [Test]
  public void Bind_GivenStaticArrayWithEquateBounds_WhenBound_ThenBoundsFolded() {
    var equate = new EquateStmt(_pos, "MAX", Int(15));
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [
      new(_pos, "a", TypeSuffix.None, [(null, new NamedConstantExpr(_pos, "MAX"))], new(_pos, BuiltinType.Word)),
    ]);
    var model = Binder.Bind(Unit(equate, dim));
    Assert.That(model.Success, Is.True);
    var array = (ArrayType)model.ModuleVariables["a()"].Type;
    Assert.That(array.IsDynamic, Is.False);
    Assert.That(array.StaticBounds![0], Is.EqualTo((0, 15)));
  }

  [Test]
  public void Bind_GivenNonConstantBounds_WhenBound_ThenArrayIsDynamic() {
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [
      new(_pos, "a", TypeSuffix.None, [(null, Name("n", TypeSuffix.Integer))], new(_pos, BuiltinType.Word)),
    ]);
    var model = Binder.Bind(Unit(Assign(Name("n", TypeSuffix.Integer), Int(5)), dim));
    var array = (ArrayType)model.ModuleVariables["a()"].Type;
    Assert.That(array.IsDynamic, Is.True);
  }

  [Test]
  public void Bind_GivenTypeDecl_WhenFieldsResolved_ThenPackedOffsets() {
    var t = new TypeDecl(_pos, "Ctx", [
      new(_pos, "flag", new(_pos, BuiltinType.Byte), null),
      new(_pos, "count", new(_pos, BuiltinType.Long), null),
      new(_pos, "name", new(_pos, BuiltinType.FixedString, FixedLength: Int(8)), null),
    ]);
    var model = Binder.Bind(Unit(t));
    var udt = model.Udts["Ctx"];
    Assert.That(udt.Size, Is.EqualTo(1 + 4 + 8));
    Assert.That(udt.FindField("count")!.Offset, Is.EqualTo(1));
    Assert.That(udt.FindField("name")!.Offset, Is.EqualTo(5));
  }

  [Test]
  public void Bind_GivenUdtMemberAccess_WhenBound_ThenFieldType() {
    var t = new TypeDecl(_pos, "Ctx", [new(_pos, "mode", new(_pos, BuiltinType.Word), null)]);
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [new(_pos, "ctx", TypeSuffix.None, null, new(_pos, BuiltinType.None, "Ctx"))]);
    var member = new MemberExpr(_pos, Name("ctx"), "mode", TypeSuffix.None);
    var model = Binder.Bind(Unit(t, dim, Assign(member, Int(1))));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.TypeOf(member), Is.EqualTo(PbType.Word));
  }

  [Test]
  public void Bind_GivenUnknownUdtField_WhenBound_ThenError() {
    var t = new TypeDecl(_pos, "Ctx", [new(_pos, "mode", new(_pos, BuiltinType.Word), null)]);
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [new(_pos, "ctx", TypeSuffix.None, null, new(_pos, BuiltinType.None, "Ctx"))]);
    var model = Binder.Bind(Unit(t, dim, Assign(new MemberExpr(_pos, Name("ctx"), "nope", TypeSuffix.None), Int(1))));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("no field")));
  }

  #endregion

  #region procedures and scoping

  [Test]
  public void Bind_GivenSubWithLocal_WhenBound_ThenLocalNotVisibleAtModuleLevel() {
    var sub = new SubDecl(_pos, "Work", [], false, Visibility.Default, null, false, [
      Assign(Name("temp", TypeSuffix.Integer), Int(1)),
    ]);
    var model = Binder.Bind(Unit(sub));
    Assert.That(model.Procedures["Work"].Variables.ContainsKey("temp%"), Is.True);
    Assert.That(model.ModuleVariables.ContainsKey("temp%"), Is.False);
  }

  [Test]
  public void Bind_GivenSharedModuleVariable_WhenUsedInSub_ThenBindsToModuleSymbol() {
    var dim = new DimStmt(_pos, StorageClass.Dim, true, [new(_pos, "g", TypeSuffix.None, null, new(_pos, BuiltinType.Word))]);
    var use = Assign(Name("g"), Int(2));
    var sub = new SubDecl(_pos, "Work", [], false, Visibility.Default, null, false, [use]);
    var model = Binder.Bind(Unit(dim, sub));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.VariableBindings[use.Target], Is.SameAs(model.ModuleVariables["g"]));
  }

  [Test]
  public void Bind_GivenUnsharedModuleVariable_WhenUsedInSub_ThenNewLocalCreated() {
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [new(_pos, "g", TypeSuffix.None, null, new(_pos, BuiltinType.Word))]);
    var use = Assign(Name("g"), Int(2));
    var sub = new SubDecl(_pos, "Work", [], false, Visibility.Default, null, false, [use]);
    var model = Binder.Bind(Unit(dim, sub));
    Assert.That(model.VariableBindings[use.Target], Is.Not.SameAs(model.ModuleVariables["g"]));
  }

  [Test]
  public void Bind_GivenByValParameter_WhenBound_ThenParameterSymbolTyped() {
    var sub = new SubDecl(_pos, "P", [new(_pos, "x", TypeSuffix.None, new(_pos, BuiltinType.Word), ByVal: true, Seg: false, IsArray: false)],
      false, Visibility.Default, null, false, [Assign(Name("x"), Int(1))]);
    var model = Binder.Bind(Unit(sub));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    var p = model.Procedures["P"].Parameters[0];
    Assert.That(p.ByVal, Is.True);
    Assert.That(p.Type, Is.EqualTo(PbType.Word));
  }

  [Test]
  public void Bind_GivenFunction_WhenNameAssigned_ThenBindsToResultVariable() {
    var assign = Assign(Name("Twice", TypeSuffix.Integer), Int(2));
    var fn = new FunctionDecl(_pos, "Twice", TypeSuffix.Integer, null, [], false, Visibility.Default, null, false, [assign]);
    var model = Binder.Bind(Unit(fn));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.Procedures["Twice"].ReturnType, Is.EqualTo(PbType.Integer));
  }

  [Test]
  public void Bind_GivenFunctionCall_WhenBound_ThenCallBindingAndReturnType() {
    var fn = new FunctionDecl(_pos, "GetVal", TypeSuffix.Long, null, [], false, Visibility.Default, null, false, []);
    var call = new CallOrIndexExpr(_pos, "GetVal", TypeSuffix.None, []);
    var model = Binder.Bind(Unit(fn, Assign(Name("x", TypeSuffix.Long), call)));
    Assert.That(model.TypeOf(call), Is.EqualTo(PbType.Long));
    Assert.That(model.CallBindings.ContainsKey(call), Is.True);
  }

  [Test]
  public void Bind_GivenSubCallWithWrongArity_WhenBound_ThenError() {
    var sub = new SubDecl(_pos, "P", [new(_pos, "x", TypeSuffix.Integer, null, false, false, false)], false, Visibility.Default, null, false, []);
    var call = new CallStmt(_pos, "P", [], true);
    var model = Binder.Bind(Unit(sub, call));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("argument")));
  }

  #endregion

  #region call-or-index classification

  [Test]
  public void Bind_GivenArrayElement_WhenBound_ThenClassifiedAsIndexing() {
    var dim = new DimStmt(_pos, StorageClass.Dim, false, [new(_pos, "a", TypeSuffix.None, [(null, Int(9))], new(_pos, BuiltinType.Word))]);
    var element = new CallOrIndexExpr(_pos, "a", TypeSuffix.None, [Int(3)]);
    var model = Binder.Bind(Unit(dim, Assign(element, Int(1))));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.VariableBindings.ContainsKey(element), Is.True);
    Assert.That(model.TypeOf(element), Is.EqualTo(PbType.Word));
  }

  [Test]
  public void Bind_GivenLenCall_WhenBound_ThenIntrinsicLongResult() {
    var call = new CallOrIndexExpr(_pos, "LEN", TypeSuffix.None, [new StringLiteralExpr(_pos, "abc")]);
    var model = Binder.Bind(Unit(Assign(Name("n", TypeSuffix.Long), call)));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.IntrinsicBindings.ContainsKey(call), Is.True);
    Assert.That(model.TypeOf(call), Is.EqualTo(PbType.Long));
  }

  [Test]
  public void Bind_GivenChrDollarCall_WhenBound_ThenStringResult() {
    // CHR$(65): the lexer stores name CHR with String suffix
    var call = new CallOrIndexExpr(_pos, "CHR", TypeSuffix.String, [Int(65)]);
    var model = Binder.Bind(Unit(Assign(Name("s", TypeSuffix.String), call)));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
    Assert.That(model.TypeOf(call), Is.EqualTo(PbType.String));
  }

  [Test]
  public void Bind_GivenUnknownName_WhenCalled_ThenError() {
    var call = new CallOrIndexExpr(_pos, "Mystery", TypeSuffix.None, [Int(1)]);
    var model = Binder.Bind(Unit(Assign(Name("x"), call)));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("Mystery")));
  }

  #endregion

  #region expression typing

  [Test]
  public void Bind_GivenIntegerDivision_WhenBound_ThenIntegralType() {
    var e = new BinaryExpr(_pos, BinaryOp.IntegerDivide, Int(7), Int(2));
    var model = Binder.Bind(Unit(Assign(Name("x", TypeSuffix.Integer), e)));
    Assert.That(model.TypeOf(e), Is.EqualTo(PbType.Integer));
  }

  [Test]
  public void Bind_GivenSlashDivision_WhenBound_ThenFloatType() {
    var e = new BinaryExpr(_pos, BinaryOp.Divide, Int(7), Int(2));
    var model = Binder.Bind(Unit(Assign(Name("x", TypeSuffix.Double), e)));
    Assert.That(model.TypeOf(e), Is.InstanceOf<ScalarType>());
    Assert.That(((ScalarType)model.TypeOf(e)).IsFloat, Is.True);
  }

  [Test]
  public void Bind_GivenComparison_WhenBound_ThenIntegerType() {
    var e = new BinaryExpr(_pos, BinaryOp.Less, Int(1), Int(2));
    var model = Binder.Bind(Unit(Assign(Name("x", TypeSuffix.Integer), e)));
    Assert.That(model.TypeOf(e), Is.EqualTo(PbType.Integer));
  }

  [Test]
  public void Bind_GivenStringNumericMix_WhenBound_ThenTypeMismatchError() {
    var e = new BinaryExpr(_pos, BinaryOp.Add, new StringLiteralExpr(_pos, "a"), Int(1));
    var model = Binder.Bind(Unit(Assign(Name("s", TypeSuffix.String), e)));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("mismatch")));
  }

  [Test]
  public void Bind_GivenAssignStringToNumeric_WhenBound_ThenTypeMismatchError() {
    var model = Binder.Bind(Unit(Assign(Name("x", TypeSuffix.Integer), new StringLiteralExpr(_pos, "a"))));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("mismatch")));
  }

  #endregion

  #region labels

  [Test]
  public void Bind_GivenGotoKnownLabel_WhenBound_ThenNoError() {
    var model = Binder.Bind(Unit(new LabelStmt(_pos, "top"), new GotoStmt(_pos, "top")));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenGotoUnknownLabel_WhenBound_ThenError() {
    var model = Binder.Bind(Unit(new GotoStmt(_pos, "nowhere")));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("nowhere")));
  }

  [Test]
  public void Bind_GivenOnErrorGotoZero_WhenBound_ThenNoError() {
    var model = Binder.Bind(Unit(new OnErrorStmt(_pos, "0", false)));
    Assert.That(model.Success, Is.True);
  }

  [Test]
  public void Bind_GivenLabelInsideSubBody_WhenGotoFromSameSub_ThenResolves() {
    var sub = new SubDecl(_pos, "P", [], false, Visibility.Default, null, false, [
      new LabelStmt(_pos, "again"),
      new GotoStmt(_pos, "again"),
    ]);
    var model = Binder.Bind(Unit(sub));
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  #endregion

  #region equates

  [Test]
  public void Bind_GivenEquateChain_WhenFolded_ThenTransitive() {
    var model = Binder.Bind(Unit(
      new EquateStmt(_pos, "A", Int(2)),
      new EquateStmt(_pos, "B", new BinaryExpr(_pos, BinaryOp.Multiply, new NamedConstantExpr(_pos, "A"), Int(3)))));
    Assert.That(model.Equates["B"].Integer, Is.EqualTo(6));
  }

  [Test]
  public void Bind_GivenEquateRedefinitionSameValue_WhenBound_ThenTolerated() {
    var model = Binder.Bind(Unit(new EquateStmt(_pos, "A", Int(1)), new EquateStmt(_pos, "A", Int(1))));
    Assert.That(model.Success, Is.True);
  }

  [Test]
  public void Bind_GivenEquateRedefinitionDifferentValue_WhenBound_ThenError() {
    var model = Binder.Bind(Unit(new EquateStmt(_pos, "A", Int(1)), new EquateStmt(_pos, "A", Int(2))));
    Assert.That(model.Errors, Is.Not.Empty);
  }

  [Test]
  public void Bind_GivenUndefinedEquateUse_WhenBound_ThenError() {
    var model = Binder.Bind(Unit(Assign(Name("x"), new NamedConstantExpr(_pos, "GONE"))));
    Assert.That(model.Errors, Has.Some.Matches<Diagnostic>(d => d.Message.Contains("GONE")));
  }

  #endregion
}
