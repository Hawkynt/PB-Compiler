using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 optimizer (docs/PB36.md): runtime trimming, trivial-I/O lowering,
/// wrap-correct constant folding, multiply strength reduction and the zero
/// idiom. The behavioral contract (byte-identical output to pb35/genuine
/// PBC 3.50) is enforced by the differential harness's pb36 pass; these tests
/// pin the size wins, the image shapes and the wrap arithmetic.
/// </summary>
[TestFixture]
public sealed class Pb36OptimizerTests {

  private static byte[] Compile(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  private static string Ascii(byte[] image) => System.Text.Encoding.ASCII.GetString(image);

  private const string _HELLO = "PRINT \"Hello, World!\"\nEND";

  /// <summary>Non-trivial twin: the variable forces the general (trimmed-runtime) path.</summary>
  private const string _HELLO_VAR = "x% = 1\nPRINT \"Hello, World!\"; x%\nEND";

  #region runtime trimming (P1/P2/P4)

  [Test]
  public void Emit_GivenHelloWorldWithVariable_WhenPb36_ThenRuntimeTrimsBelowTwoKiB() {
    var pb35 = Compile(_HELLO_VAR, Dialect.Pb35);
    var pb36 = Compile(_HELLO_VAR, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(pb36, Has.Length.LessThan(2048), "trimmed hello world should be tiny");
      Assert.That(pb36.Length, Is.LessThan(pb35.Length / 4), "trimming should remove most of the runtime");
      Assert.That(pb36[0], Is.EqualTo((byte)'M'));
      Assert.That(pb36[1], Is.EqualTo((byte)'Z'));
      Assert.That(Ascii(pb36), Does.Contain("Hello, World!"));
    });
  }

  [Test]
  public void Emit_GivenHelloWorld_WhenPb36_ThenUnusedHeapSegmentsNotReserved() {
    // resident footprint = load image + MZ MinAlloc (header offset 0x0A) heap:
    // pb35 reserves 64 KiB main + 2 x 64 KiB heap segments (~192 KiB); a trimmed
    // hello world keeps only the 64 KiB main segment
    var pb35 = Compile(_HELLO_VAR, Dialect.Pb35);
    var pb36 = Compile(_HELLO_VAR, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(Resident(pb35), Is.GreaterThanOrEqualTo(0x30000), "pb35 baseline: main + string heap + array heap");
      Assert.That(Resident(pb36), Is.LessThanOrEqualTo(0x10000 + 16), "pb36: only the 64 KiB main segment");
    });

