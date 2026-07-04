using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The back-emitter (<see cref="BasicWriter"/>): turns a bound program back into PB 3.5-compatible
/// PowerBASIC source. Each test binds a snippet, renders it, and (the round-trip contract) re-parses
/// and re-binds the rendered text under the pb35 dialect with zero errors - proving the output is
/// not just plausible text but a program the pb35 front end accepts.
/// </summary>
[TestFixture]
public sealed class BasicWriterTests {

  private static string Render(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return BasicWriter.Render(model, unit);
  }

  /// <summary>Renders the source, then re-binds the rendered text under pb35; asserts no errors.</summary>
  private static string RenderAndRebind(string source, Dialect dialect = Dialect.Pb35) {
    var basic = Render(source, dialect);
    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.That(model2.Errors, Is.Empty, $"pb35 re-bind of:\n{basic}\nerrors: " + string.Join("; ", model2.Errors));
    return basic;
  }

  [Test]
  public void Render_Pb36Ternary_HoistsToTempAndRecompilesUnderPb35() {
    var basic = RenderAndRebind("DIM X AS INTEGER\nX = 0\nDIM Y AS INTEGER\nY = IF(X = 0, 42, 100 \\ X)\nPRINT Y\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("IIF").And.Not.Contain("IF("), "the value-position ternary is hoisted, not emitted as an inline IIF");
    Assert.That(basic, Does.Contain("IF X = 0 THEN"), "hoisted into a real IF/ELSE block (preserving short-circuit)");
  }

  [Test]
  public void Render_Pb36DimInitializer_EmitsPlainTypedDimPlusAssignment() {
    var basic = RenderAndRebind("DIM N = 100000\nPRINT N\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DIM N AS LONG"), "the fused DIM splits into a typed declaration (LONG inferred for the wide literal)");
    Assert.That(basic, Does.Contain("N = 100000"), "plus the spliced assignment");
  }

  [Test]
  public void Render_Pb36DefaultParameter_FillsOmittedArgAtCallSite() {
    var basic = RenderAndRebind("DECLARE FUNCTION Pay%(BYVAL X%, BYVAL Y%)\nPRINT Pay%(5)\nFUNCTION Pay%(BYVAL X%, BYVAL Y% = 10)\n  Pay% = X% + Y%\nEND FUNCTION\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("Pay%(5, 10)").Or.Contain("Pay(5, 10)"), "pb35 has no defaults, so the omitted trailing argument is filled in at the call");
  }

  [Test]
  public void Render_Pb36Overloading_RenamesToDistinctNamesAndRecompiles() {
    var basic = RenderAndRebind("DECLARE FUNCTION Area%(BYVAL S%)\nDECLARE FUNCTION Area%(BYVAL W%, BYVAL H%)\nPRINT Area%(4); Area%(3, 5)\nFUNCTION Area%(BYVAL S%)\n  Area% = S% * S%\nEND FUNCTION\nFUNCTION Area%(BYVAL W%, BYVAL H%)\n  Area% = W% * H%\nEND FUNCTION\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("FUNCTION Area__1"), "the second overload gets a distinct pb35 name");
    Assert.That(basic, Does.Contain("Area__1% = W% * H%"), "and its result is assigned through the renamed function, not a stray local");
  }

  [Test]
  public void Render_Pb36ScaledPointer_LowersToByteArithmetic() {
    var basic = RenderAndRebind("DIM A(2) AS INTEGER\nDIM P AS INTEGER PTR\nP = VARPTR(A(0))\nP = P +* 2\nPRINT @P\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("+*"), "scaled pointer arithmetic is lowered to unscaled byte arithmetic");
    Assert.That(basic, Does.Contain("* 2"), "index scaled by the 2-byte element size");
  }

  [Test]
  public void Render_Pb36TryCatchFinally_LowersToOnErrorAndRecompiles() {
    var basic = RenderAndRebind("DIM X AS INTEGER\nTRY\n  X = 1 \\ 0\nCATCH\n  PRINT \"caught\"\nFINALLY\n  PRINT \"done\"\nEND TRY\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("TRY").And.Not.Contain("CATCH"), "TRY/CATCH/FINALLY is lowered to ON ERROR machinery");
    Assert.That(basic, Does.Contain("ON ERROR GOTO"), "a fault routes to the catch label via ON ERROR");
  }

  [Test]
  public void Render_Pb36TryFinallyOnly_SavesErrAndSharesOneFinallyBody() {
    var basic = RenderAndRebind("TRY\n  PRINT \"b\"\nFINALLY\n  PRINT \"fin\"\nEND TRY\n", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(basic, Does.Not.Contain("ERROR ERR"), "the fault edge re-raises the SAVED code - the finally body could change ERR before the re-raise");
      Assert.That(System.Text.RegularExpressions.Regex.Matches(basic, System.Text.RegularExpressions.Regex.Escape("PRINT \"fin\"")).Count,
        Is.EqualTo(1), "the FINALLY body appears once, shared between the normal and fault edges via GOTO");
    });
  }

  [Test]
  public void Render_Pb36TypeAlias_SubstitutesUnderlyingTypeEverywhere() {
    var basic = RenderAndRebind("TYPE Handle AS DWORD\nDIM h AS Handle\nh = 42\nSUB Take(BYVAL x AS Handle)\n  PRINT x\nEND SUB\nTake(h)\n", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(basic, Does.Not.Contain("Handle"), "the alias is fully resolved away - pb35 never sees the name");
      Assert.That(basic, Does.Contain("AS DWORD"), "declarations carry the underlying type");
    });
  }

  [Test]
  public void Render_Pb36TypeMethod_LiftsToSanitizedProcedureAndRecompiles() {
    var basic = RenderAndRebind("TYPE Counter\n  Value AS LONG\n  SUB Bump(BYVAL by AS LONG)\n    THIS.Value = THIS.Value + by\n  END SUB\nEND TYPE\nDIM c AS Counter\nc.Value = 10\nc.Bump(5)\nPRINT c.Value\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("SUB Counter_Bump(BYREF THIS AS Counter"), "the method lifts to a THIS-receiver SUB with a pb35-valid name and an explicit passing mode");
    Assert.That(basic, Does.Contain("Counter_Bump c, 5"), "the call resolves to the lifted name with the receiver passed first");
  }

  [Test]
  public void Render_Pb36GenericType_MonomorphizesAndRecompiles() {
    var basic = RenderAndRebind("TYPE Box OF T\n  Item AS T\n  SUB Put(BYVAL v AS T)\n    THIS.Item = v\n  END SUB\nEND TYPE\nDIM b AS Box OF LONG\nb.Put(42)\nPRINT b.Item\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("TYPE Box_Long"), "the generic monomorphizes to a concrete TYPE");
    Assert.That(basic, Does.Not.Contain(" AS T"), "the type parameter T is replaced by the concrete type");
  }

  [Test]
  public void Render_Pb36Coroutine_EmitsEnumeratorTypeStateMachineAndRecompiles() {
    var basic = RenderAndRebind("FUNCTION Squares(BYVAL n AS INTEGER) AS LONG\n  DIM i AS INTEGER\n  FOR i = 1 TO n\n    YIELD i * i\n  NEXT\nEND FUNCTION\nDIM v AS LONG\nFOR EACH v IN Squares(4)\n  PRINT v\nNEXT\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("FUNCTION Squares_MoveNext"), "the generator lowers to a MoveNext state machine");
    Assert.That(basic, Does.Match(@"DIM \w+ AS Squares"), "the enumerator local is declared as the synthesized enumerator TYPE");
  }

  [Test]
  public void Render_Pb36NestedProcedure_CallResolvesToLiftedName() {
    var basic = RenderAndRebind("FUNCTION Outer(n AS INTEGER) AS INTEGER\n  DIM total AS INTEGER\n  SUB Bump\n    total = total + n\n  END SUB\n  Bump\n  Bump\n  Outer = total\nEND FUNCTION\nPRINT Outer(7)\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("Outer_Bump"), "the nested SUB lifts to a qualified pb35 name used at both the call and the definition");
  }

  [Test]
  public void Render_Pb36TypedProcPointer_LowersToDwordThunkAndCallDword() {
    var basic = RenderAndRebind("DECLARE FUNCTION Triple&(BYVAL n AS LONG)\nDIM f AS FUNCTION(LONG) AS LONG\nf = CODEPTR32(Triple&)\nPRINT f(8)\nFUNCTION Triple&(BYVAL n AS LONG)\n  Triple& = n * 3\nEND FUNCTION\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DIM f AS DWORD"), "a typed proc pointer is a 32-bit code pointer in pb35");
    Assert.That(basic, Does.Contain("CALL DWORD (f)"), "the call goes through CALL DWORD");
    Assert.That(basic, Does.Match(@"SUB Sthunk\d+\(Sp0 AS LONG, Sresult AS LONG\)"), "a thunk adapts the function to a BYREF result");
  }

  [Test]
  public void Render_Pb36InlineLambda_LiftsToThunkedCodePointer() {
    var basic = RenderAndRebind("DIM square AS FUNCTION(LONG) AS LONG\nsquare = FUNCTION(BYVAL x AS LONG) AS LONG => x * x\nPRINT square(9)\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("=>"), "the lambda is lifted, not emitted as pb36 arrow syntax");
    Assert.That(basic, Does.Match(@"square = CODEPTR32\(Sthunk\d+\)"), "its delegate value is a thunk's code pointer");
  }

  [Test]
  public void Render_Pb36NamedDelegate_RecompilesViaThunk() {
    var basic = RenderAndRebind("DECLARE FUNCTION Comparator(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG\nDIM cmp AS Comparator\ncmp = (a, b) => a - b\nPRINT cmp(9, 4)\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DIM cmp AS DWORD"), "a named-delegate-typed variable is a DWORD code pointer");
    Assert.That(basic, Does.Contain("CALL DWORD (cmp)"), "calls dispatch through the pointer");
  }

  [Test]
  public void Render_PureFunctionFold_EmitsComputedLiteralWhenFoldsSupplied() {
    var src = "FUNCTION Cube&(BYVAL n AS LONG)\n  Cube& = n * n * n\nEND FUNCTION\nPRINT Cube&(4)\n";
    var unit = Parser.Parse(Lexer.Tokenize(src, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);

    var plain = BasicWriter.Render(model, unit);
    Assert.That(plain, Does.Contain("Cube&(4)"), "without the fold map the call is emitted verbatim");

    var folds = PowerBasic.Compiler.CodeGen.OptPureFold.Analyze(model);
    var optimized = BasicWriter.Render(model, unit, folds);
    Assert.That(optimized, Does.Contain("PRINT 64"), "with the fold map the pure call folds to its computed literal");
    Assert.That(optimized, Does.Not.Contain("Cube&(4)"), "the call is gone");
  }

  [Test]
  public void Render_SegmentedPokePeek_LowersToDefSegPlusPlainAccess() {
    var basic = RenderAndRebind("POKE &H4000:100, 65\nDIM v AS INTEGER\nv = PEEK(&H4000:100)\nPRINT v\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DEF SEG = 16384"), "the explicit segment lowers to a DEF SEG");
    Assert.That(basic, Does.Contain("POKE 100, 65"), "the poke keeps just the offset");
    Assert.That(basic, Does.Contain("v = PEEK(100)"), "the segmented peek is hoisted to DEF SEG + PEEK(offset)");
  }

  [Test]
  public void Render_ChainedComparison_DesugarsToAndedRange() {
    var basic = RenderAndRebind("DIM i AS INTEGER\ni = 5\nDIM n AS INTEGER\nn = 10\nIF 0 <= i < n THEN\n  PRINT \"ok\"\nEND IF\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("0 <= i AND i < n"), "the chain becomes (0<=i) AND (i<n), reusing the middle operand");
  }

  [Test]
  public void Render_ChainedComparison_Pb35KeepsLeftAssociative() {
    // gated: under pb35 'a < b < c' stays the classic left-associative (a<b)<c - no behavior change
    var basic = Render("DIM a AS INTEGER\nDIM b AS INTEGER\nDIM c AS INTEGER\nDIM r AS INTEGER\nr = a < b < c\n", Dialect.Pb35);
    Assert.That(basic, Does.Not.Contain(" AND "), "pb35 does not chain - it keeps left-associative comparison");
  }

  [Test]
  public void Render_NullConditional_LowersToHasValueTernary() {
    var basic = RenderAndRebind("TYPE Point\n  X AS LONG\n  Y AS LONG\nEND TYPE\nDIM p AS Point?\np.HasValue = -1\np.Value.Y = 7\nDIM r AS LONG\nr = p?.Y ?? -1\nPRINT r\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("?."), "the null-conditional operator is lowered, not emitted verbatim");
    Assert.That(basic, Does.Contain("IF p.HasValue THEN"), "it short-circuits on HasValue");
    Assert.That(basic, Does.Contain("p.Value.Y"), "reading the value's member when present");
  }

  [Test]
  public void Render_Events_LowerToHandlerArrayAddRemoveAndCallDwordLoop() {
    var basic = RenderAndRebind("DECLARE SUB ClickProc(BYVAL x AS LONG)\nDECLARE SUB Log1(BYVAL x AS LONG)\nEVENT OnClick AS ClickProc\nOnClick += CODEPTR32(Log1)\nOnClick(42)\nOnClick -= CODEPTR32(Log1)\nSUB Log1(BYVAL x AS LONG)\n  PRINT x\nEND SUB\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DIM OnClick__evh(31) AS DWORD"), "the event lowers to a fixed DWORD handler array");
    Assert.That(basic, Does.Contain("OnClick__evh(OnClick__evn) = CODEPTR32(Log1)"), "+= appends the handler pointer");
    Assert.That(basic, Does.Contain("CALL DWORD (OnClick__evh("), "invoking the event calls each handler via CALL DWORD through the array element");
    Assert.That(basic, Does.Match(@"OnClick__evh__a\d+_0& = 42"), "the raise argument is hoisted once into a typed temp (assignment-coerced to the delegate parameter type)");
    Assert.That(basic, Does.Not.Contain("IF -1"), "no artificial IF -1 grouping wrapper survives");
  }

  [Test]
  public void Render_FirstClassEventRaise_AllCallSyntaxesLowerToRaiseLoop() {
    var basic = RenderAndRebind("DECLARE SUB P(BYVAL x AS LONG)\nDECLARE SUB H(BYVAL x AS LONG)\nEVENT E AS P\nE += H\nE(1)\nE 2\nCALL E(3)\nSUB H(BYVAL x AS LONG)\n  PRINT x\nEND SUB\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("E__evh(E__evn) = CODEPTR32(H)"), "+= with a bare name takes the address implicitly");
    Assert.That(System.Text.RegularExpressions.Regex.Matches(basic, @"CALL DWORD \(E__evh\(").Count, Is.EqualTo(3), "E(1), E 2 and CALL E(3) each raise");
  }

  [Test]
  public void Render_SubLambda_AndDirectDelegateInvocation_RoundTrips() {
    var basic = RenderAndRebind("DIM x = SUB(y AS INTEGER) PRINT y * 2\nx 15\nCALL x(20)\n", Dialect.Pb36);
    Assert.That(basic, Does.Match(@"SUB S_lambda_1\(BYVAL y AS INTEGER\)"), "the SUB lambda lifts to an anonymous SUB with BYVAL params");
    Assert.That(basic, Does.Contain("x = CODEPTR32(Sthunk1)"), "its value is a thunk code pointer");
    Assert.That(System.Text.RegularExpressions.Regex.Matches(basic, @"CALL DWORD \(x\)").Count, Is.EqualTo(2), "x 15 and CALL x(20) both invoke through the pointer");
  }

  [Test]
  public void Render_ImplicitCodePtr_OnDelegateAssignment() {
    var basic = RenderAndRebind("DECLARE FUNCTION Triple(BYVAL n AS LONG) AS LONG\nDIM f AS FUNCTION(LONG) AS LONG\nf = Triple\nPRINT f(8)\nFUNCTION Triple(BYVAL n AS LONG) AS LONG\n  Triple = n * 3\nEND FUNCTION\n", Dialect.Pb36);
    Assert.That(basic, Does.Match(@"f = CODEPTR32\(Sthunk\d+\)"), "a bare procedure name assigned to a delegate takes its address implicitly");
  }

  [Test]
  public void Render_ArraySliceSpread_ExpandsToElementReads() {
    var basic = RenderAndRebind("DIM b(4) AS INTEGER\nb(3) = 33\nDIM a = {1, ..b(2 TO 3), ..b(TO ^5), ..b(^2 TO)}\nPRINT a(2)\n", Dialect.Pb36);
    Assert.That(basic, Does.Contain("DIM a(5)"), "1 + slice(2) + slice(1) + slice(2) = 6 elements, sized at compile time");
    Assert.That(basic, Does.Contain("a(1) = b(2)"), "..b(2 TO 3) reads elements 2..3");
    Assert.That(basic, Does.Contain("a(3) = b(0)"), "..b(TO ^5) is element 0 (from-end resolved against UBOUND 4)");
    Assert.That(basic, Does.Contain("a(4) = b(3)"), "..b(^2 TO) starts at UBOUND-2+1 = 3");
  }

  [Test]
  public void Render_Assignment_RoundTripsExpressionWithMinimalParens() {
    var basic = RenderAndRebind("A% = 2 + 3 * 4\nPRINT A%\n");
    Assert.That(basic, Does.Contain("A% = 2 + 3 * 4"), "multiply binds tighter than add - no parens needed");
    Assert.That(basic, Does.Contain("PRINT A%"));
  }

  [Test]
  public void Render_AddsParens_WherePrecedenceRequiresThem() {
    var basic = RenderAndRebind("A% = (2 + 3) * 4\n");
    Assert.That(basic, Does.Contain("(2 + 3) * 4"), "the lower-precedence add under a multiply keeps its parens");
  }

  [Test]
  public void Render_IfThenElse_ReconstructsBlockWithIndentedBody() {
    var basic = RenderAndRebind("IF A% > 0 THEN\n  PRINT \"pos\"\nELSE\n  PRINT \"neg\"\nEND IF\n");
    Assert.That(basic, Does.Contain("IF A% > 0 THEN"));
    Assert.That(basic, Does.Contain("ELSE"));
    Assert.That(basic, Does.Contain("END IF"));
    Assert.That(basic, Does.Contain("  PRINT \"pos\""), "the THEN body is indented one level");
  }

  [Test]
  public void Render_ForLoop_ReconstructsHeaderAndNext() {
    var basic = RenderAndRebind("FOR I% = 1 TO 10 STEP 2\n  PRINT I%\nNEXT\n");
    Assert.That(basic, Does.Contain("FOR I% = 1 TO 10 STEP 2"));
    Assert.That(basic, Does.Contain("NEXT"));
  }

  [Test]
  public void Render_Procedure_ReconstructsSignatureAndBody() {
    var basic = RenderAndRebind("FUNCTION Add%(BYVAL X%, BYVAL Y%)\n  Add% = X% + Y%\nEND FUNCTION\n");
    Assert.That(basic, Does.Contain("FUNCTION Add"));
    Assert.That(basic, Does.Contain("END FUNCTION"));
    Assert.That(basic, Does.Contain("X% + Y%"));
  }

  [Test]
  public void Render_SelectCase_ReconstructsArmsAndElse() {
    var basic = RenderAndRebind("SELECT CASE A%\nCASE 1\n  PRINT \"one\"\nCASE ELSE\n  PRINT \"other\"\nEND SELECT\n");
    Assert.That(basic, Does.Contain("SELECT CASE A%"));
    Assert.That(basic, Does.Contain("CASE 1"));
    Assert.That(basic, Does.Contain("CASE ELSE"));
    Assert.That(basic, Does.Contain("END SELECT"));
  }

  [Test]
  public void Render_FileIo_ReconstructsOpenPrintClose() {
    var basic = RenderAndRebind("OPEN \"R.TXT\" FOR OUTPUT AS #1\nPRINT #1, 6 \\ 2\nCLOSE #1\n");
    Assert.That(basic, Does.Contain("OPEN \"R.TXT\" FOR OUTPUT AS #1"));
    Assert.That(basic, Does.Contain("PRINT #1, 6 \\ 2"), "the file-number expression renders as #1, not a fallback comment");
    Assert.That(basic, Does.Contain("CLOSE #1"));
  }

  [Test]
  public void Render_OnErrorDisable_EmitsGotoZero() {
    var basic = RenderAndRebind("ON ERROR GOTO Trap\nA% = 1\nON ERROR GOTO 0\nTrap:\nRESUME NEXT\n");
    Assert.That(basic, Does.Contain("ON ERROR GOTO 0"), "the disable form keeps its 0 target");
  }

  [Test]
  public void Render_Type_ReconstructsTypeBlock() {
    var basic = RenderAndRebind("TYPE Pt\n  X AS INTEGER\n  Y AS INTEGER\nEND TYPE\nDIM P AS Pt\nP.X = 5\n");
    Assert.That(basic, Does.Contain("TYPE Pt"));
    Assert.That(basic, Does.Contain("X AS INTEGER"));
    Assert.That(basic, Does.Contain("END TYPE"));
    Assert.That(basic, Does.Contain("P.X = 5"));
  }

  [Test]
  public void Render_Pb36FromEndIndex_LowersToPb35ViaSideTable() {
    // a pb36 value-position construct the binder records a desugar for (arr(^1) -> UBOUND(arr)-1+1)
    // must come back as the pb35 core form, not the pb36 surface syntax.
    var basic = RenderAndRebind("DIM A%(10)\nA%(10) = 7\nDIM L%\nL% = A%(^1)\nPRINT L%\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("^"), "the from-end index is lowered, not emitted as arr(^1)");
    Assert.That(basic, Does.Not.Contain("[unsupported:"), "no node is dropped to a fallback comment");
  }

  [Test]
  public void Render_Pb36InterpolatedString_LowersToConcatenation() {
    // $"...{x}..." has no pb35 form; the binder desugars it to concat/STR$, which must round-trip.
    var basic = RenderAndRebind("DIM N%\nN% = 42\nDIM S$\nS$ = $\"n={N%}\"\nPRINT S$\n", Dialect.Pb36);
    Assert.That(basic, Does.Not.Contain("$\""), "the interpolated string is lowered, not emitted as $\"...\"");
    Assert.That(basic, Does.Not.Contain("[unsupported:"));
  }

  [Test]
  public void Render_NonPb35Dialect_EmitsCompatDirective_AndReboundUnderPb35SetsCompatDialect() {
    // A non-pb35 program is transpiled with a $COMPAT directive so the pb35 recompile replicates the
    // source dialect's runtime quirks (formatting, 16-bit arithmetic, rounding, VAL, ^Z, folding).
    var unit = Parser.Parse(Lexer.Tokenize("A% = 1\nPRINT A%\n", "T.BAS", Dialect.Qb45), "T.BAS", Dialect.Qb45);
    var model = Binder.Bind(unit, Dialect.Qb45);
    var basic = BasicWriter.Render(model, unit);
    Assert.That(basic, Does.StartWith("$COMPAT qb45"), "the source dialect is recorded for the pb35 recompile");

    var unit2 = Parser.Parse(Lexer.Tokenize(basic, "RT.BAS", Dialect.Pb35), "RT.BAS", Dialect.Pb35);
    var model2 = Binder.Bind(unit2, Dialect.Pb35);
    Assert.Multiple(() => {
      Assert.That(model2.Errors, Is.Empty, "the $COMPAT program binds clean under pb35");
      Assert.That(model2.CompatDialect, Is.EqualTo(Dialect.Qb45), "$COMPAT qb45 sets the compatibility dialect");
      Assert.That(model2.EffectiveDialect, Is.EqualTo(Dialect.Qb45), "runtime quirk emulation follows the $COMPAT dialect");
    });
  }

  [Test]
  public void Render_Pb35Source_EmitsNoCompatDirective() {
    var basic = RenderAndRebind("A% = 1\nPRINT A%\n", Dialect.Pb35);
    Assert.That(basic, Does.Not.Contain("$COMPAT"), "pb35 is the identity target - no compatibility directive");
  }

  [Test]
  public void Render_NeverDropsAStatement_NoFallbackComment() {
    var basic = RenderAndRebind("PRINT \"hi\"\n");
    Assert.That(basic, Does.Contain("PRINT \"hi\""));
    Assert.That(basic, Does.Not.Contain("[unsupported:"), "a plain PRINT needs no fallback comment");
  }
}
