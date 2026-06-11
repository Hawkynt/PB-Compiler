using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;
using static PowerBasic.Compiler.Tests.Syntax.ParserTestHelper;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class ParserDeclarationTests {

  #region assignment & equates

  [Test]
  public void Parse_GivenSimpleAssignment_WhenParsed_ThenTargetAndValueAreCaptured() {
    var stmt = ParseSingle<AssignStmt>("x = 1");
    Assert.Multiple(() => {
      Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("x"));
      Assert.That(((IntegerLiteralExpr)stmt.Value).Value, Is.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenLetAssignment_WhenParsed_ThenItIsAnAssignStmt()
    => Assert.That(((NameExpr)ParseSingle<AssignStmt>("LET x = 1").Target).Name, Is.EqualTo("x"));

  [Test]
  public void Parse_GivenArrayElementAssignment_WhenParsed_ThenTargetIsCallOrIndex() {
    var stmt = ParseSingle<AssignStmt>("paletteV(i * 3 + 1) = g");
    Assert.That(stmt.Target, Is.InstanceOf<CallOrIndexExpr>());
  }

  [Test]
  public void Parse_GivenIndexedMemberAssignment_WhenParsed_ThenTargetIsMemberOfIndex() {
    var stmt = ParseSingle<AssignStmt>("ctx.NamedTimers(n).Active = 1");
    Assert.That(((MemberExpr)stmt.Target).Target, Is.InstanceOf<IndexExpr>());
  }

  [Test]
  public void Parse_GivenEquateDefinition_WhenParsed_ThenEquateStmtIsProduced() {
    var stmt = ParseSingle<EquateStmt>("%MAX_SPRITES = 32");
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("MAX_SPRITES"));
      Assert.That(((IntegerLiteralExpr)stmt.Value).Value, Is.EqualTo(32));
    });
  }

  #endregion

  #region DIM family

  [Test]
  public void Parse_GivenDimWithType_WhenParsed_ThenTypeIsAttached() {
    var stmt = ParseSingle<DimStmt>("DIM x AS WORD");
    Assert.Multiple(() => {
      Assert.That(stmt.Storage, Is.EqualTo(StorageClass.Dim));
      Assert.That(stmt.Variables, Has.Count.EqualTo(1));
      Assert.That(stmt.Variables[0].Type!.Builtin, Is.EqualTo(BuiltinType.Word));
    });
  }

  [Test]
  public void Parse_GivenDimWithMultipleVariables_WhenParsed_ThenAllAreCaptured() {
    var stmt = ParseSingle<DimStmt>("DIM a AS BYTE, b(10) AS WORD, c$");
    Assert.Multiple(() => {
      Assert.That(stmt.Variables, Has.Count.EqualTo(3));
      Assert.That(stmt.Variables[1].ArrayBounds, Has.Count.EqualTo(1));
      Assert.That(stmt.Variables[2].Suffix, Is.EqualTo(TypeSuffix.String));
      Assert.That(stmt.Variables[2].Type, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenDimWithExplicitBounds_WhenParsed_ThenLowerAndUpperAreKept() {
    var stmt = ParseSingle<DimStmt>("DIM y(1 TO 5, 0 TO 3) AS WORD");
    var bounds = stmt.Variables[0].ArrayBounds!;
    Assert.Multiple(() => {
      Assert.That(bounds, Has.Count.EqualTo(2));
      Assert.That(((IntegerLiteralExpr)bounds[0].Lower!).Value, Is.EqualTo(1));
      Assert.That(((IntegerLiteralExpr)bounds[1].Upper).Value, Is.EqualTo(3));
    });
  }

  [Test]
  public void Parse_GivenDimAsSharedType_WhenParsed_ThenSharedFlagIsSet() {
    var stmt = ParseSingle<DimStmt>("DIM ctx AS SHARED CursorContextType");
    Assert.Multiple(() => {
      Assert.That(stmt.SharedFlag, Is.True);
      Assert.That(stmt.Variables[0].Type!.UserTypeName, Is.EqualTo("CursorContextType"));
    });
  }

  [Test]
  public void Parse_GivenFixedStringDim_WhenParsed_ThenFixedLengthIsKept() {
    var stmt = ParseSingle<DimStmt>("DIM s AS STRING * 12");
    var type = stmt.Variables[0].Type!;
    Assert.Multiple(() => {
      Assert.That(type.Builtin, Is.EqualTo(BuiltinType.FixedString));
      Assert.That(((IntegerLiteralExpr)type.FixedLength!).Value, Is.EqualTo(12));
    });
  }

  [TestCase("LOCAL i AS INTEGER", StorageClass.Local)]
  [TestCase("STATIC i AS INTEGER", StorageClass.Static)]
  [TestCase("SHARED i AS INTEGER", StorageClass.Shared)]
  [TestCase("PUBLIC i AS INTEGER", StorageClass.Public)]
  [TestCase("EXT i AS INTEGER", StorageClass.Ext)]
  public void Parse_GivenStorageKeyword_WhenParsed_ThenStorageClassMatches(string source, StorageClass expected)
    => Assert.That(ParseSingle<DimStmt>(source).Storage, Is.EqualTo(expected));

  [Test]
  public void Parse_GivenCommonSharedBlock_WhenParsed_ThenBlockNameIsKept() {
    var stmt = ParseSingle<DimStmt>("COMMON SHARED /video/ a, b()");
    Assert.Multiple(() => {
      Assert.That(stmt.Storage, Is.EqualTo(StorageClass.Common));
      Assert.That(stmt.SharedFlag, Is.True);
      Assert.That(stmt.CommonBlock, Is.EqualTo("video"));
      Assert.That(stmt.Variables[1].ArrayBounds, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenRedim_WhenParsed_ThenVariablesAreCaptured() {
    var stmt = ParseSingle<RedimStmt>("REDIM LZWDictionaryStore(4095)");
    Assert.That(stmt.Variables[0].ArrayBounds, Has.Count.EqualTo(1));
  }

  [Test]
  public void Parse_GivenErase_WhenParsed_ThenAllArraysAreListed() {
    var stmt = ParseSingle<EraseStmt>("ERASE a, b");
    Assert.That(stmt.Arrays.Select(a => a.Name), Is.EqualTo(new[] { "a", "b" }));
  }

  #endregion

  #region TYPE / UNION

  [Test]
  public void Parse_GivenTypeDeclaration_WhenParsed_ThenFieldsAreCaptured() {
    var stmt = ParseSingle<TypeDecl>("""
      TYPE SVGAScreen
          XRes AS WORD
          Name AS STRING * 4
          NamedTimers(8) AS NamedTimerType
      END TYPE
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("SVGAScreen"));
      Assert.That(stmt.Fields, Has.Count.EqualTo(3));
      Assert.That(stmt.Fields[1].Type.Builtin, Is.EqualTo(BuiltinType.FixedString));
      Assert.That(stmt.Fields[2].ArrayBounds, Has.Count.EqualTo(1));
      Assert.That(stmt.Fields[2].Type.UserTypeName, Is.EqualTo("NamedTimerType"));
    });
  }

  [Test]
  public void Parse_GivenTypeFieldWithRangeBounds_WhenParsed_ThenLowerBoundIsKept() {
    var stmt = ParseSingle<TypeDecl>("""
      TYPE T
          slots(1 TO 8) AS WORD
      END TYPE
      """);
    Assert.That(((IntegerLiteralExpr)stmt.Fields[0].ArrayBounds![0].Lower!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Parse_GivenUnionDeclaration_WhenParsed_ThenUnionDeclIsProduced() {
    var stmt = ParseSingle<UnionDecl>("""
      UNION Overlay
          w AS WORD
          l AS LONG
      END UNION
      """);
    Assert.That(stmt.Fields, Has.Count.EqualTo(2));
  }

  #endregion

  #region SUB / FUNCTION / DECLARE

  [Test]
  public void Parse_GivenEmptySub_WhenParsed_ThenBodyIsEmpty() {
    var stmt = ParseSingle<SubDecl>("""
      SUB Svga_InitDispatchTable
      END SUB
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("Svga_InitDispatchTable"));
      Assert.That(stmt.Parameters, Is.Empty);
      Assert.That(stmt.Body, Is.Empty);
    });
  }

  [Test]
  public void Parse_GivenSubWithParameters_WhenParsed_ThenModifiersAreCaptured() {
    var stmt = ParseSingle<SubDecl>("""
      SUB Plot(BYVAL x AS WORD, SEG buffer AS ANY, paletteV() AS BYTE, c)
      END SUB
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Parameters, Has.Count.EqualTo(4));
      Assert.That(stmt.Parameters[0].ByVal, Is.True);
      Assert.That(stmt.Parameters[1].Seg, Is.True);
      Assert.That(stmt.Parameters[1].Type!.Builtin, Is.EqualTo(BuiltinType.Any));
      Assert.That(stmt.Parameters[2].IsArray, Is.True);
      Assert.That(stmt.Parameters[3].Type, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenStaticSub_WhenParsed_ThenStaticFlagIsSet() {
    var stmt = ParseSingle<SubDecl>("""
      SUB Tick STATIC
      END SUB
      """);
    Assert.That(stmt.IsStatic, Is.True);
  }

  [Test]
  public void Parse_GivenFunctionWithReturnType_WhenParsed_ThenReturnTypeIsKept() {
    var stmt = ParseSingle<FunctionDecl>("""
      FUNCTION Cursor_IsVisible AS BYTE
          Cursor_IsVisible = 1
      END FUNCTION
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.ReturnType!.Builtin, Is.EqualTo(BuiltinType.Byte));
      Assert.That(stmt.Body, Has.Count.EqualTo(1));
      Assert.That(stmt.Body[0], Is.InstanceOf<AssignStmt>());
    });
  }

  [Test]
  public void Parse_GivenSuffixedFunctionName_WhenParsed_ThenSuffixIsTheReturnType() {
    var stmt = ParseSingle<FunctionDecl>("""
      FUNCTION GetName$(BYVAL handle AS BYTE)
      END FUNCTION
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Suffix, Is.EqualTo(TypeSuffix.String));
      Assert.That(stmt.ReturnType, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenDeclareSubWithoutParameters_WhenParsed_ThenParametersAreNull() {
    var stmt = ParseSingle<DeclareStmt>("DECLARE SUB Beep2");
    Assert.Multiple(() => {
      Assert.That(stmt.IsFunction, Is.False);
      Assert.That(stmt.Parameters, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenDeclareFunction_WhenParsed_ThenSignatureIsCaptured() {
    var stmt = ParseSingle<DeclareStmt>("DECLARE FUNCTION Svga_GetPixel(BYVAL x AS WORD, BYVAL y AS WORD) AS BYTE");
    Assert.Multiple(() => {
      Assert.That(stmt.IsFunction, Is.True);
      Assert.That(stmt.Parameters, Has.Count.EqualTo(2));
      Assert.That(stmt.ReturnType!.Builtin, Is.EqualTo(BuiltinType.Byte));
    });
  }

  #endregion

  #region DEF FN / DEFtype / DEF SEG

  [Test]
  public void Parse_GivenSingleLineDefFn_WhenParsed_ThenExpressionBodyIsKept() {
    var stmt = ParseSingle<DefFnDecl>("DEF FNAdd(a, b) = a + b");
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("FNAdd"));
      Assert.That(stmt.Parameters, Has.Count.EqualTo(2));
      Assert.That(stmt.Body, Is.InstanceOf<BinaryExpr>());
      Assert.That(stmt.BlockBody, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenBlockDefFn_WhenParsed_ThenBlockBodyIsKept() {
    var stmt = ParseSingle<DefFnDecl>("""
      DEF FNMax(a, b)
          IF a > b THEN FNMax = a ELSE FNMax = b
      END DEF
      """);
    Assert.Multiple(() => {
      Assert.That(stmt.Body, Is.Null);
      Assert.That(stmt.BlockBody, Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Parse_GivenDefIntRanges_WhenParsed_ThenRangesAreCaptured() {
    var stmt = ParseSingle<DefTypeStmt>("DEFINT a-k, x-z");
    Assert.Multiple(() => {
      Assert.That(stmt.Type, Is.EqualTo(BuiltinType.Integer));
      Assert.That(stmt.Ranges, Is.EqualTo(new[] { ('A', 'K'), ('X', 'Z') }));
    });
  }

  [Test]
  public void Parse_GivenDefSngSingleLetter_WhenParsed_ThenRangeCollapses()
    => Assert.That(ParseSingle<DefTypeStmt>("DEFSNG c").Ranges, Is.EqualTo(new[] { ('C', 'C') }));

  [Test]
  public void Parse_GivenBareDefSeg_WhenParsed_ThenSegmentIsNull()
    => Assert.That(ParseSingle<DefSegStmt>("DEF SEG").Segment, Is.Null);

  [Test]
  public void Parse_GivenDefSegWithAddress_WhenParsed_ThenSegmentIsKept()
    => Assert.That(((IntegerLiteralExpr)ParseSingle<DefSegStmt>("DEF SEG = &HA000").Segment!).Value, Is.EqualTo(unchecked((short)0xA000))); // radix literals read signed (PB 3.1+)

  #endregion

  #region calls

  [Test]
  public void Parse_GivenCallKeyword_WhenParsed_ThenFlagIsSet() {
    var stmt = ParseSingle<CallStmt>("CALL Vga_PutPixel(x, y, c)");
    Assert.Multiple(() => {
      Assert.That(stmt.Name, Is.EqualTo("Vga_PutPixel"));
      Assert.That(stmt.UsedCallKeyword, Is.True);
      Assert.That(stmt.Arguments, Has.Count.EqualTo(3));
    });
  }

  [Test]
  public void Parse_GivenCallWithoutArguments_WhenParsed_ThenArgumentsAreEmpty()
    => Assert.That(ParseSingle<CallStmt>("CALL Timer_Init").Arguments, Is.Empty);

  [Test]
  public void Parse_GivenBareCallWithArguments_WhenParsed_ThenItIsACallStmt() {
    var stmt = ParseSingle<CallStmt>("MyProc a, b + 1");
    Assert.Multiple(() => {
      Assert.That(stmt.UsedCallKeyword, Is.False);
      Assert.That(stmt.Arguments, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenBareCallWithParens_WhenParsed_ThenArgumentsAreParsed()
    => Assert.That(ParseSingle<CallStmt>("MyProc(1, 2)").Arguments, Has.Count.EqualTo(2));

  [Test]
  public void Parse_GivenBareIdentifierAlone_WhenParsed_ThenItIsACallWithoutArguments()
    => Assert.That(ParseSingle<CallStmt>("Cursor_Init").Arguments, Is.Empty);

  [Test]
  public void Parse_GivenCallInterrupt_WhenParsed_ThenItBecomesACommand() {
    var stmt = ParseSingle<CommandStmt>("CALL INTERRUPT &H10");
    Assert.Multiple(() => {
      Assert.That(stmt.Keyword, Is.EqualTo("INTERRUPT"));
      Assert.That(((IntegerLiteralExpr)stmt.Arguments[0]!).Value, Is.EqualTo(0x10));
    });
  }

  #endregion

  #region INCR/DECR, SWAP, MID$, LSET/RSET

  [Test]
  public void Parse_GivenIncrWithoutAmount_WhenParsed_ThenAmountIsNull() {
    var stmt = ParseSingle<IncrDecrStmt>("INCR i");
    Assert.Multiple(() => {
      Assert.That(stmt.Increment, Is.True);
      Assert.That(stmt.Amount, Is.Null);
    });
  }

  [Test]
  public void Parse_GivenDecrWithAmount_WhenParsed_ThenAmountIsKept() {
    var stmt = ParseSingle<IncrDecrStmt>("DECR count, 2");
    Assert.Multiple(() => {
      Assert.That(stmt.Increment, Is.False);
      Assert.That(((IntegerLiteralExpr)stmt.Amount!).Value, Is.EqualTo(2));
    });
  }

  [Test]
  public void Parse_GivenSwap_WhenParsed_ThenBothSidesAreCaptured() {
    var stmt = ParseSingle<SwapStmt>("SWAP a, b(2)");
    Assert.Multiple(() => {
      Assert.That(stmt.Left, Is.InstanceOf<NameExpr>());
      Assert.That(stmt.Right, Is.InstanceOf<CallOrIndexExpr>());
    });
  }

  [Test]
  public void Parse_GivenMidAssignment_WhenParsed_ThenAllPartsAreCaptured() {
    var stmt = ParseSingle<MidAssignStmt>("MID$(s$, idx, 1) = CHR$(65)");
    Assert.Multiple(() => {
      Assert.That(((NameExpr)stmt.Target).Name, Is.EqualTo("s"));
      Assert.That(((NameExpr)stmt.Start).Name, Is.EqualTo("idx"));
      Assert.That(((IntegerLiteralExpr)stmt.Length!).Value, Is.EqualTo(1));
      Assert.That(stmt.Value, Is.InstanceOf<CallOrIndexExpr>());
    });
  }

  [Test]
  public void Parse_GivenMidAssignmentWithoutLength_WhenParsed_ThenLengthIsNull()
    => Assert.That(ParseSingle<MidAssignStmt>("MID$(s$, 2) = t$").Length, Is.Null);

  [Test]
  public void Parse_GivenMidAsFunctionOnRightSide_WhenParsed_ThenItStaysAnExpression()
    => Assert.That(ParseSingle<AssignStmt>("c = MID$(s$, 1, 1)").Value, Is.InstanceOf<CallOrIndexExpr>());

  [Test]
  public void Parse_GivenLset_WhenParsed_ThenLeftFlagIsSet() {
    var stmt = ParseSingle<LsetRsetStmt>("LSET buffer$ = data$");
    Assert.That(stmt.IsLeft, Is.True);
  }

  [Test]
  public void Parse_GivenRset_WhenParsed_ThenLeftFlagIsCleared()
    => Assert.That(ParseSingle<LsetRsetStmt>("RSET f$ = v$").IsLeft, Is.False);

  #endregion

  #region labels, asm, meta

  [Test]
  public void Parse_GivenIdentifierLabel_WhenParsed_ThenLabelStmtIsProduced()
    => Assert.That(ParseSingle<LabelStmt>("NextSaveX:").Name, Is.EqualTo("NextSaveX"));

  [Test]
  public void Parse_GivenNumericLineNumber_WhenParsed_ThenLabelUsesTheNumber() {
    var unit = Parse("100 CLS");
    Assert.Multiple(() => {
      Assert.That(((LabelStmt)unit.Statements[0]).Name, Is.EqualTo("100"));
      Assert.That(unit.Statements[1], Is.InstanceOf<CommandStmt>());
    });
  }

  [Test]
  public void Parse_GivenLabelThenStatementOnSameLine_WhenParsed_ThenBothAreProduced() {
    var unit = Parse("Done: RETURN");
    Assert.Multiple(() => {
      Assert.That(unit.Statements[0], Is.InstanceOf<LabelStmt>());
      Assert.That(unit.Statements[1], Is.InstanceOf<ReturnStmt>());
    });
  }

  [Test]
  public void Parse_GivenInlineAsm_WhenParsed_ThenTextIsKept()
    => Assert.That(ParseSingle<InlineAsmStmt>("!MOV ES, emsPageFrame").Text, Is.EqualTo("MOV ES, emsPageFrame"));

  [Test]
  public void Parse_GivenMetaCommand_WhenParsed_ThenArgumentsRunToEndOfLine() {
    var stmt = ParseSingle<MetaStmt>("$STACK 2048");
    Assert.Multiple(() => {
      Assert.That(stmt.Command, Is.EqualTo("STACK"));
      Assert.That(stmt.Arguments, Has.Count.EqualTo(1));
      Assert.That(stmt.Arguments[0].IntegerValue, Is.EqualTo(2048));
    });
  }

  #endregion
}