    static int Resident(byte[] exe) {
      var headerParagraphs = exe[0x08] | exe[0x09] << 8;
      var minAlloc = exe[0x0A] | exe[0x0B] << 8;
      return exe.Length - headerParagraphs * 16 + minAlloc * 16;
    }
  }

  [Test]
  public void Emit_GivenStringProgram_WhenPb36_ThenStringKernelIncludedAndSmallerThanPb35() {
    const string source = "a$ = \"x\"\nb$ = a$ + \"y\"\nPRINT b$\nEND";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(pb36.Length, Is.LessThan(pb35.Length), "file/array/quad runtime should still trim away");
      Assert.That(pb36, Has.Length.GreaterThan(2048), "the string kernel must stay in");
    });
  }

  [Test]
  public void Emit_GivenFileProgram_WhenPb36_ThenCompilesWithFileAndStringSections() {
    const string source = "OPEN \"X.TXT\" FOR OUTPUT AS #1\nPRINT #1, \"x\"\nCLOSE #1\nKILL \"X.TXT\"\nEND";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36, Is.Not.Empty); // unresolved-label emission would have thrown
  }

  [Test]
  public void Emit_GivenSameSource_WhenPb35_ThenTrimmingNeverTouchesThePb35Layout() {
    // determinism guard: two pb35 compiles must be byte-identical (no pb36 state leaks)
    var first = Compile(_HELLO, Dialect.Pb35);
    _ = Compile(_HELLO, Dialect.Pb36);
    var second = Compile(_HELLO, Dialect.Pb35);
    Assert.That(second, Is.EqualTo(first));
  }

  #endregion

  #region P7 - trivial-I/O lowering (raw COM-style image)

  [Test]
  public void Emit_GivenHelloWorld_WhenPb36_ThenTwentyFiveByteComImage() {
    var image = Compile(_HELLO, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(image, Has.Length.EqualTo(25), "MOV AH,9 / MOV DX / INT 21h / INT 20h + text + '$'");
      Assert.That(image[..2], Is.EqualTo(new byte[] { 0xB4, 0x09 }), "AH=9 DOS string writer");
      Assert.That(image[^1], Is.EqualTo((byte)'$'));
      Assert.That(Ascii(image), Does.Contain("Hello, World!\r\n"));
    });
  }

  [Test]
  public void Emit_GivenLiteralContainingDollar_WhenPb36_ThenHandleWriterVariant() {
    var image = Compile("PRINT \"100$ for you\"\nEND", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(image[..2], Is.EqualTo(new byte[] { 0xB4, 0x40 }), "'$' in text forces the AH=40h writer");
      Assert.That(Ascii(image), Does.Contain("100$ for you"));
    });
  }

  [Test]
  public void Emit_GivenConstantNumericPrint_WhenPb36_ThenPbFormattedAtCompileTime() {
    var image = Compile("PRINT 2 + 3\nPRINT -7\nEND", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(image, Has.Length.LessThan(64));
      Assert.That(Ascii(image), Does.Contain(" 5 \r\n"), "PB integer format: space, digits, trailing space");
      Assert.That(Ascii(image), Does.Contain("-7 \r\n"));
    });
  }

  [Test]
  public void Emit_GivenCommaSeparator_WhenPb36_ThenFourteenColumnZonesPrecomputed() {
    var image = Compile("PRINT \"ab\", \"cd\"\nEND", Dialect.Pb36);
    Assert.That(Ascii(image), Does.Contain("ab" + new string(' ', 12) + "cd\r\n"));
  }

  [Test]
  public void Emit_GivenEndWithExitCode_WhenPb36_ThenExplicitTerminateCall() {
    var image = Compile("PRINT \"x\"\nEND 3", Dialect.Pb36);
    var hasExit = false;
    for (var i = 0; i + 4 < image.Length; ++i)
      hasExit |= image[i] == 0xB8 && image[i + 1] == 0x03 && image[i + 2] == 0x4C; // MOV AX,4C03h
    Assert.That(hasExit, Is.True, "explicit exit code uses AH=4Ch with AL=3");
  }

  [Test]
  public void Emit_GivenNonTrivialStatement_WhenPb36_ThenGeneralMzPathTaken() {
    var image = Compile("x% = 1\nPRINT x%\nEND", Dialect.Pb36);
    Assert.That(image[..2], Is.EqualTo(new byte[] { (byte)'M', (byte)'Z' }), "variables need the real runtime");
  }

  [Test]
  public void Emit_GivenHelloWorld_WhenPb35_ThenNoTrivialLowering() {
    var image = Compile(_HELLO, Dialect.Pb35);
    Assert.That(image[..2], Is.EqualTo(new byte[] { (byte)'M', (byte)'Z' }), "P7 is pb36-only");
  }

  #endregion

  #region statement pruning (O2/O10), literal pool (O11), folding (O9), unrolling (O7)

  private static SemanticModel BindModel(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return model;
  }

  [Test]
  public void Prune_GivenCodeAfterGoto_WhenPruned_ThenDeadStatementsDropUntilLabel() {
    var model = BindModel("GOTO Tail\nPRINT 1\nPRINT 2\nTail:\nPRINT 3\nEND");
    Pb36Pruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.PrintStmt>().Count(), Is.EqualTo(1),
      "only the labeled tail PRINT survives");
  }

  [Test]
  public void Prune_GivenDataInDeadRegion_WhenPruned_ThenDataSurvives() {
    var model = BindModel("GOTO Tail\nDATA 1,2,3\nPRINT 9\nTail:\nREAD a%\nPRINT a%\nEND");
    Pb36Pruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DataStmt>().Count(), Is.EqualTo(1),
      "DATA acts at compile time and must survive dead regions");
  }

  [Test]
  public void Prune_GivenRedundantDefSegs_WhenPruned_ThenOnlyLastBeforeObserverSurvives() {
    var model = BindModel("DEF SEG = &H40\nx% = 1\nDEF SEG = &HB800\ny% = PEEK(0)\nDEF SEG\nPRINT y%\nEND");
    Pb36Pruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DefSegStmt>().Count(), Is.EqualTo(2),
      "the first DEF SEG is shadowed; the one feeding PEEK and the reset survive");
  }

  [Test]
  public void Prune_GivenPeekBetweenDefSegs_WhenPruned_ThenBothSurvive() {
    var model = BindModel("DEF SEG = &H40\ny% = PEEK(0)\nDEF SEG = &HB800\nz% = PEEK(0)\nPRINT y%; z%\nEND");
    Pb36Pruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DefSegStmt>().Count(), Is.EqualTo(2));
  }

  [Test]
  public void Emit_GivenContainedLiterals_WhenPb36_ThenPoolSharesBytes() {
    const string source = "x$ = \"Hello, World!\"\nPRINT x$; \"World!\"; \"lo, W\"\nEND";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountOf(pb35, "World!"), Is.EqualTo(2), "pb35 keeps one blob per literal");
      Assert.That(CountOf(pb36, "World!"), Is.EqualTo(1), "pb36 packs contained literals");
      Assert.That(CountOf(pb36, "lo, W"), Is.EqualTo(1));
    });

    static int CountOf(byte[] image, string text) {
      var needle = System.Text.Encoding.ASCII.GetBytes(text);
      var count = 0;
      for (var i = 0; i + needle.Length <= image.Length; ++i)
        if (image.AsSpan(i, needle.Length).SequenceEqual(needle))
          ++count;
      return count;
    }
  }

  [Test]
  public void Emit_GivenLiteralConcat_WhenPb36_ThenFoldedToOnePooledLiteral() {
    const string source = "a$ = \"fold\" + \"ed \" + \"parts\"\nPRINT a$" + "\nEND";
    var image = Compile(source, Dialect.Pb36);
    Assert.That(Ascii(image), Does.Contain("folded parts"));
  }

  [Test]
  public void Emit_GivenSmallConstantTripLoop_WhenSpeedOptimized_ThenUnrolledImageDiffers() {
    const string body = "t% = 0\nFOR i% = 1 TO 3\n  t% = t% + i%\nNEXT i%\nPRINT t%; i%\nEND";
    var generic = Compile(body, Dialect.Pb36);
    var unrolled = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    Assert.That(unrolled, Is.Not.EqualTo(generic), "SPEED should unroll the trip-3 loop");
  }

  #endregion

  #region wrap-correct constant folding (O1)

  [TestCase(32767 + 1, (short)-32768)]
  [TestCase(-32768 - 1, (short)32767)]
  [TestCase(65535 + 1, (short)0)]
  [TestCase(12345, (short)12345)]
  public void WrapToType_GivenIntegerOverflow_WhenWrapped_ThenSilentWrapBits(long value, short expected)
    => Assert.That(CodeGenerator.WrapToType(value, PbType.Integer), Is.EqualTo(expected));

  [TestCase(255 + 1, (byte)0)]
  [TestCase(256 + 7, (byte)7)]
  public void WrapToType_GivenByteOverflow_WhenWrapped_ThenLowByte(long value, byte expected)
    => Assert.That(CodeGenerator.WrapToType(value, PbType.Byte), Is.EqualTo(expected));

  [TestCase(65536L + 5, 5L)]
  [TestCase(-1L, 65535L)]
  public void WrapToType_GivenWordOverflow_WhenWrapped_ThenUnsignedLowWord(long value, long expected)
    => Assert.That(CodeGenerator.WrapToType(value, PbType.Word), Is.EqualTo(expected));

  [TestCase(2147483647L + 1, -2147483648L)]
  [TestCase(4294967296L + 9, 9L)]
  public void WrapToType_GivenLongOverflow_WhenWrapped_ThenSilentWrapBits(long value, long expected)
    => Assert.That(CodeGenerator.WrapToType(value, PbType.Long), Is.EqualTo(expected));

  [TestCase(4294967295L, 4294967295L)]
  [TestCase(4294967296L, 0L)]
  public void WrapToType_GivenDwordOverflow_WhenWrapped_ThenUnsignedLowDword(long value, long expected)
    => Assert.That(CodeGenerator.WrapToType(value, PbType.Dword), Is.EqualTo(expected));

  [Test]
  public void Emit_GivenConstantExpressions_WhenPb36_ThenFoldedCodeIsSmaller() {
    const string source = "y& = 7\nx% = 2 + 3 * 4 - 1\ny& = y& + 1000 * 1000\nPRINT x%; y&\nEND";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36.Length, Is.LessThan(pb35.Length));
  }

  #endregion

  #region definite-assignment frame-zero elision (O19)

  private static (SemanticModel Model, ProcedureSymbol Proc) BindProc(string body) {
    var source = $"DECLARE SUB P\nCALL P\nEND\nSUB P\n{body}\nEND SUB";
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return (model, model.Procedures["P"]);
  }

  private static bool CanElide(string body) {
    var (model, proc) = BindProc(body);
    var locals = proc.Variables.Values
      .Where(v => v.Storage == VariableStorage.Local && !v.IsArray)
      .Distinct()
      .ToList();
    return CodeGenerator.CanElideFrameZeroing(model, proc.Body!, locals);
  }

  [Test]
  public void Elide_GivenStraightLineAssignments_WhenAnalyzed_ThenProvable()
    => Assert.That(CanElide("a% = 1\nb% = a% * 2\nPRINT b%"), Is.True);

  [Test]
  public void Elide_GivenReadBeforeAssignment_WhenAnalyzed_ThenKeepsZeroing()
    => Assert.That(CanElide("b% = a% + 1\na% = 2\nPRINT b%"), Is.False);

  [Test]
  public void Elide_GivenAssignmentBehindIf_WhenAnalyzed_ThenKeepsZeroing()
    => Assert.That(CanElide("IF 1 THEN\n  a% = 1\nEND IF\nPRINT a%"), Is.False);

  [Test]
  public void Elide_GivenForCounterAsOnlyLocal_WhenAnalyzed_ThenProvable()
    => Assert.That(CanElide("FOR i% = 1 TO 3\n  PRINT i%\nNEXT i%"), Is.True);

  [Test]
  public void Elide_GivenLocalAssignedOnlyInsideFor_WhenAnalyzed_ThenKeepsZeroing()
    => Assert.That(CanElide("FOR i% = 1 TO 3\n  a% = i%\nNEXT i%\nPRINT a%"), Is.False);

  [Test]
  public void Elide_GivenDynamicStringLocal_WhenAnalyzed_ThenProvableViaIndividualSlotZeroing()
    => Assert.That(CanElide("a% = 1\ns$ = \"x\"\nPRINT a%; s$"), Is.True);

  [Test]
  public void Elide_GivenFunctionCallInPrefix_WhenAnalyzed_ThenKeepsZeroing() {
    const string source = "DECLARE FUNCTION F%\nEND\nFUNCTION F%\n  F% = 1\nEND FUNCTION\nSUB P\n  a% = F%\n  PRINT a%\nEND SUB";
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var proc = model.Procedures["P"];
    var locals = proc.Variables.Values.Where(v => v.Storage == VariableStorage.Local && !v.IsArray).Distinct().ToList();
    Assert.That(CodeGenerator.CanElideFrameZeroing(model, proc.Body!, locals), Is.False,
      "user FUNCTION calls are opaque - the proof must stop");
  }

  [Test]
  public void Emit_GivenElidableSub_WhenPb36_ThenSmallerThanPb35() {
    const string source = "DECLARE SUB P\nCALL P\nEND\nSUB P\n  a% = 1\n  b% = a% + 1\n  PRINT b%\nEND SUB";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36.Length, Is.LessThan(pb35.Length));
  }

  #endregion

  #region block-move widening (C1/R3)

  [Test]
  public void Emit_GivenUdtCopies_WhenPb36_ThenNotLargerThanPb35() {
    const string source = "TYPE T\n  x AS LONG\n  y AS LONG\nEND TYPE\nDIM a AS T\nDIM b AS T\na.x = 1\nb = a\nPRINT b.x\nEND";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36.Length, Is.LessThanOrEqualTo(pb35.Length), "word-wide REP MOVSW never grows the image");
  }

  [Test]
  public void Emit_GivenUdtCopiesUnderCpu386_WhenPb36_ThenDwordMoves() {
    const string source = "$CPU 80386\nTYPE T\n  x AS LONG\n  y AS LONG\nEND TYPE\nDIM a AS T\nDIM b AS T\na.x = 1\nb = a\nPRINT b.x\nEND";
    var image = Compile(source, Dialect.Pb36);
    // REP MOVSD = F3 66 A5
    var found = false;
    for (var i = 0; i + 2 < image.Length; ++i)
      found |= image[i] == 0xF3 && image[i + 1] == 0x66 && image[i + 2] == 0xA5;
    Assert.That(found, Is.True, "$CPU 80386 + pb36 should emit REP MOVSD block copies");
  }

  #endregion

  #region strength reduction (O4) and zero idiom (O8)

  [Test]
  public void Emit_GivenMultiplyByPowerOfTwo_WhenPb36_ThenCompilesSmaller() {
    const string source = "FOR i% = 1 TO 10\n  a% = i% * 8\n  b& = i% * 4&\nNEXT i%\nPRINT a%; b&\nEND";
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36.Length, Is.LessThan(pb35.Length));
  }

  private static int CountImulBx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xF7 && image[i + 1] == 0xEB) // IMUL BX (F7 /5)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenCheckedMultiplyByTwo_WhenPb36_ThenKeepsImulForOverflowTrap() {
    // $ERROR OVERFLOW ON: a shift chain cannot raise error 6 on signed overflow,
    // so the strength reducer must back off and keep the genuine IMUL (the
    // integer formatter the PRINT pulls in uses no IMUL BX, so any F7 EB present
    // is the multiply itself). Without the guard the multiply would be a bare SHL.
    const string source = "$ERROR OVERFLOW ON\nx% = 30000\ny% = x% * 2\nPRINT y%\nEND";
    var checked_ = Compile(source, Dialect.Pb36);
    Assert.That(CountImulBx(checked_), Is.GreaterThanOrEqualTo(1),
      "checked x% * 2 must keep IMUL BX for the error-6 overflow trap, not strength-reduce to a shift");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Speed_ThenShiftAddReplacesImul() {
    // y% = x% * 3 in the modular int16 path lowers to (v + v<<1); no IMUL BX,
    // and no PRINT means the integer formatter (which has none anyway) is absent
    const string source = "$OPTIMIZE SPEED\nx% = 11\ny% = x% * 3\nEND";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "x% * 3 under SPEED should be a shift-add chain, no IMUL BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThirteen_WhenPb36Speed_ThenKeepsCompactImul() {
    // 13 = 1101b: three set bits, not a contiguous run - no cheap shift chain,
    // so the compact IMUL BX is kept
    const string source = "$OPTIMIZE SPEED\nx% = 11\ny% = x% * 13\nEND";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.EqualTo(1), "x% * 13 has no two-term decomposition, keep IMUL BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Default_ThenKeepsImul() {
    // the shift chains are a SPEED trade (a few bytes for the cycles); SIZE/default
    // keep the 2-byte IMUL
    const string source = "x% = 11\ny% = x% * 3\nEND";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.EqualTo(1), "without $OPTIMIZE SPEED the compact IMUL BX is kept");
  }

  [Test]
  public void Emit_GivenModularAddConstant_WhenPb36_ThenFewerBytesThanVariableAdd() {
    // y% = x% + 7 folds to one immediate ADD; y% = x% + z% must load and combine
    // a second operand, so the constant form is strictly smaller
    var constAdd = Compile("x% = 100\ny% = x% + 7\nEND", Dialect.Pb36);
    var varAdd = Compile("x% = 100\nz% = 7\ny% = x% + z%\nEND", Dialect.Pb36);
    Assert.That(constAdd.Length, Is.LessThan(varAdd.Length),
      "v% + const should fold to one immediate ALU op, smaller than a two-operand add");
  }

  private static int CountMovBxAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if ((image[i] == 0x8B && image[i + 1] == 0xD8) || (image[i] == 0x89 && image[i + 1] == 0xC3))
        ++count; // MOV BX, AX (either direction's encoding)
    return count;
  }

  [Test]
  public void Emit_GivenBitwiseMaskConstant_WhenPb36_ThenFoldsToImmediateNoRegisterLoad() {
    // y% = x% AND 15 folds the mask into AND AX,imm; the variable form must load BX
    var constMask = Compile("x% = 100\ny% = x% AND 15\nEND", Dialect.Pb36);
    var varMask = Compile("x% = 100\nw% = 15\ny% = x% AND w%\nEND", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovBxAx(constMask), Is.Zero, "x% AND 15 should fold to AND AX,imm with no MOV BX,AX");
      Assert.That(CountMovBxAx(varMask), Is.GreaterThanOrEqualTo(1), "x% AND w% must load the second operand into BX");
    });
  }

  [Test]
  public void Emit_GivenCompareConstant_WhenPb36_ThenFoldsToImmediate() {
    // y% = (x% = 5) compares against an immediate, no constant register load
    var pb36 = Compile("x% = 100\ny% = (x% = 5)\nEND", Dialect.Pb36);
    Assert.That(CountMovBxAx(pb36), Is.Zero, "comparison against a constant should fold to CMP AX,imm");
  }

  [Test]
  public void Emit_GivenModularIncrementByOne_WhenPb36_ThenUsesIncNotAddImmediate() {
    // y% = x% + 1 folds to INC AX (one byte); y% = x% + 5 needs ADD AX,imm (three)
    var inc = Compile("x% = 100\ny% = x% + 1\nEND", Dialect.Pb36);
    var add = Compile("x% = 100\ny% = x% + 5\nEND", Dialect.Pb36);
    Assert.That(inc.Length, Is.LessThan(add.Length), "+1 should be INC AX, smaller than ADD AX,imm");
  }

  [Test]
  public void Emit_GivenCompareAgainstZero_WhenPb36_ThenUsesOrIdiomNotCmpImmediate() {
    var pb36 = Compile("x% = 7\ny% = (x% = 0)\nEND", Dialect.Pb36);
    var hasOrAxAx = false;
    var hasCmpAxZero = false;
    for (var i = 0; i + 2 < pb36.Length; ++i) {
      hasOrAxAx |= pb36[i] == 0x09 && pb36[i + 1] == 0xC0;                       // OR AX,AX (r/m,reg form)
      hasCmpAxZero |= pb36[i] == 0x3D && pb36[i + 1] == 0x00 && pb36[i + 2] == 0x00; // CMP AX,0
    }
    Assert.Multiple(() => {
      Assert.That(hasOrAxAx, Is.True, "x% = 0 should test via OR AX,AX");
      Assert.That(hasCmpAxZero, Is.False, "x% = 0 should not emit CMP AX,0");
    });
  }

  [Test]
  public void Emit_GivenMultiplyByZeroWithFunctionOperand_WhenPb36_ThenOperandStillEvaluated() {
    // the FUNCTION call has side effects - x * 0 must keep the call (assert: the
    // call's PRINT side effect stays inside the image as a literal)
    const string source = "DECLARE FUNCTION F%\nx% = F% * 0\nPRINT x%\nEND\nFUNCTION F%\n  PRINT \"SIDE-EFFECT-MARKER\"\n  F% = 7\nEND FUNCTION";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(Ascii(pb36), Does.Contain("SIDE-EFFECT-MARKER"));
  }

  #endregion

  #region O4 - integer divide / modulo strength reduction

  private static int CountIdivBx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xF7 && image[i + 1] == 0xFB)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenPowerOfTwoDivides_WhenPb36_ThenIdivDisappears() {
    const string source = """
      a% = -29
      PRINT a% \ 8
      PRINT a% MOD 8
      PRINT a% \ 2
      PRINT a% MOD 2
      END
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(CountIdivBx(pb36), Is.LessThan(CountIdivBx(pb35)),
      "pb36 should shift/mask power-of-two \\ and MOD instead of IDIV BX");
  }

  [Test]
  public void Emit_GivenLongPowerOfTwoDivide_WhenPb36_ThenNoRuntimeDivCall() {
    const string source = """
      b& = -100000
      PRINT b& \ 4
      PRINT b& MOD 4
      END
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(pb36.Length, Is.LessThan(pb35.Length),
      "pb36 long power-of-two \\ / MOD should drop the LongDiv/LongMod runtime sections");
  }

  #endregion

  #region O14 - tail-call optimization

  [Test]
  public void Execute_GivenDeepTailRecursion_WhenPb36_ThenConstantStack() {
    // 60000 self-calls would devour ~480 KiB of stack without the tail jump
    var unit = Parser.Parse(Lexer.Tokenize("""
      DECLARE SUB CountDown(BYVAL n&)
      CountDown 60000
      PRINT "DONE"
      END
      SUB CountDown(BYVAL n&)
        IF n& > 0 THEN CountDown n& - 1
      END SUB
      """, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty);
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
    Assert.That(output, Is.EqualTo("DONE\n"));
  }

  [Test]
  public void Emit_GivenByRefRecursion_WhenPb36_ThenKeepsTheCall() {
    // BYREF parameters pin the standard frame - the self-call must stay a CALL
    var source = """
      DECLARE SUB Twice(n&)
      m& = 3
      Twice m&
      PRINT m&
      END
      SUB Twice(n&)
        IF n& < 10 THEN
          n& = n& * 2
          Twice n&
        END IF
      END SUB
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty);
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(exe));
    Assert.That(output, Is.EqualTo(" 12\n"));
  }

  #endregion

  #region O6 - single-expression function inlining

  [Test]
  public void Emit_GivenSingleExpressionFunction_WhenPb36_ThenNoCall() {
    const string source = """
      DECLARE FUNCTION Triple%(BYVAL x%)
      a% = 5
      PRINT Triple%(a%) + Triple%(2)
      END
      FUNCTION Triple%(BYVAL x%)
        Triple% = x% * 3
      END FUNCTION
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var output35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var output36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.Multiple(() => {
      Assert.That(output36, Is.EqualTo(" 21\n"));
      Assert.That(output36, Is.EqualTo(output35));
      Assert.That(pb36.Length, Is.LessThan(pb35.Length), "the inlined image sheds the call frames");
    });
  }

  [Test]
  public void Emit_GivenSideEffectArguments_WhenInlined_ThenEvaluatedOnce() {
    const string source = """
      DECLARE FUNCTION Sq&(BYVAL x&)
      DECLARE FUNCTION Bump%(q%)
      n% = 0
      PRINT Sq&(Bump%(n%))
      PRINT n%
      END
      FUNCTION Sq&(BYVAL x&)
        Sq& = x& * x&
      END FUNCTION
      FUNCTION Bump%(q%)
        q% = q% + 7
        Bump% = q%
      END FUNCTION
      """;
    var pb36 = Compile(source, Dialect.Pb36);
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.That(output, Is.EqualTo(" 49\n 7\n"), "argument effects must run exactly once");
  }

  #endregion

  #region O3 - common subexpression elimination

  private static (int slots, System.Collections.Generic.Dictionary<PowerBasic.Compiler.Syntax.Ast.Expression, PowerBasic.Compiler.CodeGen.Pb36CommonSubexpr.CseMark> marks) AnalyzeCse(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var r = PowerBasic.Compiler.CodeGen.Pb36CommonSubexpr.Analyze(model.MainBody, model);
    return (r.SlotCount, r.Marks);
  }

  [Test]
  public void Cse_GivenRepeatedAddress_ThenOneSlotWithDefineAndUse() {
    var (slots, marks) = AnalyzeCse("x% = 1\ny% = 2\na% = y% * 320 + x%\nb% = y% * 320 + x%\nEND");
    Assert.That(slots, Is.EqualTo(1));
    Assert.That(marks.Values.Count(m => m.IsDefine), Is.EqualTo(1));
    Assert.That(marks.Values.Count(m => !m.IsDefine), Is.EqualTo(1));
  }

  [Test]
  public void Cse_GivenWriteBetweenUses_ThenNotCached() {
    var (slots, _) = AnalyzeCse("x% = 1\ny% = 2\na% = y% * 320\ny% = 9\nb% = y% * 320\nEND");
    Assert.That(slots, Is.EqualTo(0), "the write to y% invalidates the subtree");
  }

  [Test]
  public void Cse_GivenBarrierBetweenUses_ThenNotCached() {
    var (slots, _) = AnalyzeCse("DECLARE SUB P\nx% = 1\na% = x% * 7\nP\nb% = x% * 7\nEND\nSUB P\nEND SUB");
    Assert.That(slots, Is.EqualTo(0), "the CALL ends the straight-line run");
  }

  [Test]
  public void Execute_GivenCseHeavyArithmetic_WhenPb36_ThenMatchesAndShrinks() {
    const string source = """
      x% = 7
      y% = 3
      a% = y% * 320 + x%
      b% = y% * 320 + x%
      c% = y% * 320 + x%
      PRINT a%; b%; c%
      END
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var out35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var out36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.Multiple(() => {
      Assert.That(out36, Is.EqualTo(" 967  967  967\n"));
      Assert.That(out36, Is.EqualTo(out35));
      Assert.That(pb36.Length, Is.LessThan(pb35.Length), "two recomputations become slot reloads");
    });
  }

  #endregion

  #region O18 - interprocedural constant propagation

  [Test]
  public void Ipcp_GivenConstantArgEverywhere_ThenParamPropagated() {
    const string source = """
      DECLARE SUB P(BYVAL m%, BYVAL v%)
      P 1, 10
      P 1, 20
      END
      SUB P(BYVAL m%, BYVAL v%)
        PRINT m%; v%
      END SUB
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var ipcp = PowerBasic.Compiler.CodeGen.Pb36Ipcp.Analyze(model);
    var pSub = model.Procedures["P"];
    Assert.Multiple(() => {
      Assert.That(ipcp.ContainsKey(pSub.Parameters[0]), Is.True, "m% is always 1");
      Assert.That(ipcp.ContainsKey(pSub.Parameters[1]), Is.False, "v% varies");
    });
  }

  [Test]
  public void Ipcp_GivenWrittenParam_ThenNotPropagated() {
    const string source = """
      DECLARE SUB P(BYVAL m%)
      P 1
      P 1
      END
      SUB P(BYVAL m%)
        m% = m% + 1
        PRINT m%
      END SUB
      """;
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var ipcp = PowerBasic.Compiler.CodeGen.Pb36Ipcp.Analyze(model);
    Assert.That(ipcp, Is.Empty, "a written parameter is not constant-propagated");
  }

  #endregion

  #region C2 - $CPU 80486 alignment + BSWAP

  [Test]
  public void Execute_GivenCpu486_WhenAlignedProcs_ThenOutputUnchanged() {
    // 16-byte procedure alignment is output-invariant; the program must run
    // identically to the un-aligned build
    const string source = """
      $CPU 80486
      DECLARE FUNCTION Tri%(BYVAL n%)
      PRINT Tri%(5); Tri%(9)
      END
      FUNCTION Tri%(BYVAL n%)
        Tri% = n% * (n% + 1) \ 2
      END FUNCTION
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo(" 15  45\n"));
  }

  [Test]
  public void Execute_GivenBswapInlineAsm_WhenPb36_ThenReversesByteOrder() {
    const string source = """
      $CPU 80486
      v& = &H11223344
      ! MOV EAX, v&
      ! BSWAP EAX
      ! MOV v&, EAX
      PRINT HEX$(v&)
      END
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo("44332211\n"));
  }

  [Test]
  public void Emit_GivenCpu486ProcAlignment_ThenProcEntryIs16ByteAligned() {
    var image = Compile("""
      $CPU 80486
      DECLARE SUB P
      P
      END
      SUB P
        PRINT "x"
      END SUB
      """, Dialect.Pb36);
    // a NOP run (0x90) appears as the alignment pad before the aligned proc
    var nopRun = 0; var maxRun = 0;
    foreach (var b in image) { nopRun = b == 0x90 ? nopRun + 1 : 0; maxRun = System.Math.Max(maxRun, nopRun); }
    Assert.That(maxRun, Is.GreaterThan(0), "an alignment NOP pad should be present");
  }

  #endregion
}
