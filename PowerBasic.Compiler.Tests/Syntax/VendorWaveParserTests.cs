using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

/// <summary>
/// Parser/binder coverage for the vendor-corpus wave: BIT statements,
/// ARRAY SORT/SCAN, colon bounds, CDECL optional parameters, EXIT FAR,
/// dotted variable names, REPLACE/ITERATE/WRITE/CHAIN, bare CASE relations,
/// spaced two-character relations and the array/scalar namespace split.
/// </summary>
[TestFixture]
public sealed class VendorWaveParserTests {

  private static SemanticModel Bind(string source) {
    var tokens = Lexer.Tokenize(source, "test.bas");
    var unit = Parser.Parse(tokens, "test.bas");
    return Binder.Bind(unit);
  }

  #region statements

  [Test]
  public void Parse_GivenBitSet_WhenParsed_ThenOpAndOperandsCaptured() {
    var stmt = ParseSingle<BitStmt>("BIT SET x%, 6");
    Assert.Multiple(() => {
      Assert.That(stmt.Op, Is.EqualTo(BitOp.Set));
      Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("x"));
    });
  }

  [Test]
  public void Parse_GivenBitToggle_WhenParsed_ThenOpIsToggle()
    => Assert.That(ParseSingle<BitStmt>("BIT TOGGLE l&, 30").Op, Is.EqualTo(BitOp.Toggle));

  [Test]
  public void Parse_GivenArraySortWithCollate_WhenParsed_ThenPartsCaptured() {
    var stmt = ParseSingle<ArraySortStmt>("ARRAY SORT a$(1) FOR n%, COLLATE c$, DESCEND");
    Assert.Multiple(() => {
      Assert.That(stmt.Array.Name, Is.EqualTo("a"));
      Assert.That(stmt.Count, Is.Not.Null);
      Assert.That(stmt.Collate, Is.Not.Null);
      Assert.That(stmt.Descend, Is.True);
    });
  }

  [Test]
  public void Parse_GivenArrayScan_WhenParsed_ThenRelopAndTargetCaptured() {
    var stmt = ParseSingle<ArrayScanStmt>("ARRAY SCAN a$(1) FOR n%, FROM 2 TO 5, = x$, TO found%");
    Assert.Multiple(() => {
      Assert.That(stmt.Op, Is.EqualTo(CaseComparison.Equal));
      Assert.That(stmt.FromPos, Is.Not.Null);
      Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("found"));
    });
  }

  [Test]
  public void Parse_GivenExitFarAt_WhenParsed_ThenLabelCaptured()
    => Assert.That(ParseSingle<ExitFarStmt>("EXIT FAR AT Cleanup").AtLabel, Is.EqualTo("Cleanup"));

  [Test]
  public void Parse_GivenBareExitFar_WhenParsed_ThenNoLabel()
    => Assert.That(ParseSingle<ExitFarStmt>("EXIT FAR").AtLabel, Is.Null);

  [Test]
  public void Parse_GivenReplace_WhenParsed_ThenAllThreePartsCaptured() {
    var stmt = ParseSingle<ReplaceStmt>("REPLACE \"-\" WITH \"+\" IN t$");
    Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("t"));
  }

  [Test]
  public void Parse_GivenIterateFor_WhenParsed_ThenKindIsFor()
    => Assert.That(ParseSingle<IterateStmt>("ITERATE FOR").Kind, Is.EqualTo(ExitKind.For));

  [Test]
  public void Parse_GivenWriteWithFile_WhenParsed_ThenItemsCaptured() {
    var stmt = ParseSingle<WriteStmt>("WRITE #1, a%, \"x\"");
    Assert.Multiple(() => {
      Assert.That(stmt.FileNumber, Is.Not.Null);
      Assert.That(stmt.Items, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenChain_WhenParsed_ThenNotRun()
    => Assert.That(ParseSingle<ChainStmt>("CHAIN \"NEXT.PBC\"").IsRun, Is.False);

  [Test]
  public void Parse_GivenRunWithTarget_WhenParsed_ThenIsRun()
    => Assert.That(ParseSingle<ChainStmt>("RUN \"OTHER.EXE\"").IsRun, Is.True);

  [Test]
  public void Parse_GivenPokeDollar_WhenParsed_ThenCommandWithTwoArguments() {
    var stmt = ParseSingle<CommandStmt>("POKE$ ofs%, b$");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("POKE$"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
    });
  }

  #endregion

  #region declarations & expressions

  [Test]
  public void Parse_GivenColonBounds_WhenParsed_ThenLowerAndUpperCaptured() {
    var stmt = ParseSingle<DimStmt>("DIM a%(0:13)");
    var (lower, _) = stmt.Variables[0].ArrayBounds![0];
    Assert.That(lower, Is.Not.Null);
  }

  [Test]
  public void Parse_GivenArrayParameterWithDimensionCount_WhenParsed_ThenIsArray() {
    var sub = ParseSingle<SubDecl>("SUB S(arr(1) AS LONG)\nEND SUB");
    Assert.That(sub.Parameters[0].IsArray, Is.True);
  }

  [Test]
  public void Parse_GivenCdeclOptionalParameters_WhenParsed_ThenBracketedAreOptional() {
    var fn = ParseSingle<FunctionDecl>("FUNCTION F CDECL (BYVAL a, BYVAL b [, BYVAL c])\nEND FUNCTION");
    Assert.Multiple(() => {
      Assert.That(fn.Cdecl, Is.True);
      Assert.That(fn.Parameters[1].Optional, Is.False);
      Assert.That(fn.Parameters[2].Optional, Is.True);
    });
  }

  [Test]
  public void Parse_GivenModifierBeforeParameterList_WhenParsed_ThenAccepted() {
    var fn = ParseSingle<FunctionDecl>("FUNCTION F CDECL (BYVAL a)\nEND FUNCTION");
    Assert.That(fn.Parameters, Has.Count.EqualTo(1));
  }

  [Test]
  public void Parse_GivenAnonymousTypedDeclareParameters_WhenParsed_ThenTypesCaptured() {
    var decl = ParseSingle<DeclareStmt>("DECLARE SUB S(BYVAL STRING, INTEGER)");
    Assert.Multiple(() => {
      Assert.That(decl.Parameters, Has.Count.EqualTo(2));
      Assert.That(decl.Parameters![0].ByVal, Is.True);
    });
  }

  [Test]
  public void Parse_GivenDottedDimName_WhenParsed_ThenFlatNameKept() {
    var stmt = ParseSingle<DimStmt>("DIM TL.Char AS BYTE");
    Assert.That(stmt.Variables[0].Name, Is.EqualTo("TL.Char"));
  }

  [Test]
  public void Parse_GivenSpacedGreaterEquals_WhenParsed_ThenOneComparison() {
    var stmt = ParseSingle<IfStmt>("IF a% > = 3 THEN b% = 1");
    Assert.That(((BinaryExpr)stmt.Condition).Op, Is.EqualTo(BinaryOp.GreaterEqual));
  }

  [Test]
  public void Parse_GivenBareCaseRelation_WhenParsed_ThenComparisonCaptured() {
    var stmt = ParseSingle<SelectStmt>("SELECT CASE x%\nCASE = 34\ny% = 1\nEND SELECT");
    Assert.That(stmt.Arms[0].Selectors[0].IsComparison, Is.EqualTo(CaseComparison.Equal));
  }

  [Test]
  public void Parse_GivenTypeAbbreviation_WhenParsed_ThenResolvesToDword() {
    var model = Bind("DIM d AS DWD");
    Assert.That(model.ModuleVariables["d"].Type, Is.EqualTo(PbType.Dword));
  }

  #endregion

  #region binder semantics

  [Test]
  public void Bind_GivenScalarAndArrayWithSameName_WhenBound_ThenBothCoexist() {
    var model = Bind("DIM Pbu(10) AS STRING\nPbu$ = Pbu(1)");
    Assert.Multiple(() => {
      Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
      Assert.That(model.ModuleVariables.ContainsKey("Pbu()"), Is.True);
      Assert.That(model.ModuleVariables.ContainsKey("Pbu$"), Is.True);
    });
  }

  [Test]
  public void Bind_GivenFunctionPseudoVariable_WhenAssigned_ThenBindsToResult() {
    var model = Bind("FUNCTION F%\nFUNCTION = 42\nEND FUNCTION");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenAssignmentToWrongSuffixOfFunctionName_WhenBound_ThenCreatesVariable() {
    // Datestamp& = 0 inside FUNCTION DateStamp??? is a fresh LONG, not a recursive call
    var model = Bind("FUNCTION F???\nF& = 0\nF??? = 1\nEND FUNCTION");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenDottedVariableWithoutType_WhenBound_ThenFlattened() {
    var model = Bind("Max.X = 319\ny! = Max.X");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenMemberAccessOnUdt_WhenBound_ThenStillMemberAccess() {
    var model = Bind("TYPE T\n  X AS INTEGER\nEND TYPE\nDIM Max AS T\nMax.X = 319");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenInstrWithAny_WhenBound_ThenAccepted() {
    var model = Bind("p% = INSTR(a$, ANY \"-/\")");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenVariadicChr_WhenBound_ThenAccepted() {
    var model = Bind("c$ = CHR$(65, 66, 67)");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenOptionSigned_WhenVarPtrBound_ThenIntegerResult() {
    var model = Bind("$OPTION SIGNED\nDIM x%\np% = VARPTR(x%)");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenUboundOfBareArrayName_WhenBound_ThenArrayResolved() {
    var model = Bind("DIM a%(5)\nu% = UBOUND(a%)");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  [Test]
  public void Bind_GivenSharedBareArrayThenDim_WhenBound_ThenCompatible() {
    var model = Bind("SHARED s$()\nDIM s$(1:100)");
    Assert.That(model.Success, Is.True, string.Join("; ", model.Errors));
  }

  #endregion
}
