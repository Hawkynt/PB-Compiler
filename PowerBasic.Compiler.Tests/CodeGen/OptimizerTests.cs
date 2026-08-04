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
public sealed class OptimizerTests {

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
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.PrintStmt>().Count(), Is.EqualTo(1),
      "only the labeled tail PRINT survives");
  }

  [Test]
  public void Emit_GivenOptimizeSize_WhenCompiled_ThenSmallerImageSameBehavior() {
    // $OPTIMIZE SIZE: no inlining plus S3 procedure tail-merging must shrink a branchy program
    // with duplicate procedures; the differential batteries prove behavior elsewhere. (S1
    // short-jump relaxation is no longer a SIZE lever - it runs on every optimized image, since
    // the short form is smaller AND the near form it replaces is an 80386 encoding.)
    static byte[] Compile(string source) {
      var model = BindModel(source);
      var generator = new CodeGenerator(model);
      var exe = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      return exe;
    }
    const string body = """
      DECLARE FUNCTION Mix%(BYVAL a%, BYVAL b%)
      DIM i AS INTEGER, total AS LONG
      FOR i = 1 TO 50
        IF i MOD 3 = 0 THEN
          total = total + Mix%(i, 3)
        ELSEIF i MOD 5 = 0 THEN
          total = total - Mix%(i, 5)
        ELSE
          total = total + Mix%(i, 7)
        END IF
      NEXT
      PRINT total
      END
      FUNCTION Mix%(BYVAL a%, BYVAL b%)
        Mix% = a% * b% + a% - b%
      END FUNCTION
      """;
    var sized = Compile("$OPTIMIZE SIZE\n" + body);
    var plain = Compile(body);
    Assert.That(sized.Length, Is.LessThan(plain.Length), $"SIZE image ({sized.Length}) must undercut the default ({plain.Length})");
  }

  [Test]
  public void Execute_GivenOptimizeSizeAndCallInLoop_WhenRun_ThenBodySurvivesAndMatchesDefault() {
    // O6's purge drops a procedure it expects to inline at EVERY call site - but $OPTIMIZE SIZE
    // never inlines, so the body must stay. Purging it left each call referencing a label
    // nothing binds, and the compiler threw at fixup resolution.
    const string body = """
      DECLARE FUNCTION F%(BYVAL a%)
      DIM i AS INTEGER, t AS LONG
      FOR i = 1 TO 5
        t = t + F%(i)
      NEXT
      PRINT t
      END
      FUNCTION F%(BYVAL a%)
        F% = a% + 1
      END FUNCTION
      """;
    var sized = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile("$OPTIMIZE SIZE\n" + body, Dialect.Pb36)));
    Assert.That(sized, Is.EqualTo(DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(body, Dialect.Pb36)))),
      "$OPTIMIZE SIZE must compile and behave exactly like the default build");
  }

  [Test]
  public void Execute_GivenNonLeftLeaningConcatShapes_WhenOptimized_ThenSingleAllocPathBuildsCorrectly() {
    // O9 closure: right-nested and mixed concat trees flatten into the O24 single-allocation
    // builder (FlattenStringConcat collects BOTH sides recursively); barrier operands
    // (function results sharing a buffer) stay on the pairwise consume-immediately path by
    // design - staging them would alias. This pins the non-left-leaning shapes' behavior.
    const string source = """
      $OPTIMIZE SPEED
      a$ = "aa"
      b$ = "bb"
      c$ = "cc"
      d$ = a$ + (b$ + c$)
      e$ = (a$ + b$) + (c$ + a$)
      f$ = a$ + (b$ + (c$ + "zz"))
      PRINT d$; "|"; e$; "|"; f$
      """;
    var model = BindModel(source);
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo("aabbcc|aabbccaa|aabbcczz\n"));
  }

  [Test]
  public void Emit_GivenLatticeProvedComparison_WhenOptimized_ThenDeadArmVanishes() {
    // O16 completed: the interval lattice (not just FOR-counter ranges) feeds comparison
    // folding - x% is provably 200 at the IF, so x% < 300 is decidable and the dead ELSE
    // arm (with its marker literal) never reaches the image
    static bool HasMarker(string source, bool optimize) {
      var model = BindModel(source);
      var generator = new CodeGenerator(model) { Optimize = optimize };
      var exe = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
      var marker = System.Text.Encoding.ASCII.GetBytes("XDEADX");
      for (var i = 0; i + marker.Length <= exe.Length; ++i)
        if (exe.AsSpan(i, marker.Length).SequenceEqual(marker))
          return true;
      return false;
    }
    const string source = """
      DIM x AS INTEGER
      x = 200
      IF x < 300 THEN
        PRINT "live"
      ELSE
        PRINT "XDEADX"
      END IF
      """;
    Assert.Multiple(() => {
      Assert.That(HasMarker(source, optimize: false), Is.True, "unoptimized keeps both arms");
      Assert.That(HasMarker(source, optimize: true), Is.False, "the lattice proves x in [200,200], so the ELSE arm is dead");
    });
  }

  [Test]
  public void Prune_GivenConsecutiveLocates_WhenPruned_ThenOnlyTheLastSurvives() {
    var model = BindModel("LOCATE 1, 1\nLOCATE 2, 3\nPRINT \"x\"\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(1));
  }

  [Test]
  public void Prune_GivenOutputOrCursorReadBetweenLocates_WhenPruned_ThenBothSurvive() {
    var model = BindModel("LOCATE 1, 1\nPRINT \"x\"\nLOCATE 2, 3\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(2), "PRINT observes the cursor");
    var model2 = BindModel("LOCATE 1, 1\nr% = CSRLIN\nLOCATE 2, 3\nPRINT r%\nEND");
    OptPruner.Prune(model2);
    Assert.That(model2.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(2), "CSRLIN reads the cursor");
  }

  [Test]
  public void Prune_GivenPartialLocateMasks_WhenPruned_ThenOnlyCoveredFolds() {
    // LOCATE ,5 sets only the column; LOCATE 1,1 covers it -> earlier folds
    var model = BindModel("LOCATE , 5\nLOCATE 1, 1\nPRINT \"x\"\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(1));
    // LOCATE 1,1 then LOCATE ,5: the later sets only the column - the earlier row survives
    var model2 = BindModel("LOCATE 1, 1\nLOCATE , 5\nPRINT \"x\"\nEND");
    OptPruner.Prune(model2);
    Assert.That(model2.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(2));
  }

  [Test]
  public void Prune_GivenClsChains_WhenPruned_ThenRedundantWorkFolds() {
    // CLS re-clears and homes: an earlier CLS and an earlier (unobserved) LOCATE are dead
    var model = BindModel("CLS\nCLS\nPRINT \"x\"\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "CLS"), Is.EqualTo(1));
    var model2 = BindModel("LOCATE 5, 5\nCLS\nPRINT \"x\"\nEND");
    OptPruner.Prune(model2);
    Assert.That(model2.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(0), "CLS homes the cursor - the unobserved LOCATE is dead");
  }

  [Test]
  public void Prune_GivenGotoChain_WhenPruned_ThenGotoThreadsToFinalLabel() {
    // GOTO Hop lands on a label whose next executable statement is another GOTO - the
    // decompiled (and generated) control flow should jump straight to the final label
    var model = BindModel("i% = 1\nIF i% = 1 THEN GOTO Hop\nPRINT 0\nHop:\nGOTO Final\nPRINT 1\nFinal:\nPRINT 2\nEND");
    OptPruner.Prune(model);
    var gotos = AllGotos(model.MainBody);
    Assert.That(gotos.Any(g => g.Target.Equals("Final", StringComparison.OrdinalIgnoreCase) )
      && gotos.Count(g => g.Target.Equals("Hop", StringComparison.OrdinalIgnoreCase)) == 0,
      "the conditional GOTO threads through Hop straight to Final");
  }

  [Test]
  public void Prune_GivenGotoCycle_WhenPruned_ThenTerminatesUnchanged() {
    var model = BindModel("GOTO A\nA:\nGOTO B\nB:\nGOTO A\n");
    Assert.DoesNotThrow(() => OptPruner.Prune(model), "a GOTO cycle must not hang the threader");
  }

  private static List<PowerBasic.Compiler.Syntax.Ast.GotoStmt> AllGotos(IEnumerable<PowerBasic.Compiler.Syntax.Ast.Statement> body) {
    var result = new List<PowerBasic.Compiler.Syntax.Ast.GotoStmt>();
    foreach (var s in body)
      switch (s) {
        case PowerBasic.Compiler.Syntax.Ast.GotoStmt g: result.Add(g); break;
        case PowerBasic.Compiler.Syntax.Ast.IfStmt i:
          result.AddRange(AllGotos(i.Then));
          foreach (var (_, b) in i.ElseIfs) result.AddRange(AllGotos(b));
          if (i.Else != null) result.AddRange(AllGotos(i.Else));
          break;
      }
    return result;
  }

  [Test]
  public void Prune_GivenDataInDeadRegion_WhenPruned_ThenDataSurvives() {
    var model = BindModel("GOTO Tail\nDATA 1,2,3\nPRINT 9\nTail:\nREAD a%\nPRINT a%\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DataStmt>().Count(), Is.EqualTo(1),
      "DATA acts at compile time and must survive dead regions");
  }

  [Test]
  public void Prune_GivenRedundantDefSegs_WhenPruned_ThenOnlyLastBeforeObserverSurvives() {
    var model = BindModel("DEF SEG = &H40\nx% = 1\nDEF SEG = &HB800\ny% = PEEK(0)\nDEF SEG\nPRINT y%\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.DefSegStmt>().Count(), Is.EqualTo(2),
      "the first DEF SEG is shadowed; the one feeding PEEK and the reset survive");
  }

  [Test]
  public void Prune_GivenPeekBetweenDefSegs_WhenPruned_ThenBothSurvive() {
    var model = BindModel("DEF SEG = &H40\ny% = PEEK(0)\nDEF SEG = &HB800\nz% = PEEK(0)\nPRINT y%; z%\nEND");
    OptPruner.Prune(model);
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

  [Test]
  public void Emit_GivenLongShiftUnderCpu386_WhenPb36_ThenSingleDwordShift() {
    // a constant-count LONG SHIFT collapses the per-bit loop to one 66 C1 dword shift
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nx& = 3\nSHIFT LEFT x&, 4\nPRINT x&\nEND";
    const string no386 = "$OPTIMIZE SPEED\nx& = 3\nSHIFT LEFT x&, 4\nPRINT x&\nEND";
    Assert.That(CountDwordShiftImm(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountDwordShiftImm(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should add a 32-bit dword shift the per-bit-loop version lacks");
  }

  // 66 C1 = operand-size-prefixed shift-group-by-imm8 = a 32-bit SHL/SHR/ROL/ROR imm
  private static int CountDwordShiftImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0xC1)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongDivideByConstantUnderCpu386_WhenPb36_ThenHardwareIdiv() {
    // a constant divisor of magnitude >= 2 drops the LongDiv runtime call for a 66 F7 IDIV;
    // the dividend is a SUB parameter with differing call args so it cannot be folded away
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL n AS LONG)\nd 100000007\nd 9\nEND\nSUB d(BYVAL n AS LONG)\nPRINT n \\ 7\nEND SUB";
    const string no386 = "$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL n AS LONG)\nd 100000007\nd 9\nEND\nSUB d(BYVAL n AS LONG)\nPRINT n \\ 7\nEND SUB";
    Assert.That(CountDwordF7(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountDwordF7(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should add a 32-bit IDIV the runtime-call version lacks");
  }

  // 66 F7 = operand-size-prefixed group-3 (IDIV/DIV/MUL/NEG) = a 32-bit divide here
  private static int CountDwordF7(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0xF7)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongDivideRangeKnown_WhenPb36_ThenNarrowedTo16BitIdiv() {
    // a signed LONG \ by a small constant whose dividend the interval lattice proves
    // fits int16 (a FOR counter 0..100) narrows to one 16-bit IDIV BX; an INPUT-sourced
    // dividend (range unknown) stays on the LongDiv runtime call (no IDIV BX). No $CPU.
    const string narrowed = "$OPTIMIZE SPEED\nFOR i& = 0 TO 100\nx& = i& \\ 3\nNEXT i&\nPRINT x&\nEND";
    const string runtime = "$OPTIMIZE SPEED\nINPUT j&\nx& = j& \\ 3\nPRINT x&\nEND";
    Assert.That(CountIdivBx(Compile(narrowed, Dialect.Pb36)),
      Is.GreaterThan(CountIdivBx(Compile(runtime, Dialect.Pb36))),
      "a range-known LONG divide should narrow to a 16-bit IDIV BX the runtime-call version lacks");
  }

  // 39 D8 = CMP AX, BX - the whole comparison once it has been narrowed to 16 bits
  private static int CountCmpAxBx(byte[] image) => CountPair(image, 0x39, 0xD8);

  // 19 CA = SBB DX, CX - the second instruction of every 32-bit compare sequence
  private static int CountSbbDxCx(byte[] image) => CountPair(image, 0x19, 0xCA);

  // F7 E3 = MUL BX (group 3 /4) - the narrowed unsigned 16x16 -> 32 multiply
  private static int CountMulBx(byte[] image) => CountPair(image, 0xF7, 0xE3);

  [Test]
  public void Emit_GivenMultiplyByOne_WhenPb36PlainOptimize_ThenNoImul() {
    // O0076/O0077: x * 1 / x * -1 / x * 0 fold to nothing / neg / xor under PLAIN --optimize (they
    // are strictly smaller than IMUL, not a SPEED trade), so the image is smaller than x * 3 which
    // keeps the multiply. $OPTIMIZE SPEED is NOT set here - that is the point of the test.
    var one = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 1\nPRINT y\nEND", Dialect.Pb36);
    var three = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 3\nPRINT y\nEND", Dialect.Pb36);
    Assert.That(one.Length, Is.LessThan(three.Length), "x * 1 folds away without $OPTIMIZE SPEED; x * 3 keeps IMUL");
  }

  [Test]
  public void Emit_GivenSelfXor_WhenPb36_ThenFoldedToZeroSmallerImage() {
    // O0076: x XOR x is 0 (and x AND x / x OR x is x), folded without a second operand or the XOR,
    // so the optimized image is smaller than xor-ing two distinct variables.
    var self = Compile("DIM x AS INTEGER, r AS INTEGER\nLINE INPUT x$\nx = VAL(x$)\nr = x XOR x\nPRINT r\nEND", Dialect.Pb36);
    var distinct = Compile("DIM x AS INTEGER, y AS INTEGER, r AS INTEGER\nLINE INPUT x$\nx = VAL(x$)\ny = 3\nr = x XOR y\nPRINT r\nEND", Dialect.Pb36);
    Assert.That(self.Length, Is.LessThan(distinct.Length), "x XOR x folds to 0; x XOR y keeps the XOR");
  }

  [Test]
  public void Emit_GivenRepeatedLenOfSameString_WhenPb36_ThenCachedSmallerImage() {
    // O0180: LEN(s$) + LEN(s$) + LEN(s$) reads the descriptor once and reloads a slot for the rest,
    // so the optimized image is smaller than reading a fresh LEN each time (which the unoptimized
    // build does). A single LEN can't cache, so its optimized/unoptimized sizes are the baseline.
    const string three = "DIM s AS STRING, n AS LONG\nLINE INPUT s\nn = LEN(s) + LEN(s) + LEN(s)\nPRINT n\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(three, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var opt = new CodeGenerator(model).EmitExecutable();
    var noOpt = new CodeGenerator(model) { Optimize = false }.EmitExecutable();
    Assert.That(opt.Length, Is.LessThan(noOpt.Length), "repeated LEN(s$) caches to one descriptor read");
  }

  [Test]
  public void Emit_GivenZeroLengthLeftDollar_WhenPb36_ThenFoldedToEmptyNoCall() {
    // O0266: LEFT$(a$, 0) is provably the empty string, so no rt_strleft call (folded to xor ax,ax).
    // The same shape with a non-zero length keeps the call, so its image is larger.
    var zero = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = LEFT$(a, 0)\nPRINT t\nEND", Dialect.Pb36);
    var nonZero = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = LEFT$(a, 2)\nPRINT t\nEND", Dialect.Pb36);
    Assert.That(zero.Length, Is.LessThan(nonZero.Length),
      "LEFT$(a$, 0) folds to the empty string (no StrLeft); LEFT$(a$, 2) keeps the call");
  }

  [Test]
  public void Emit_GivenConcatWithEmptyLiteral_WhenPb36_ThenNoStrCatCall() {
    // O0178: t = a$ + "" is just t = a$ (reading a$ already yields an owned copy), so no StrCat.
    // The same shape with a non-empty literal keeps the concat, so its image is larger.
    var empty = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = a + \"\"\nPRINT t\nEND", Dialect.Pb36);
    var nonEmpty = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = a + \"x\"\nPRINT t\nEND", Dialect.Pb36);
    Assert.That(empty.Length, Is.LessThan(nonEmpty.Length),
      "a$ + \"\" drops to a plain copy (no StrCat); a$ + \"x\" keeps the concat");
  }

  [Test]
  public void Emit_GivenEmptyStringComparison_WhenPb36_ThenHandleTestNotStrCmp() {
    // O0181: s = "" is emptiness - a handle test - so rt_strcmp is not called (and, being the only
    // string comparison, is trimmed from the image). The same shape against a non-empty literal
    // keeps the StrCmp call, so its image is larger; comparing the two isolates the win.
    var empty = Compile("DIM s AS STRING\nLINE INPUT s\nIF s = \"\" THEN PRINT 1\nEND", Dialect.Pb36);
    var nonEmpty = Compile("DIM s AS STRING\nLINE INPUT s\nIF s = \"x\" THEN PRINT 1\nEND", Dialect.Pb36);
    Assert.That(empty.Length, Is.LessThan(nonEmpty.Length),
      "s = \"\" tests the handle (rt_strcmp trimmed); s = \"x\" keeps the StrCmp call");
  }

  [Test]
  public void Emit_GivenUnsignedComparisonAsValue_WhenPb36_ThenBranchlessSbb() {
    // O0088: f = (a < b) over WORD operands used as a value tests the carry the CMP already set,
    // so SBB AX,AX (19 C0) turns it into PB's -1/0 in two bytes rather than MOV -1 / Jcc / MOV 0.
    // Module WORDs (not params) so nothing is IPCP-folded; INPUT makes them genuinely runtime.
    const string source = "DIM a AS WORD, b AS WORD, f AS INTEGER\nINPUT a\nINPUT b\nf = (a < b)\nPRINT f\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var optimized = new CodeGenerator(model).EmitExecutable();
    var plain = new CodeGenerator(model) { Optimize = false }.EmitExecutable();
    Assert.Multiple(() => {
      Assert.That(CountPair(optimized, 0x19, 0xC0), Is.GreaterThan(0), "SBB AX,AX materializes the unsigned-< truth value");
      Assert.That(CountPair(plain, 0x19, 0xC0), Is.Zero, "the unoptimized path keeps the MOV -1 / Jcc / MOV 0 branch");
      Assert.That(CountPair(plain, 0xB8, 0xFF), Is.GreaterThan(0), "...which materializes -1 with MOV AX,-1");
    });
  }

  private static int CountPair(byte[] image, byte first, byte second) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == first && image[i + 1] == second)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenNoInlineFunction_WhenPb36_ThenSurvivesAsARealProcedure() {
    // a one-expression FUNCTION is the inliner's bread and butter: without NOINLINE it is
    // substituted at the only call site and then purged, with it the procedure stays
    const string body = "DECLARE FUNCTION Twice&(BYVAL x&)\nPRINT Twice(21)\nEND\nFUNCTION Twice&(BYVAL x&)__\nTwice = x& + x&\nEND FUNCTION";
    Assert.That(Procedures(body.Replace("__", " NOINLINE")), Does.Contain("Twice"), "NOINLINE must keep the procedure");
    Assert.That(Procedures(body.Replace("__", "")), Does.Not.Contain("Twice"), "without it the inliner should absorb it");
  }

  /// <summary>The names of the procedures that survive to the emitted image.</summary>
  private static IEnumerable<string> Procedures(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return generator.DescribeImage().Procedures.Select(p => p.Name);
  }

  [Test]
  public void Emit_GivenLongCompareRangeKnown_WhenPb36_ThenNarrowedTo16BitCompare() {
    // both operands of the LONG compare are range-known (a FOR counter 1..100 against a
    // constant), so the nine-instruction 32-bit sequence collapses to one CMP AX, BX. The
    // INPUT-sourced variant has the identical shape but an unknown range, so it keeps the
    // wide compare (SBB DX, CX).
    const string narrowed = "$OPTIMIZE SPEED\nDIM n AS LONG\nFOR i& = 1 TO 100\nIF i& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    const string wide = "$OPTIMIZE SPEED\nDIM n AS LONG\nINPUT k&\nFOR i& = 1 TO 100\nIF k& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    Assert.That(CountSbbDxCx(Compile(narrowed, Dialect.Pb36)), Is.Zero, "a range-known LONG compare should not emit the 32-bit sequence");
    Assert.That(CountCmpAxBx(Compile(narrowed, Dialect.Pb36)), Is.GreaterThan(0), "it should compare in 16 bits instead");
    Assert.That(CountSbbDxCx(Compile(wide, Dialect.Pb36)), Is.GreaterThan(0), "an unknown-range LONG compare must keep the 32-bit sequence");
  }

  [Test]
  public void Emit_GivenLongCompareRangeKnown_WhenOptimizerOff_ThenWideCompareKept() {
    // the narrowing is gated on Optimize, so the faithful build is untouched (golden gate)
    const string source = "DIM n AS LONG\nFOR i& = 1 TO 100\nIF i& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    Assert.That(CountSbbDxCx(Compile(source, Dialect.Pb35)), Is.GreaterThan(0));
  }

  [Test]
  public void Emit_GivenDwordMultiplyRangeKnown_WhenPb36_ThenNarrowedTo16BitMul() {
    // $ERROR NUMERIC ON keeps an unsigned multiply integral (no float promotion), so it
    // reaches the 32-bit path; both operands are range-known, so it becomes one MUL BX
    // instead of the three-MUL rt_lmul call. The INPUT-sourced variant keeps the call.
    const string narrowed = "$ERROR NUMERIC ON\n$OPTIMIZE SPEED\nDIM c AS DWORD\nFOR i& = 1 TO 100\na??? = i&\nb??? = 3\nc = a??? * b???\nNEXT i&\nPRINT c\nEND";
    const string wide = "$ERROR NUMERIC ON\n$OPTIMIZE SPEED\nDIM c AS DWORD\nINPUT k&\nFOR i& = 1 TO 100\na??? = k&\nb??? = 3\nc = a??? * b???\nNEXT i&\nPRINT c\nEND";
    Assert.That(CountMulBx(Compile(narrowed, Dialect.Pb36)),
      Is.GreaterThan(CountMulBx(Compile(wide, Dialect.Pb36))),
      "a range-known DWORD multiply should add a 16-bit MUL BX the runtime-call version lacks");
  }

  [Test]
  public void Execute_GivenLongCompareRangeKnown_WhenPb36_ThenSameResultsAsWide() {
    // the narrowed compare must decide exactly like the 32-bit one across the sign
    // boundary: negative counters, both int16 endpoints, and the equality forms
    const string source = """
      $OPTIMIZE SPEED
      DIM lo AS LONG, hi AS LONG, eq AS LONG
      FOR i& = -32768 TO -32760
        IF i& < -32764& THEN lo = lo + 1
        IF i& >= -32764& THEN hi = hi + 1
        IF i& = -32768& THEN eq = eq + 1
      NEXT i&
      FOR j& = 32760 TO 32767
        IF j& > 32764& THEN hi = hi + 1
        IF j& <= 32764& THEN lo = lo + 1
      NEXT j&
      PRINT lo; hi; eq
      END
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo(" 9  8  1\n"));
  }

  [Test]
  public void Execute_GivenDwordMultiplyRangeKnown_WhenPb36_ThenSameProductAsWide() {
    // the narrowed MUL must produce the full 32-bit product, including the upper word
    const string source = """
      $ERROR NUMERIC ON
      $OPTIMIZE SPEED
      DIM c AS DWORD, t AS DWORD
      FOR i& = 65530 TO 65535
        a??? = i&
        b??? = 65535
        c = a??? * b???
        t = t + (c AND 1023)
      NEXT i&
      PRINT c; t
      END
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo(" 4294836225  21\n"));
  }

  [Test]
  public void Emit_GivenQuadBitwiseUnderCpu386_WhenPb36_ThenInlineDwordOps() {
    // a QUAD OR runs inline as two 66 0B (OR EAX, m32) halves instead of the QuadOr call
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nx&& = 1099511627775\ny&& = 76861433640456465\nPRINT x&& OR y&&\nEND";
    const string no386 = "$OPTIMIZE SPEED\nx&& = 1099511627775\ny&& = 76861433640456465\nPRINT x&& OR y&&\nEND";
    Assert.That(CountDwordOrEax(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountDwordOrEax(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should add inline 32-bit OR halves the runtime-call version lacks");
  }

  // 66 0B = operand-size-prefixed OR r32, r/m32
  private static int CountDwordOrEax(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x0B)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenQuadShiftUnderCpu386_WhenPb36_ThenDoublePrecisionShld() {
    // a constant-count QUAD SHIFT LEFT collapses the per-bit loop to a 66 0F A4 SHLD
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nx&& = 3\nSHIFT LEFT x&&, 5\nPRINT x&&\nEND";
    const string no386 = "$OPTIMIZE SPEED\nx&& = 3\nSHIFT LEFT x&&, 5\nPRINT x&&\nEND";
    Assert.That(CountShld(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountShld(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should add a 66 0F A4 SHLD the per-bit-loop version lacks");
  }

  // 66 0F A4 = operand-size-prefixed SHLD r/m32, r32, imm8
  private static int CountShld(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x0F && image[i + 2] == 0xA4)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenEraseStaticArrayUnderCpu386_WhenPb36_ThenRepStosd() {
    // ERASE of a static array zeroes it DWORD-wide (F3 66 AB) instead of REP STOSW
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\na%(1) = 5\nERASE a%\nPRINT a%(1)\nEND";
    const string no386 = "$OPTIMIZE SPEED\nDIM a%(1 TO 10)\na%(1) = 5\nERASE a%\nPRINT a%(1)\nEND";
    Assert.That(CountRepStosd(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountRepStosd(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should zero-fill the ERASEd array with REP STOSD");
  }

  [Test]
  public void Emit_GivenConstantArrayFillUnderCpu386_WhenPb36_ThenRepStosd() {
    // a FOR-loop constant array fill stores two elements per REP STOSD instead of REP STOSW
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = 1234\nNEXT i%\nPRINT a%(1)\nEND";
    const string no386 = "$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = 1234\nNEXT i%\nPRINT a%(1)\nEND";
    Assert.That(CountRepStosd(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountRepStosd(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should fill the array DWORD-wide with REP STOSD");
  }

  [Test]
  public void Emit_GivenLatticeProvenCondition_WhenPb36_ThenDeadArmNotEmitted() {
    // p% is [5,8] (IF-join), so `p% < 20` is always true - the ELSE arm is unreachable and its
    // code (the "DEADXYZ" literal) is not emitted at all; an INPUT-sourced p% keeps both arms.
    const string folds = "$OPTIMIZE SPEED\np% = 5\nIF q% > 0 THEN p% = 8\nIF p% < 20 THEN\nPRINT \"LIVE\"\nELSE\nPRINT \"DEADXYZ\"\nEND IF\nEND";
    const string nofold = "$OPTIMIZE SPEED\nINPUT p%\nIF p% < 20 THEN\nPRINT \"LIVE\"\nELSE\nPRINT \"DEADXYZ\"\nEND IF\nEND";
    Assert.That(Ascii(Compile(folds, Dialect.Pb36)), Does.Not.Contain("DEADXYZ"),
      "a lattice-proven-false arm should be dead-code-eliminated");
    Assert.That(Ascii(Compile(nofold, Dialect.Pb36)), Does.Contain("DEADXYZ"),
      "an unknown condition keeps both arms");
  }

  [Test]
  public void Emit_GivenLatticeBoundedIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    // k% is [5,10] (an IF-join, not a constant and not a FOR counter) - the interval lattice
    // proves it lies inside a%(0 TO 20), so the bounds check drops; an INPUT-sourced k% is unknown
    // and keeps the check. Exercises the lattice wired into IndexRangeOf for arbitrary variables.
    const string bounded = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 20)\nk% = 5\nIF c% > 0 THEN k% = 10\na%(k%) = k%\nEND";
    const string unknown = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 20)\nINPUT k%\na%(k%) = k%\nEND";
    Assert.That(CountRaise9(Compile(bounded, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(unknown, Dialect.Pb36))),
      "a lattice-bounded variable index inside the array bounds should drop the Error-9 check");
  }

  [Test]
  public void Emit_GivenForCounterIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    // a%(i%) with i% the in-bounds FOR counter drops its bounds check; a%(k%) keeps it.
    // The store value is non-constant so the constant-fill idiom does not confound the count.
    const string counterIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = i%\nNEXT i%\nEND";
    const string varIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nk% = 5\nFOR i% = 1 TO 10\na%(k%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(counterIdx, Dialect.Pb36)),
      Is.LessThan(CountRaise9(Compile(varIdx, Dialect.Pb36))),
      "a FOR-counter index inside the array bounds should drop the Error-9 bounds check");
  }

  [Test]
  public void Emit_GivenTwoRangeIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    // a%(i% + j%) with i% the [2,9] FOR counter and j% = i% - 1 a derived [1,8] var:
    // the index range [3,17] lies inside the (0 TO 30) bounds, so the Error-9 check drops.
    // An INPUT-sourced j% is unknown, so that variant keeps the check.
    const string twoRange = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 30)\nFOR i% = 2 TO 9\nj% = i% - 1\na%(i% + j%) = i%\nNEXT i%\nEND";
    const string defeated = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 30)\nINPUT j%\nFOR i% = 2 TO 9\na%(i% + j%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(twoRange, Dialect.Pb36)),
      Is.LessThan(CountRaise9(Compile(defeated, Dialect.Pb36))),
      "an index summing two range-known vars, provably in bounds, should drop the Error-9 check");
  }

  [Test]
  public void Emit_GivenMaskedAndModIndexUnderBoundsOn_WhenPb36_ThenInRangeCheckElided() {
    // a(x AND 7) is always in [0,7] (the mask keeps only the low bits); a(i% MOD 8) over a
    // non-negative counter is in [0,7]. Both lie inside a(0 TO 7), so pb36 drops the Error-9
    // bounds check that pb35 (no range lattice) keeps.
    const string andIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 7)\nFOR i% = 1 TO 50\na%(i% AND 7) = i%\nNEXT i%\nEND";
    const string modIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 7)\nFOR i% = 0 TO 50\na%(i% MOD 8) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(andIdx, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(andIdx, Dialect.Pb35))),
      "x AND 7 is always in [0,7] - the bounds check should drop");
    Assert.That(CountRaise9(Compile(modIdx, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(modIdx, Dialect.Pb35))),
      "i% MOD 8 over a non-negative counter is in [0,7] - the bounds check should drop");
  }

  [Test]
  public void Emit_GivenDividedIndexUnderBoundsOn_WhenPb36_ThenInRangeCheckElided() {
    // a(i% \ 2) over i% in [0,30] is in [0,15] (truncated divide is monotonic in the dividend),
    // inside a(0 TO 15), so pb36 drops the Error-9 bounds check that pb35 keeps.
    const string idx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 15)\nFOR i% = 0 TO 30\na%(i% \\ 2) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(idx, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(idx, Dialect.Pb35))),
      "i% \\ 2 over [0,30] is in [0,15] - the bounds check should drop");
  }

  [Test]
  public void Emit_GivenForCounterAddUnderOverflowOn_WhenPb36_ThenCheckElided() {
    // i% + 1 over an in-range FOR counter drops its Error-6 check; k% + 1 keeps it
    const string counterAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i% = 1 TO 100\nx% = i% + 1\nNEXT i%\nEND";
    const string varAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k%\nFOR i% = 1 TO 100\nx% = k% + 1\nNEXT i%\nEND";
    Assert.That(CountRaise6(Compile(counterAdd, Dialect.Pb36)),
      Is.LessThan(CountRaise6(Compile(varAdd, Dialect.Pb36))),
      "an in-range FOR-counter add should drop its Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenLongForCounterAddUnderOverflowOn_WhenPb36_ThenCheckElided() {
    // a LONG i& + 1& over [1,100] -> [2,101] stays inside 32 bits and drops its Error-6
    // check; a LONG k& + 1& with an unknown k& keeps the 32-bit ADD/ADC overflow trap
    const string counterAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i& = 1 TO 100\nx& = i& + 1&\nNEXT i&\nEND";
    const string varAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k&\nFOR i& = 1 TO 100\nx& = k& + 1&\nNEXT i&\nEND";
    Assert.That(CountRaise6(Compile(counterAdd, Dialect.Pb36)),
      Is.LessThan(CountRaise6(Compile(varAdd, Dialect.Pb36))),
      "an in-range LONG FOR-counter add should drop its 32-bit Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenLongForCounterSubtractUnderOverflowOn_WhenPb36_ThenCheckElided() {
    // a LONG i& - 1& over [1,100] -> [0,99] stays inside 32 bits and drops its Error-6 check
    const string counterSub = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i& = 1 TO 100\nx& = i& - 1&\nNEXT i&\nEND";
    const string varSub = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k&\nFOR i& = 1 TO 100\nx& = k& - 1&\nNEXT i&\nEND";
    Assert.That(CountRaise6(Compile(counterSub, Dialect.Pb36)),
      Is.LessThan(CountRaise6(Compile(varSub, Dialect.Pb36))),
      "an in-range LONG FOR-counter subtract should drop its 32-bit Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenDivideByForCounter_WhenPb36_ThenZeroGuardElided() {
    // 100 \ i% with i% a [1,10] counter (excludes 0) drops the TEST BX,BX zero guard;
    // 100 \ k% keeps it
    const string counterDiv = "$OPTIMIZE SPEED\nFOR i% = 1 TO 10\nx% = 100 \\ i%\nNEXT i%\nPRINT x%\nEND";
    // a SUB parameter divisor (differing call args) is non-constant and not range-known
    const string varDiv = "$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL k AS INTEGER)\nd 3\nd 7\nEND\nSUB d(BYVAL k AS INTEGER)\nPRINT 100 \\ k\nEND SUB";
    Assert.That(CountTestBxBx(Compile(counterDiv, Dialect.Pb36)),
      Is.LessThan(CountTestBxBx(Compile(varDiv, Dialect.Pb36))),
      "a divisor whose counter range excludes zero should drop the divide-by-zero guard");
  }

  [Test]
  public void Emit_GivenLiteralSelfAppend_WhenPb36Speed_ThenInPlaceNoLiteralAlloc() {
    // s$ = s$ + "x" appends the literal in place (rt_strcatlit) - the literal is NOT materialized
    // via StrMem at the call site, so no MOV DX,DS (8C DA) there. s$ = "x" + s$ (prepend) still
    // materializes the literal through StrMem, emitting the 8C DA. The init is identical, so the
    // append image has strictly fewer 8C DA than the prepend image.
    const string append = "$OPTIMIZE SPEED\ns$ = \"z\"\nFOR i% = 1 TO 3\ns$ = s$ + \"x\"\nNEXT i%\nPRINT s$\nEND";
    const string prepend = "$OPTIMIZE SPEED\ns$ = \"z\"\nFOR i% = 1 TO 3\ns$ = \"x\" + s$\nNEXT i%\nPRINT s$\nEND";
    Assert.That(CountMovDxDs(Compile(append, Dialect.Pb36)), Is.LessThan(CountMovDxDs(Compile(prepend, Dialect.Pb36))),
      "a literal self-append should append in place, not materialize the literal at the call site");
  }

  // 8C DA = MOV DX, DS - the segment setup emitted before a StrMem literal materialization
  private static int CountMovDxDs(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8C && image[i + 1] == 0xDA)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenVariableSelfAppend_WhenPb36Speed_ThenCallsInPlaceRoutine() {
    // s$ = s$ + v$ emits a CALL to the in-place rt_strcatvar routine; a literal self-append
    // s$ = s$ + "x" calls rt_strcatlit instead, so it makes zero calls to rt_strcatvar.
    const string withVar = "$OPTIMIZE SPEED\ns$ = \"a\"\nv$ = \"b\"\nFOR i% = 1 TO 3\ns$ = s$ + v$\nNEXT i%\nPRINT s$\nEND";
    const string literal = "$OPTIMIZE SPEED\ns$ = \"a\"\nFOR i% = 1 TO 3\ns$ = s$ + \"x\"\nNEXT i%\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatVar(Compile(withVar, Dialect.Pb36)), Is.GreaterThan(0),
      "a variable self-append should call rt_strcatvar");
    Assert.That(CountCallsToStrCatVar(Compile(literal, Dialect.Pb36)), Is.Zero,
      "a literal self-append should not call rt_strcatvar");
  }

  // count E8 (near CALL) sites whose signed rel16 target lands on the rt_strcatvar entry. Its
  // entry is TEST DX,DX / JNZ +1 / RET / TEST AX,AX = 85 D2 75 01 C3 85 C0 (short-form branch:
  // relaxation runs on every optimized image). File-relative offsets equal the in-memory rel16.
  private static readonly byte[] _strCatVarHead = { 0x85, 0xD2, 0x75, 0x01, 0xC3, 0x85, 0xC0 };
  private static int CountCallsToStrCatVar(byte[] image) {
    var head = -1;
    for (var i = 0; i + _strCatVarHead.Length <= image.Length && head < 0; ++i) {
      var match = true;
      for (var j = 0; j < _strCatVarHead.Length; ++j)
        if (image[i + j] != _strCatVarHead[j]) { match = false; break; }
      if (match)
        head = i;
    }
    if (head < 0)
      return 0;
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xE8 && i + 3 + (short)(image[i + 1] | (image[i + 2] << 8)) == head)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenSubstringFunctionLeft_WhenPb36_ThenTailAppendedInPlace() {
    // LEFT$/RIGHT$/MID$ construct a fresh, dead, topmost temp - like a concat - so a tail operand
    // appends to it in place (rt_strcatvar); a bare-variable left is LIVE storage, so it does not.
    const string funcLeft = "$OPTIMIZE SPEED\nx$ = \"hello\"\nv$ = \"!\"\ns$ = LEFT$(x$, 3) + v$\nPRINT s$\nEND";
    const string varLeft = "$OPTIMIZE SPEED\nx$ = \"hello\"\nv$ = \"!\"\ns$ = x$ + v$\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatVar(Compile(funcLeft, Dialect.Pb36)), Is.GreaterThan(CountCallsToStrCatVar(Compile(varLeft, Dialect.Pb36))),
      "a LEFT$/MID$ result is a dead temp whose tail appends in place; a bare-variable left does not");
  }

  [Test]
  public void Emit_GivenNonLeftLeaningConcat_WhenPb36_ThenSingleAllocMultiConcat() {
    // (a$+b$) + (c$+d$): a four-leaf tree of plain string variables. O24 (multi-concat) subsumes the
    // older O9 dead-temp reuse for chains of three or more operands - it flattens the tree to
    // [a$,b$,c$,d$] and builds it with ONE rt_strcatn allocation (strictly better than the pairwise
    // in-place append). A call operand (UCASE$) returns a shared/volatile buffer that a later operand
    // would clobber, so it is NOT safe to pre-stage: such a chain falls back to the pairwise/O9 path
    // and does NOT use rt_strcatn (correctness over the allocation win).
    const string balanced = "$OPTIMIZE SPEED\na$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\ns$ = (a$ + b$) + (c$ + d$)\nPRINT s$\nEND";
    const string impure = "$OPTIMIZE SPEED\na$=\"a\"\nb$=\"b\"\nc$=\"c\"\ns$ = (a$ + b$) + UCASE$(c$)\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(balanced, Dialect.Pb36)), Is.EqualTo(1),
      "a four-leaf concat tree of plain variables builds with one rt_strcatn allocation");
    Assert.That(CountCallsToStrCatN(Compile(impure, Dialect.Pb36)), Is.Zero,
      "a chain whose operand is a call (shared/volatile buffer) is not pre-staged - it falls back off rt_strcatn");
  }

  [Test]
  public void Emit_GivenConcatChain_WhenPb36_ThenSingleAllocMultiConcat() {
    // a$ + b$ + c$ is a three-leaf chain: O24 builds it with one rt_strcatn allocation (it subsumes
    // the older O9 pairwise reuse for >=3 operands). A plain two-operand a$ + b$ stays on StrCat.
    const string chain = "$OPTIMIZE SPEED\na$ = \"a\"\nb$ = \"b\"\nc$ = \"c\"\ns$ = a$ + b$ + c$\nPRINT s$\nEND";
    const string pair = "$OPTIMIZE SPEED\na$ = \"a\"\nb$ = \"b\"\ns$ = a$ + b$\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb36)), Is.EqualTo(1),
      "a three-operand concat chain builds with one rt_strcatn allocation");
    Assert.That(CountCallsToStrCatN(Compile(pair, Dialect.Pb36)), Is.Zero,
      "a two-operand concat does not use the multi-concat builder");
  }

  [Test]
  public void Emit_GivenStringSelfAppend_WhenPb36_ThenSmallerThanNonSelf() {
    // s$ = s$ + x$ skips the StrDup of s$ and the StrAssign (StrCat consumes s$ directly),
    // so it emits less code than the otherwise-identical non-self s$ = t$ + x$
    const string selfAppend = "$OPTIMIZE SPEED\ns$ = \"a\"\nt$ = \"c\"\nx$ = \"b\"\ns$ = s$ + x$\nPRINT s$; t$\nEND";
    const string nonSelf = "$OPTIMIZE SPEED\ns$ = \"a\"\nt$ = \"c\"\nx$ = \"b\"\ns$ = t$ + x$\nPRINT s$; t$\nEND";
    Assert.That(Compile(selfAppend, Dialect.Pb36).Length,
      Is.LessThan(Compile(nonSelf, Dialect.Pb36).Length),
      "a string self-append should emit less code than the non-self concat");
  }

  // O24 multi-concat: the rt_strcatn entry prologue PUSH BX/CX/DX/SI/DI/ES then MOV [rt_st3],CX
  // = 53 51 52 56 57 06 89 0E. Count E8 (near CALL) sites whose rel16 target lands on that entry.
  private static readonly byte[] _strCatNHead = { 0x53, 0x51, 0x52, 0x56, 0x57, 0x06, 0x89, 0x0E };
  private static int CountCallsToStrCatN(byte[] image) {
    var head = -1;
    for (var i = 0; i + _strCatNHead.Length <= image.Length && head < 0; ++i) {
      var match = true;
      for (var j = 0; j < _strCatNHead.Length; ++j)
        if (image[i + j] != _strCatNHead[j]) { match = false; break; }
      if (match)
        head = i;
    }
    if (head < 0)
      return 0;
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xE8 && i + 3 + (short)(image[i + 1] | (image[i + 2] << 8)) == head)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFourOperandConcatChain_WhenPb36_ThenSingleAllocMultiConcat() {
    // r$ = a$ & b$ & c$ & d$ is a 4-leaf chain: it builds with ONE rt_strcatn call (a single heap
    // allocation) instead of the pairwise StrCat chain. A plain two-operand concat does not qualify.
    const string chain = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\nr$ = a$ & b$ & c$ & d$\nPRINT r$\nEND";
    const string pair = "a$=\"a\"\nb$=\"b\"\nr$ = a$ & b$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb36)), Is.EqualTo(1),
      "a four-operand concat chain takes the single-allocation rt_strcatn path exactly once");
    Assert.That(CountCallsToStrCatN(Compile(pair, Dialect.Pb36)), Is.Zero,
      "a two-operand concat does not use the multi-concat builder");
  }

  [Test]
  public void Emit_GivenThreeOperandConcat_WhenPb36_ThenSingleAllocMultiConcat() {
    // boundary: three leaves is the smallest chain the multi-concat builder fires on (two go to O9).
    const string three = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nr$ = a$ + b$ + c$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(three, Dialect.Pb36)), Is.EqualTo(1),
      "a three-operand concat is the boundary case that fires the single-allocation builder");
  }

  [Test]
  public void Emit_GivenMultiConcat_WhenPb35_ThenNoMultiConcatBuilder() {
    // the optimization is strictly Optimize-gated: pb35 (unoptimized) never calls rt_strcatn, so its
    // string concatenation lowering - and thus its observable output - is unchanged.
    const string chain = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\nr$ = a$ & b$ & c$ & d$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb35)), Is.Zero,
      "pb35 must not take the multi-concat path");
  }

  [Test]
  public void Emit_GivenMultiConcatWithCallOperand_WhenPb36_ThenFallsBackOffTheSingleAllocBuilder() {
    // a string-returning function call yields a SHARED/volatile result buffer: a later operand's
    // evaluation would clobber it, so pre-staging all operands then concatenating (rt_strcatn) would
    // alias it (e.g. F$()&G$()&H$() -> "HHH"). The multi-concat builder therefore refuses a chain that
    // contains a call and falls back to the pairwise path, which consumes each operand immediately.
    const string withCall = "DECLARE FUNCTION F$()\n"
      + "a$=\"a\"\nc$=\"c\"\nr$ = a$ & F$() & c$\nPRINT r$\nEND\n"
      + "FUNCTION F$()\nF$ = \"x\"\nEND FUNCTION";
    Assert.That(CountCallsToStrCatN(Compile(withCall, Dialect.Pb36)), Is.Zero,
      "a chain containing a call operand is not pre-staged - it falls back off rt_strcatn for correctness");
  }

  // 85 DB = TEST BX, BX - the divide-by-zero guard set up by EmitInt16DivideGuard
  private static int CountTestBxBx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x85 && image[i + 1] == 0xDB)
        ++count;
    return count;
  }

  // B8 06 00 = MOV AX, 6 - the Error 6 (overflow) raise set up by EmitRaiseWhen
  private static int CountRaise6(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xB8 && image[i + 1] == 0x06 && image[i + 2] == 0x00)
        ++count;
    return count;
  }

  // B8 09 00 = MOV AX, 9 - the Error 9 (subscript) raise set up by EmitRaiseWhen
  private static int CountRaise9(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xB8 && image[i + 1] == 0x09 && image[i + 2] == 0x00)
        ++count;
    return count;
  }

  // F3 66 AB = REP STOSD (dword store)
  private static int CountRepStosd(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xF3 && image[i + 1] == 0x66 && image[i + 2] == 0xAB)
        ++count;
    return count;
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
  public void Emit_GivenAccumulatorLoop_WhenPb36Speed_ThenSmallerFromRegisterResidency() {
    // s% = s% + i% over a SI/DI-clean FOR loop keeps the counter in SI and the
    // accumulator in DI, so the per-iteration cell load/store of s% disappears
    const string body = "s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(speed.Length, Is.LessThan(plain.Length),
      "the register-resident accumulator should shrink the loop versus the memory-cell version");
  }

  [Test]
  public void Emit_GivenConditionalAccumulateLoop_WhenPb36Speed_ThenCounterInSi() {
    // a FOR loop whose body is a clean IF (SI-clean condition + scalar-assign arm) keeps the
    // counter in SI: the increment becomes ADD SI, imm (83 C6), which a memory-cell counter lacks.
    const string body = "s% = 0\nFOR i% = 1 TO 10\n  IF i% > 5 THEN s% = s% + i%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountAddSiImm(speed), Is.GreaterThan(CountAddSiImm(plain)),
      "a FOR counter over a clean-IF body should increment in SI (ADD SI, imm)");
  }

  [Test]
  public void Emit_GivenBinaryWithProvenConstantOperand_WhenPb36_ThenImmediateAlu() {
    // c% + b% where b% is an SCCP-proven constant folds the constant into one immediate ALU op
    // (ADD AX, imm8 = 83 C0) instead of push-left / eval-right / pop / add. The same loop over a
    // runtime-unknown operand (a BYVAL parameter, called with differing args so IPCP cannot prove
    // it constant either) keeps the register-to-register add - so it has one fewer immediate add.
    const string proven = "$OPTIMIZE SPEED\nb% = 5\nc% = 0\nFOR i% = 1 TO 10\n  c% = c% + b%\nNEXT i%\nPRINT c%\nEND";
    const string runtime = "$OPTIMIZE SPEED\nDECLARE SUB t(BYVAL k%)\nt 5\nt 7\nEND\nSUB t(BYVAL k%)\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + k%\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountAddAxImm(Compile(proven, Dialect.Pb36)), Is.GreaterThan(CountAddAxImm(Compile(runtime, Dialect.Pb36))),
      "a proven-constant operand should fold into an immediate ALU op (ADD AX, imm); a runtime parameter cannot");
  }

  // 83 C0 = ADD AX, imm8 - an immediate add (the folded-constant-operand form)
  private static int CountAddAxImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenDirectCellStore_WhenPb36_ThenNoValuePark() {
    // a store to a direct-cell variable needs no address computation, so the value is no longer
    // parked (push ax / pop ax) across EmitPlace. The same stores to a BYREF parameter DO need a
    // pointer load, so the park stays - one extra push per store.
    const string direct = "$OPTIMIZE SPEED\nDECLARE SUB s(x%)\ns 9\nEND\nSUB s(x%)\n  a% = x%\n  b% = x%\n  d% = x%\n  PRINT a%; b%; d%\nEND SUB";
    const string byref = "$OPTIMIZE SPEED\nDECLARE SUB s(x%)\ns 9\nEND\nSUB s(x%)\n  x% = x% + 1\n  x% = x% + 1\n  x% = x% + 1\n  PRINT x%\nEND SUB";
    Assert.That(CountPushAx(Compile(direct, Dialect.Pb36)), Is.LessThan(CountPushAx(Compile(byref, Dialect.Pb36))),
      "direct-cell stores drop the value park; BYREF stores keep it (one push per store)");
  }

  // 0x50 = PUSH AX (the value park before an address computation)
  private static int CountPushAx(byte[] image) {
    var count = 0;
    foreach (var b in image)
      if (b == 0x50)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenNumericPrintInLoopBody_WhenPb36Speed_ThenCounterStaysInSi() {
    // a PRINT of plain numeric items (and string literals, whose SI load is saved/restored) leaves
    // SI/DI intact, so a FOR counter over a printing body stays in SI (ADD SI, imm). A non-literal
    // string item (a string variable) prints via a path that may clobber SI, blocking residency.
    const string numeric = "$OPTIMIZE SPEED\ns% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\n  PRINT \"v=\"; s%\nNEXT i%\nEND";
    const string stringVar = "$OPTIMIZE SPEED\nz$ = \"v=\"\ns% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\n  PRINT z$; s%\nNEXT i%\nEND";
    Assert.That(CountAddSiImm(Compile(numeric, Dialect.Pb36)), Is.GreaterThan(CountAddSiImm(Compile(stringVar, Dialect.Pb36))),
      "a numeric/literal PRINT keeps the FOR counter in SI; a string-variable item blocks residency");
  }

  [Test]
  public void Emit_GivenSelectCaseInLoopBody_WhenPb36Speed_ThenCounterStaysInSi() {
    // an INTEGER SELECT CASE dispatches through AX/BX/DX (jump table or compare chain), never the
    // index registers, so a FOR counter over a SELECT body stays in SI (ADD SI, imm). A STRING
    // SELECT (string compares touch SI) is not SI-clean and blocks residency.
    const string intSel = "$OPTIMIZE SPEED\ns% = 0\nFOR i% = 1 TO 10\n  SELECT CASE i%\n  CASE 1, 3, 5\n    s% = s% + i%\n  CASE ELSE\n    s% = s% - 1\n  END SELECT\nNEXT i%\nPRINT s%\nEND";
    const string strSel = "$OPTIMIZE SPEED\nz$ = \"a\"\ns% = 0\nFOR i% = 1 TO 10\n  SELECT CASE z$\n  CASE \"a\"\n    s% = s% + i%\n  END SELECT\nNEXT i%\nPRINT s%\nEND";
    Assert.That(CountAddSiImm(Compile(intSel, Dialect.Pb36)), Is.GreaterThan(CountAddSiImm(Compile(strSel, Dialect.Pb36))),
      "an integer SELECT body keeps the FOR counter in SI; a string SELECT blocks residency");
  }

  [Test]
  public void Emit_GivenLongForLoop_WhenCpu386Speed_ThenCounterInEsi() {
    // a LONG FOR counter over an SI-clean body lives in the 32-bit register ESI under $CPU 80386:
    // the increment is ADD ESI, imm (66 83 C6), absent without $CPU 80386 (the counter then lives
    // in its 4-byte memory cell). The "true win" of the 386 path - a full LONG local in a register.
    const string body = "$OPTIMIZE SPEED\ns& = 0\nFOR i& = 1 TO 10\n  s& = s& + i&\n  PRINT s&\nNEXT i&\nPRINT s&\nEND";
    var with386 = Compile("$CPU 80386\n" + body, Dialect.Pb36);
    var no386 = Compile(body, Dialect.Pb36);
    Assert.That(CountAddEsiImm(with386), Is.GreaterThan(CountAddEsiImm(no386)),
      "a LONG FOR counter should increment in ESI (66 83 C6) under $CPU 80386");
  }

  [Test]
  public void Emit_GivenLongAccumulatorLoop_WhenCpu386Speed_ThenAccumulatorInEdi() {
    // a hot LONG accumulator joins the ESI counter in EDI under $CPU 80386 - two full LONG locals
    // resident at once. The accumulator is loaded into EDI (66 8B 3E); a loop that only prints the
    // counter has no LONG accumulator and so no EDI load.
    const string withAcc = "$CPU 80386\n$OPTIMIZE SPEED\ns& = 0\nFOR i& = 1 TO 10\n  s& = s& + i&\n  PRINT s&\nNEXT i&\nPRINT s&\nEND";
    const string noAcc = "$CPU 80386\n$OPTIMIZE SPEED\nFOR i& = 1 TO 10\n  PRINT i&\nNEXT i&\nEND";
    Assert.That(CountMovEdiMem(Compile(withAcc, Dialect.Pb36)), Is.GreaterThan(CountMovEdiMem(Compile(noAcc, Dialect.Pb36))),
      "a LONG accumulator joins the ESI counter in EDI (66 8B 3E) under $CPU 80386");
  }

  // 66 8B 3E = MOV EDI, [disp16] (operand-size-prefixed) - loading a LONG accumulator into EDI
  private static int CountMovEdiMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x8B && image[i + 2] == 0x3E)
        ++count;
    return count;
  }

  // 66 83 C6 = ADD ESI, imm8 (operand-size-prefixed) - the increment of an ESI-resident LONG counter
  private static int CountAddEsiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x83 && image[i + 2] == 0xC6)
        ++count;
    return count;
  }

  // 83 C6 = ADD SI, imm8 - the increment of an SI-resident FOR counter
  private static int CountAddSiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC6)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenNestedIntegerLoops_WhenPb36Speed_ThenInnerCounterInDi() {
    // a doubly-nested integer loop with SI/DI-clean bodies keeps the outer counter in SI
    // and the inner counter in DI: the inner increment becomes ADD DI, imm (83 C7), absent
    // when $OPTIMIZE SPEED is off (both counters then live in memory cells).
    const string body = "s% = 0\nFOR i% = 1 TO 8\n  FOR j% = 1 TO 8\n    s% = s% + i%\n  NEXT j%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountAddDiImm(speed), Is.GreaterThan(CountAddDiImm(plain)),
      "the inner FOR counter should increment in DI (ADD DI, imm) under SPEED");
  }

  // 83 C7 = ADD DI, imm8 - the increment of a DI-resident (nested) FOR counter
  private static int CountAddDiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC7)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenDoLoopAccumulator_WhenPb36Speed_ThenAccumulatorInSi() {
    // an SI/DI-clean DO/LOOP keeps its hot accumulator in SI (no FOR counter competes): the
    // accumulate becomes MOV SI, AX (89 F0), absent when $OPTIMIZE SPEED is off (s% lives in
    // its memory cell). Generalizes register residency beyond the FOR-loop shape.
    const string body = "s% = 0\ni% = 1\nDO\n  s% = s% + i%\n  i% = i% + 1\nLOOP UNTIL i% > 10\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountMovSiAx(speed), Is.GreaterThan(CountMovSiAx(plain)),
      "a DO-loop accumulator should be written in SI (MOV SI, AX) under SPEED");
  }

  [Test]
  public void Emit_GivenDoLoopTwoAccumulators_WhenPb36Speed_ThenSecondInDi() {
    // a DO loop has no counter, so both SI and DI are free: two hot accumulators live in
    // registers. The second is written via MOV DI, AX (89 F8), absent when SPEED is off.
    const string body = "s% = 0\np% = 1\ni% = 1\nDO\n  s% = s% + i%\n  p% = p% + 2\n  i% = i% + 1\nLOOP UNTIL i% > 8\nPRINT s%; p%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountMovDiAx(speed), Is.GreaterThan(CountMovDiAx(plain)),
      "a second DO-loop accumulator should live in DI (MOV DI, AX) under SPEED");
  }

  // 89 F8 = MOV DI, AX - the write of a DI-resident accumulator
  private static int CountMovDiAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x89 && image[i + 1] == 0xF8)
        ++count;
    return count;
  }

  // 89 F0 = MOV SI, AX - the write of an SI-resident accumulator
  private static int CountMovSiAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x89 && image[i + 1] == 0xF0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Speed_ThenShiftAddReplacesImul() {
    // x% is made opaque (BYREF call) so SCCP cannot fold it - this pins the
    // modular shift-add path, not whole-expression constant folding
    const string source = "$OPTIMIZE SPEED\nx% = 11\nT x%\ny% = x% * 3\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "x% * 3 under SPEED should be a shift-add chain, no IMUL BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThirteen_WhenPb36Speed_ThenKeepsCompactImul() {
    // 13 = 1101b: three set bits, not a contiguous run - no cheap shift chain,
    // so the compact IMUL BX is kept
    const string source = "$OPTIMIZE SPEED\nx% = 11\nT x%\ny% = x% * 13\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.EqualTo(1), "x% * 13 has no two-term decomposition, keep IMUL BX");
  }

  [Test]
  public void Emit_GivenDataNeverRead_WhenPb36_ThenDataBytesOmitted() {
    // DATA bytes nobody READs are dead - the pool labels stay (the runtime references rt_dataptr) but
    // the bytes are omitted, so the same program with a live READ (which keeps the bytes) is larger.
    const string withRead = "DIM q%\nREAD q%\nDATA 1111, 2222, 3333, 4444, 5555, 6666, 7777, 8888\nPRINT q%\nEND";
    const string noRead = "DATA 1111, 2222, 3333, 4444, 5555, 6666, 7777, 8888\nPRINT \"hi\"\nEND";
    Assert.That(Compile(noRead, Dialect.Pb36).Length, Is.LessThan(Compile(withRead, Dialect.Pb36).Length),
      "DATA that is never read should not emit its bytes");
  }

  [Test]
  public void Emit_GivenModularMultiplyByVariable_WhenPb36Speed_ThenReadsMemoryOperandNoImulBx() {
    // x% * z% (variable * variable): the right operand is a direct cell, so the modular path reads it
    // straight into the one-operand IMUL [mem] (DX:AX = AX*[mem]) instead of staging it through BX
    // (push left / eval right / mov bx / pop / IMUL BX) - smaller and faster, same low-16 product.
    const string source = "$OPTIMIZE SPEED\nx% = 11\nz% = 7\nT x%\nT z%\ny% = x% * z%\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "a variable*variable multiply should IMUL the direct-memory right operand, not stage it through BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Default_ThenKeepsImul() {
    // the shift chains are a SPEED trade (a few bytes for the cycles); SIZE/default
    // keep the 2-byte IMUL
    const string source = "x% = 11\nT x%\ny% = x% * 3\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.EqualTo(1), "without $OPTIMIZE SPEED the compact IMUL BX is kept");
  }

  [Test]
  public void Emit_GivenModularAddConstant_WhenPb36_ThenFewerBytesThanVariableAdd() {
    // y% = x% + 7 folds to one immediate ADD; y% = x% + z% must load and combine
    // a second operand, so the constant form is strictly smaller (x%/z% opaque)
    var constAdd = Compile(_TOUCH + "x% = 100\nT x%\ny% = x% + 7\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    var varAdd = Compile(_TOUCH + "x% = 100\nz% = 7\nT x%\nT z%\ny% = x% + z%\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
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

  // a BYREF call makes a variable opaque to SCCP so the O8 immediate path (not
  // whole-expression constant folding) is what these byte-level tests exercise
  private const string _TOUCH = "DECLARE SUB T(a%)\n";
  private const string _TOUCH_END = "\nSUB T(a%)\nEND SUB";
  private const string _TOUCHL = "DECLARE SUB TL(a&)\n";
  private const string _TOUCHL_END = "\nSUB TL(a&)\nEND SUB";

  [Test]
  public void Emit_GivenBitwiseMaskConstant_WhenPb36_ThenFoldsToImmediateNoRegisterLoad() {
    // y% = x% AND 15 folds the mask into AND AX,imm; the variable form y% = x% AND w% reads w%
    // straight as a memory operand (AND AX,[w%] = 23 06) - neither stages the operand through BX.
    var constMask = Compile(_TOUCH + "x% = 100\nT x%\ny% = x% AND 15\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    var varMask = Compile(_TOUCH + "x% = 100\nw% = 15\nT x%\nT w%\ny% = x% AND w%\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovBxAx(constMask), Is.Zero, "x% AND 15 should fold to AND AX,imm with no MOV BX,AX");
      Assert.That(CountMovBxAx(varMask), Is.Zero, "x% AND w% reads w% as a memory operand, not via MOV BX,AX");
      Assert.That(CountAndAxMem(varMask), Is.GreaterThanOrEqualTo(1), "x% AND w% should use AND AX,[w%]");
    });
  }

  [Test]
  public void Emit_GivenBinaryWithMemoryRightOperand_WhenPb36_ThenAluMemoryOperand() {
    // c% + n% with n% a direct-cell operand reads it as an ALU memory operand (ADD AX,[n%]), so it
    // needs no MOV BX,AX staging; an expression right operand (n% * i%) must still be staged via BX.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + n%\n  NEXT i%\n  PRINT c%\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + (n% * i%)\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountMovBxAx(Compile(mem, Dialect.Pb36)), Is.LessThan(CountMovBxAx(Compile(staged, Dialect.Pb36))),
      "a direct-cell right operand is read as an ALU memory operand, not staged through BX");
  }

  // 23 06 = AND AX, [disp16] - the memory-operand bitwise form
  private static int CountAndAxMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x23 && image[i + 1] == 0x06)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenCompareWithMemoryRightOperand_WhenPb36_ThenCmpMemoryOperand() {
    // i% > n% with n% a direct cell compares it as a memory operand (CMP AX,[n%]); an expression
    // right operand (n% * i%) must be staged through BX first.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  c% = 0\n  FOR i% = 1 TO 10\n    IF i% > n% THEN c% = c% + 1\n  NEXT i%\n  PRINT c%\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  c% = 0\n  FOR i% = 1 TO 10\n    IF i% > (n% * i%) THEN c% = c% + 1\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountCmpMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountCmpMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell compare operand is read as a CMP memory operand (CMP AX,[n%]); a staged operand is CMP AX,BX");
  }

  // 3B /r with a memory mod field (mod != 11) = CMP r16, [mem] - the memory-operand compare form
  private static int CountCmpMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x3B && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenSelfModifyStore_WhenPb36_ThenMemoryReadModifyWrite() {
    // a% = a% + 1 on a non-resident direct cell becomes INC [a%] (one instruction); the same
    // increment of a DIFFERENT target (b% = a% + 1) cannot read-modify-write and uses load/inc/store.
    const string rmw = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  a% = n%\n  a% = a% + 1\n  PRINT a%\nEND SUB";
    const string nonrmw = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%)\n  a% = n%\n  b% = a% + 1\n  PRINT a%; b%\nEND SUB";
    Assert.That(CountIncMem(Compile(rmw, Dialect.Pb36)), Is.GreaterThan(CountIncMem(Compile(nonrmw, Dialect.Pb36))),
      "a self-increment of a direct cell becomes INC [mem]; an increment into a different target does not");
  }

  // FF /0 with a memory mod field = INC word [mem] - the memory read-modify-write increment
  private static int CountIncMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xFF && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenIncrWithAmount_WhenPb36_ThenMemoryAddImmediate() {
    // INCR a%, 5 on a non-resident direct cell becomes ADD [a%],5 (one immediate, no AX park);
    // INCR of an array element needs an address computation and stages the amount through AX.
    const string direct = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a% = n%\n  INCR a%, 5\n  INCR a%, 6\n  PRINT a%\nEND SUB";
    const string array = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  DIM z%(0 TO 3)\n  z%(1) = n%\n  INCR z%(1), 5\n  INCR z%(1), 6\n  PRINT z%(1)\nEND SUB";
    Assert.That(CountAddMemImm(Compile(direct, Dialect.Pb36)), Is.GreaterThan(CountAddMemImm(Compile(array, Dialect.Pb36))),
      "INCR of a direct cell with a constant amount uses ADD [mem],imm; an array element does not");
  }

  // 83 /0 with a memory mod field = ADD word [mem], imm8 (sign-extended) - the memory add-immediate
  private static int CountAddMemImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatBinaryWithDirectCellOperand_WhenPb36_ThenFpuMemoryOperand() {
    // r! = a! + b! with b! a direct cell adds it straight from memory (FADD m32); an expression
    // right operand (b! * a!) must be FLD-ed onto the stack and combined with FADDP.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  b! = n% + 1\n  r! = a! + b!\n  r! = r! + b!\n  PRINT r!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  b! = n% + 1\n  r! = a! + (b! * a!)\n  r! = r! + (b! * a!)\n  PRINT r!\nEND SUB";
    Assert.That(CountFaddMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFaddMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell float operand is added as an FPU memory operand (FADD m32); a staged operand uses FADDP");
  }

  // D8 /0 (m32) or DC /0 (m64) with a memory mod field = FADD real [mem] - the x87 add memory operand
  private static int CountFaddMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatCompareWithDirectCellOperand_WhenPb36_ThenFcompMemoryOperand() {
    // IF a! < b! with b! a direct cell compares it as an FPU memory operand (FCOMP m32); an
    // expression right operand (b! * a!) must be FLD-ed and compared with FXCH;FCOMPP.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  b! = n% + 1\n  IF a! < b! THEN PRINT \"lt\"\n  IF a! > b! THEN PRINT \"gt\"\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  b! = n% + 1\n  IF a! < (b! * a!) THEN PRINT \"lt\"\n  IF a! > (b! * a!) THEN PRINT \"gt\"\nEND SUB";
    Assert.That(CountFcompMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFcompMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell float compare operand uses FCOMP m32; a staged operand uses FXCH;FCOMPP");
  }

  // D8 /3 (m32) or DC /3 (m64) with a memory mod field = FCOMP real [mem] - the x87 compare memory operand
  private static int CountFcompMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0x18 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatTimesIntegerCell_WhenPb36_ThenFpuIntegerMemoryOperand() {
    // x! = x! + i% with i% a signed-integer direct cell reads it with FIADD m16 (no AX load,
    // no FILD scratch); an expression right operand (i% + 1) must be loaded and FILD-ed.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  x! = n%\n  i% = n% + 1\n  x! = x! + i%\n  x! = x! + i%\n  PRINT x!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  x! = n%\n  i% = n% + 1\n  x! = x! + (i% + 1)\n  x! = x! + (i% + 1)\n  PRINT x!\nEND SUB";
    Assert.That(CountFiaddMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFiaddMem(Compile(staged, Dialect.Pb36))),
      "a signed-integer direct-cell operand is added to a float with FIADD m16; a staged operand uses FILD;FADDP");
  }

  // DE /0 (m16) or DA /0 (m32) with a memory mod field = FIADD int [mem] - the x87 integer add memory operand
  private static int CountFiaddMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xDE or 0xDA && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatTimesConstant_WhenPb36_ThenFpuConstantMemoryOperand() {
    // r! = a! * 1.5 multiplies by the data-segment float constant in place (FMUL qword [f_n]);
    // an expression right operand (b! + b!) must be FLD-ed and combined with FMULP.
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  r! = a! * 1.5\n  r! = r! * 2.5\n  PRINT r!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%)\n  a! = n%\n  b! = n% + 1\n  r! = a! * (b! + b!)\n  r! = r! * (b! + b!)\n  PRINT r!\nEND SUB";
    Assert.That(CountFmulMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFmulMem(Compile(staged, Dialect.Pb36))),
      "a float constant operand multiplies via an FPU memory operand (FMUL qword [f_n]); an expression operand uses FMULP");
  }

  // D8 /1 (m32) or DC /1 (m64) with a memory mod field = FMUL real [mem] - the x87 multiply memory operand
  private static int CountFmulMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0x08 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongOpWithDirectCellOperand_WhenPb36_ThenLoadsRightWithoutStaging() {
    // a LONG op (AND/OR/XOR) against a BYVAL direct-cell right operand loads it into BX:CX
    // straight from memory, skipping the push/pop staging of the left (MOV BX,AX); a BYREF
    // operand is not a direct cell and keeps staging. Two call sites defeat IPCP folding.
    const string mem = "DECLARE SUB s(BYVAL a AS LONG, BYVAL b AS LONG)\ns 7, 3\ns 100, 200\nEND\nSUB s(BYVAL a AS LONG, BYVAL b AS LONG)\n  r& = a AND b\n  r& = r OR b\n  r& = r XOR b\n  PRINT r&\nEND SUB";
    const string staged = "DECLARE SUB s(BYVAL a AS LONG, b AS LONG)\nDIM q AS LONG\nq = 3\ns 7, q\nq = 200\ns 100, q\nEND\nSUB s(BYVAL a AS LONG, b AS LONG)\n  r& = a AND b\n  r& = r OR b\n  r& = r XOR b\n  PRINT r&\nEND SUB";
    Assert.That(CountMovBxAx(Compile(mem, Dialect.Pb36)), Is.LessThan(CountMovBxAx(Compile(staged, Dialect.Pb36))),
      "a LONG direct-cell right operand loads into BX:CX from memory; a BYREF operand stages through MOV BX,AX");
  }

  [Test]
  public void Emit_GivenCompareConstant_WhenPb36_ThenFoldsToImmediate() {
    // y% = (x% = 5) compares against an immediate, no constant register load
    var pb36 = Compile(_TOUCH + "x% = 100\nT x%\ny% = (x% = 5)\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(CountMovBxAx(pb36), Is.Zero, "comparison against a constant should fold to CMP AX,imm");
  }

  // 8B /1 with a memory mod field = MOV CX, [mem] - the int32 path loading the right
  // operand's high word straight from its cell (replaces the old MOV CX,DX staging)
  private static int CountMovCxMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8B && (image[i + 1] & 0x38) == 0x08 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongBitwiseConstant_WhenPb36_ThenFoldsToImmediatePairNoRegisterLoad() {
    // b& = a& AND 255 folds into AND AX,imm / AND DX,imm; the variable form must
    // load the high word into CX
    var constMask = Compile(_TOUCHL + "a& = &H1234\nTL a&\nb& = a& AND 255\nTL b&\nEND" + _TOUCHL_END, Dialect.Pb36);
    var varMask = Compile(_TOUCHL + "a& = &H1234\nm& = 255\nTL a&\nTL m&\nb& = a& AND m&\nTL b&\nEND" + _TOUCHL_END, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovCxMem(constMask), Is.Zero, "a& AND 255 should fold to immediate pair ops, no high-word load into CX");
      Assert.That(CountMovCxMem(varMask), Is.GreaterThanOrEqualTo(1), "a& AND m& loads the second operand's high word into CX straight from memory (MOV CX,[m&+2])");
    });
  }

  [Test]
  public void Emit_GivenLongEqualsConstant_WhenPb36_ThenFoldsWithoutRegisterLoad() {
    // y% = (p& = 123456) subtracts the constant halves in place; the variable
    // form must load the comparand into CX
    var constEq = Compile(_TOUCHL + _TOUCH + "p& = 7\nTL p&\ny% = (p& = 123456)\nT y%\nEND" + _TOUCHL_END + _TOUCH_END, Dialect.Pb36);
    var varEq = Compile(_TOUCHL + _TOUCH + "p& = 7\nq& = 123456\nTL p&\nTL q&\ny% = (p& = q&)\nT y%\nEND" + _TOUCHL_END + _TOUCH_END, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovCxMem(constEq), Is.Zero, "p& = const should fold the comparand, no high-word load");
      Assert.That(CountMovCxMem(varEq), Is.GreaterThanOrEqualTo(1), "p& = q& loads the comparand's high word straight from memory (MOV CX,[q&+2])");
    });
  }

  [Test]
  public void Emit_GivenModularIncrementByOne_WhenPb36_ThenUsesIncNotAddImmediate() {
    // y% = x% + 1 folds to INC AX (one byte); y% = x% + 5 needs ADD AX,imm (three)
    // x% is opaque (BYREF call) so SCCP cannot fold the whole expression away
    var inc = Compile(_TOUCH + "x% = 100\nT x%\ny% = x% + 1\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    var add = Compile(_TOUCH + "x% = 100\nT x%\ny% = x% + 5\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(inc.Length, Is.LessThan(add.Length), "+1 should be INC AX, smaller than ADD AX,imm");
  }

  [Test]
  public void Emit_GivenCompareAgainstZero_WhenPb36_ThenUsesOrIdiomNotCmpImmediate() {
    var pb36 = Compile(_TOUCH + "x% = 7\nT x%\ny% = (x% = 0)\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
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

  #region O6b - induction-variable array store ($OPTIMIZE SPEED)

  /// <summary>ADD BX,2 - the induction-variable pointer step over 2-byte elements.</summary>
  private static int CountPointerStepByTwo(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC3 && image[i + 2] == 0x02)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenArrayStoreForLoop_WhenPb36Speed_ThenFewerElementSizeMultiplies() {
    // FOR i%=0 TO 9: a%(i%)=i%: NEXT - the normal loop body emits IMUL AX,AX,2
    // (element size) per iteration to compute the array element address; O6b steps
    // a DS-relative pointer by 2 instead, replacing each IMUL+MOV BX,AX with a
    // PUSH/POP+ADD BX,2 sequence.  The PRINT after the loop still reads a%(3) the
    // normal way (one IMUL), so we compare speed-vs-plain at the count level:
    // speed should have exactly one (from PRINT), plain should have two (loop + PRINT).
    const string body = """
      DIM a%(0 TO 9)
      FOR i% = 0 TO 9
        a%(i%) = i%
      NEXT i%
      PRINT a%(3)
      END
      """;
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    // the pointer step itself is the signature of IVSR: ADD BX,2 walks the elements, and it
    // appears only when the per-iteration address computation has been replaced by stepping.
    // (Counting the subscript scale no longer discriminates - IVSR's one-time setup scales the
    // base address with the same instruction the per-iteration path used to.)
    Assert.That(CountPointerStepByTwo(speed), Is.GreaterThan(0), "O6b should step a pointer through the elements");
    Assert.That(CountPointerStepByTwo(plain), Is.Zero, "without $OPTIMIZE SPEED each element address is recomputed");
  }

  [Test]
  public void Execute_GivenArrayStoreForLoop_WhenPb36Speed_ThenCorrectValues() {
    // verify the stored values are byte-identical to the unoptimized path
    const string source = """
      $OPTIMIZE SPEED
      DIM a%(0 TO 4)
      FOR i% = 0 TO 4
        a%(i%) = i% * 10
      NEXT i%
      PRINT a%(0); a%(1); a%(2); a%(3); a%(4)
      END
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo(" 0  10  20  30  40\n"));
  }

  [Test]
  public void Execute_GivenArrayStoreForLoopNonZeroLbound_WhenPb36Speed_ThenCorrectValues() {
    // lbound != 0: the initial pointer must account for the bias
    const string source = """
      $OPTIMIZE SPEED
      DIM a%(3 TO 7)
      FOR i% = 3 TO 7
        a%(i%) = i% * 2
      NEXT i%
      PRINT a%(3); a%(5); a%(7)
      END
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    Assert.That(output, Is.EqualTo(" 6  10  14\n"));
  }

  [Test]
  public void Emit_GivenArrayStoreExprReadsArray_WhenPb36Speed_ThenSameAsPlain() {
    // expr reads a%(0) - O6b must decline (conservative aliasing: any a% reference
    // in expr causes fallback to the standard per-iteration address computation)
    const string body = """
      DIM a%(0 TO 9)
      a%(0) = 1
      FOR i% = 1 TO 9
        a%(i%) = a%(0) + i%
      NEXT i%
      PRINT a%(5)
      END
      """;
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    // when O6b declines, each element address is still computed per iteration - no pointer step
    Assert.That(CountPointerStepByTwo(speed), Is.Zero,
      "when expr references a%, O6b declines and no pointer stepping appears");
    Assert.That(CountElementScaleByTwo(speed), Is.GreaterThanOrEqualTo(CountElementScaleByTwo(plain)),
      "the per-iteration subscript scale is still there");
  }

  [Test]
  public void Emit_GivenArrayStoreWithBoundsCheck_WhenPb36Speed_ThenSameAsPlain() {
    // $ERROR BOUNDS ON suppresses O6b so per-element bounds checking keeps working
    const string body = """
      DIM a%(0 TO 9)
      FOR i% = 0 TO 9
        a%(i%) = i%
      NEXT i%
      PRINT a%(3)
      END
      """;
    var checked_ = Compile("$OPTIMIZE SPEED\n$ERROR BOUNDS ON\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountPointerStepByTwo(checked_), Is.Zero,
      "O6b must not fire under $ERROR BOUNDS ON - every element keeps its own checked address");
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
  public void Emit_GivenConstantDivide_WhenPb36Speed_ThenReciprocalMultiplyReplacesIdiv() {
    // x% \ 10 by a non-power-of-two constant becomes a verified MUL+shift; x% is
    // opaque (BYREF call) so SCCP cannot fold the whole division away
    var speed = Compile("$OPTIMIZE SPEED\n" + _TOUCH + "x% = 7\nT x%\ny% = x% \\ 10\nz% = x% MOD 10\nT y%\nT z%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(CountIdivBx(speed), Is.Zero, "x% \\ 10 under SPEED should be a reciprocal multiply, no IDIV BX");
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

  #region O6b - array element address induction-variable strength reduction ($OPTIMIZE SPEED)

  /// <summary>
  /// Per-iteration subscript scaling for a 2-byte element, in either encoding: the optimizer
  /// lowers the scale to SHL AX,1 (the 8086-safe, ~10x cheaper form of IMUL AX,AX,2 - which is an
  /// 80186 instruction), so a test that means "the scale is still computed each iteration" has to
  /// accept both. IVSR removes it entirely, whichever form it took.
  /// </summary>
  private static int CountElementScaleByTwo(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x6B && image[i + 1] == 0xC0 && image[i + 2] == 0x02)   // IMUL AX,AX,2
        ++count;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xD1 && image[i + 1] == 0xE0)                            // SHL AX,1
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenPb36Speed_ThenPerIterationImulRemoved() {
    // Without IVSR: x% = a%(i%) emits IMUL AX,AX,2 every iteration to scale the subscript.
    // With IVSR: the address is pre-computed and stepped by 2 each iteration - no IMUL inside the loop.
    // The array has elementSize=2 so the unoptimized subscript scale is exactly IMUL AX,AX,2.
    const string body = "DIM a%(1 TO 10)\nDIM x%\nFOR i% = 1 TO 10\n  x% = a%(i%)\nNEXT i%\nPRINT x%\nEND";
    var plain = Compile(body, Dialect.Pb36);
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    Assert.Multiple(() => {
      // the optimized loop must not contain the per-iteration element-scale IMUL
      Assert.That(CountElementScaleByTwo(speed), Is.Zero,
        "IVSR should eliminate the per-iteration subscript scale inside the x%=a%(i%) loop");
      // without $OPTIMIZE SPEED the scaling IMUL must be present
      Assert.That(CountElementScaleByTwo(plain), Is.GreaterThanOrEqualTo(1),
        "without $OPTIMIZE SPEED the subscript scale must still be computed per iteration");
    });
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenBoundsChecking_ThenNoIvsr() {
    // $ERROR BOUNDS ON must suppress the optimization: the bounds check that the
    // IMUL path raises for out-of-range subscripts must not be silently removed.
    const string body = "$ERROR BOUNDS ON\nDIM a%(1 TO 5)\nDIM x%\nFOR i% = 1 TO 5\n  x% = a%(i%)\nNEXT i%\nPRINT x%\nEND";
    var checked_ = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    // bounds-checked: every subscript must still go through the real address path with range checks
    // (the IMUL may be folded away but the bounds-check emitter must still be there - we just
    // confirm the optimization does NOT fire by checking that the image is not suspiciously tiny)
    Assert.That(CountElementScaleByTwo(checked_), Is.GreaterThanOrEqualTo(1),
      "$ERROR BOUNDS ON must keep the address recomputation path (with the range check), not step a blind pointer");
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenMultiStatementBody_ThenNoIvsr() {
    // A body with more than one statement does not qualify - the optimization must not fire.
    // Two-statement body: x% = a%(i%) followed by PRINT x%.
    const string body = "$OPTIMIZE SPEED\nDIM a%(1 TO 3)\nDIM x%\nFOR i% = 1 TO 3\n  x% = a%(i%)\n  PRINT x%\nNEXT i%\nEND";
    var image = Compile(body, Dialect.Pb36);
    Assert.That(CountElementScaleByTwo(image), Is.GreaterThanOrEqualTo(1),
      "two-statement body must not trigger IVSR; the address IMUL must still appear per-iteration");
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

  [Test]
  public void Execute_GivenForwardingTailCall_WhenPb36_ThenReturnsCorrectValue() {
    // GIVEN a SUB whose last action is CALL B with a DIFFERENT argument count
    // (na = 2 bytes, nb = 6 bytes) - WHEN compiled pb36 the call becomes a frame
    // teardown + jmp - THEN B runs with the right arguments and returns to main.
    const string source = """
      DECLARE SUB Forward(BYVAL n%)
      DECLARE SUB Land(BYVAL a%, BYVAL b%, BYVAL c%)
      Forward 7
      PRINT "ok"
      END
      SUB Forward(BYVAL n%)
        Land n%, n% * 2, n% + 100
      END SUB
      SUB Land(BYVAL a%, BYVAL b%, BYVAL c%)
        PRINT a%; b%; c%
      END SUB
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var out35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var out36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.Multiple(() => {
      Assert.That(out36, Is.EqualTo(" 7  14  107\nok\n"));
      Assert.That(out36, Is.EqualTo(out35), "tail-jumped output must equal the genuine call chain");
    });
  }

  [Test]
  public void Execute_GivenDeepMutualTailRecursion_WhenPb36_ThenConstantStack() {
    // GIVEN two SUBs that tail-call each other 120000 times - WHEN pb36 turns each
    // tail call into a frame-reusing jump - THEN the chain runs in constant stack
    // (a real two-frame-per-bounce chain would blow the default 2 KiB DOS stack).
    var unit = Parser.Parse(Lexer.Tokenize("""
      DECLARE SUB Ping(BYVAL n&)
      DECLARE SUB Pong(BYVAL n&)
      Ping 120000
      PRINT "DONE"
      END
      SUB Ping(BYVAL n&)
        IF n& > 0 THEN Pong n& - 1
      END SUB
      SUB Pong(BYVAL n&)
        IF n& > 0 THEN Ping n& - 1
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
  public void Execute_GivenWorkAfterCall_WhenPb36_ThenNotConvertedAndRuns() {
    // GIVEN a call that is NOT in tail position (a PRINT runs after it returns) -
    // WHEN compiled pb36 - THEN it stays an ordinary CALL and the trailing
    // statement still executes (a wrong conversion would lose the "after" line).
    const string source = """
      DECLARE SUB AfterWork(BYVAL n%)
      DECLARE SUB Note(BYVAL n%)
      AfterWork 3
      END
      SUB AfterWork(BYVAL n%)
        Note n%
        PRINT "after"; n%
      END SUB
      SUB Note(BYVAL n%)
        PRINT "note"; n%
      END SUB
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var out35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var out36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.Multiple(() => {
      Assert.That(out36, Is.EqualTo("note 3\nafter 3\n"));
      Assert.That(out36, Is.EqualTo(out35));
    });
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

  [Test]
  public void Emit_GivenMultiStatementLeaf_WhenPb36_ThenInlinesAndMatches() {
    // GIVEN a small multi-statement leaf FUNCTION (a temp local, then the result)
    // WHEN compiled pb35 (real call) and pb36 (inlined)
    // THEN the DOSBox output is identical and the inlined image is smaller
    const string source = """
      DECLARE FUNCTION Poly%(BYVAL x%)
      a% = 4
      PRINT Poly%(a%); Poly%(2); Poly%(a% + 3)
      END
      FUNCTION Poly%(BYVAL x%)
        LOCAL t%
        t% = x% * x%
        Poly% = t% + x% + 1
      END FUNCTION
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var out35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var out36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    Assert.Multiple(() => {
      // Poly(4)=16+4+1=21, Poly(2)=4+2+1=7, Poly(7)=49+7+1=57
      Assert.That(out36, Is.EqualTo(" 21  7  57\n"));
      Assert.That(out36, Is.EqualTo(out35));
      Assert.That(pb36.Length, Is.LessThan(pb35.Length), "the inlined image sheds the call frame");
    });
  }

  [Test]
  public void Emit_GivenEveryCallInlines_WhenPb36_ThenProcedurePurged() {
    // GIVEN a multi-statement leaf whose every call inlines
    // WHEN compiled, vs a twin that takes its address (CODEPTR forces a real body)
    // THEN the all-inlined image is smaller - the procedure body is gone
    const string inlinedAll = """
      DECLARE FUNCTION Poly%(BYVAL x%)
      PRINT Poly%(3); Poly%(5)
      END
      FUNCTION Poly%(BYVAL x%)
        LOCAL t%
        t% = x% * x%
        Poly% = t% + x%
      END FUNCTION
      """;
    const string addressTaken = """
      DECLARE FUNCTION Poly%(BYVAL x%)
      DIM p AS LONG
      p = CODEPTR(Poly%)
      PRINT Poly%(3); Poly%(5); p
      END
      FUNCTION Poly%(BYVAL x%)
        LOCAL t%
        t% = x% * x%
        Poly% = t% + x%
      END FUNCTION
      """;
    var inlined = Compile(inlinedAll, Dialect.Pb36);
    var kept = Compile(addressTaken, Dialect.Pb36);
    // the address-taken twin must emit the real procedure body; the all-inlined one
    // purges it, so the inlined image is the smaller of the two
    Assert.That(inlined.Length, Is.LessThan(kept.Length), "fully-inlined procedure should be purged from the image");
  }

  [Test]
  public void Emit_GivenTrivialTypeMethod_WhenPb36_ThenInlinedThroughByRefReceiverAndPurged() {
    // GIVEN a trivial TYPE method (its body reads/writes fields through the BYREF THIS receiver)
    // WHEN every call inlines, vs a twin where CODEPTR forces all bodies to survive
    // THEN the all-inlined image is smaller - the lifted method procedure is gone
    const string inlinedAll = """
      TYPE Vec
        x AS LONG
        y AS LONG
        FUNCTION Sum() AS LONG
          Sum = THIS.x + THIS.y
        END FUNCTION
      END TYPE
      DIM v AS Vec
      v.x = 3 : v.y = 4
      PRINT v.Sum(); v.Sum()
      END
      """;
    const string addressTaken = """
      DECLARE FUNCTION Keep%(BYVAL n%)
      TYPE Vec
        x AS LONG
        y AS LONG
        FUNCTION Sum() AS LONG
          Sum = THIS.x + THIS.y
        END FUNCTION
      END TYPE
      DIM v AS Vec
      DIM p AS LONG
      v.x = 3 : v.y = 4
      p = CODEPTR(Keep%)
      PRINT v.Sum(); v.Sum(); p
      END
      FUNCTION Keep%(BYVAL n%)
        Keep% = n%
      END FUNCTION
      """;
    var inlined = Compile(inlinedAll, Dialect.Pb36);
    var kept = Compile(addressTaken, Dialect.Pb36);
    Assert.That(inlined.Length, Is.LessThan(kept.Length), "a trivial method inlines through its BYREF receiver and is purged");
  }

  [Test]
  public void Emit_GivenTwoCallSitesAndSelfMutatingParam_WhenInlined_ThenNoCollision() {
    // GIVEN a leaf that mutates its own BYVAL parameter and a body local
    // WHEN inlined at two call sites in one expression
    // THEN each inlining uses its own temps - the two results do not collide
    const string source = """
      DECLARE FUNCTION Step%(BYVAL n%)
      PRINT Step%(10) + Step%(100)
      END
      FUNCTION Step%(BYVAL n%)
        LOCAL acc%
        acc% = n%
        n% = n% + 1
        acc% = acc% + n%
        Step% = acc%
      END FUNCTION
      """;
    var output = DosBoxRunner.Normalize(DosBoxRunner.Run(Compile(source, Dialect.Pb36)));
    // Step(10)=10+11=21, Step(100)=100+101=201, sum 222 - no shared frame slots
    Assert.That(output, Is.EqualTo(" 222\n"));
  }

  [Test]
  public void Emit_GivenIneligibleCallees_WhenPb36_ThenRealCallKept() {
    // GIVEN callees that disqualify inlining (a nested call, a loop, an ON ERROR)
    // WHEN every one is invoked
    // THEN the program still runs correctly via real calls (the procedures survive)
    const string source = """
      DECLARE FUNCTION Leaf%(BYVAL x%)
      DECLARE FUNCTION Caller%(BYVAL x%)
      DECLARE FUNCTION Loopy%(BYVAL x%)
      DECLARE FUNCTION Guarded%(BYVAL x%)
      PRINT Caller%(3); Loopy%(4); Guarded%(5)
      END
      FUNCTION Leaf%(BYVAL x%)
        Leaf% = x% + 1
      END FUNCTION
      FUNCTION Caller%(BYVAL x%)
        Caller% = Leaf%(x%) * 2
      END FUNCTION
      FUNCTION Loopy%(BYVAL x%)
        LOCAL s%, i%
        FOR i% = 1 TO x%
          s% = s% + i%
        NEXT i%
        Loopy% = s%
      END FUNCTION
      FUNCTION Guarded%(BYVAL x%)
        ON ERROR GOTO 0
        Guarded% = x% * 10
      END FUNCTION
      """;
    var pb35 = Compile(source, Dialect.Pb35);
    var pb36 = Compile(source, Dialect.Pb36);
    var out35 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb35));
    var out36 = DosBoxRunner.Normalize(DosBoxRunner.Run(pb36));
    // Caller(3) = (3+1)*2 = 8 ; Loopy(4) = 1+2+3+4 = 10 ; Guarded(5) = 50
    Assert.Multiple(() => {
      Assert.That(out36, Is.EqualTo(" 8  10  50\n"));
      Assert.That(out36, Is.EqualTo(out35), "ineligible callees stay byte-correct via the real call");
    });
  }

  #endregion

  #region O3 - common subexpression elimination

  private static (int slots, System.Collections.Generic.Dictionary<PowerBasic.Compiler.Syntax.Ast.Expression, PowerBasic.Compiler.CodeGen.OptCommonSubexpr.CseMark> marks) AnalyzeCse(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty);
    var r = PowerBasic.Compiler.CodeGen.OptCommonSubexpr.Analyze(model.MainBody, model);
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
    var ipcp = PowerBasic.Compiler.CodeGen.OptIpcp.Analyze(model);
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
    var ipcp = PowerBasic.Compiler.CodeGen.OptIpcp.Analyze(model);
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

  #region LICM - loop-invariant code motion ($OPTIMIZE SPEED)

  /// <summary>
  /// Parses and binds a source snippet, extracts the first FOR loop from the main
  /// body, and runs AnalyzeLicm on its body with the given parameters.
  /// </summary>
  private static (int slots, int preheaderCount, int useMarks) RunLicmAnalysis(string source, bool checkedArithmetic = false) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    // find the first FOR loop
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.ForStmt>().FirstOrDefault();
    Assert.That(loop, Is.Not.Null, "source must contain a FOR loop");
    var name = (PowerBasic.Compiler.Syntax.Ast.NameExpr)loop!.Variable;
    var counter = model.VariableBindings[name];
    var r = PowerBasic.Compiler.CodeGen.OptCommonSubexpr.AnalyzeLicm(loop.Body, counter, 0, checkedArithmetic, model);
    return (r.SlotCount, r.Preheader.Count, r.Marks.Values.Count(m => !m.IsDefine));
  }

  [Test]
  public void Licm_GivenBodyWithIfBlock_WhenAnalyzed_ThenUnconditionalInvariantsStillHoist() {
    // an IF in the body previously disabled LICM wholesale; the invariant k%*m% in the
    // UNCONDITIONAL statement and in the IF condition must still hoist, while the write
    // to a% inside the branch is honored by the write-set
    const string source = """
      k% = 7
      m% = 13
      FOR i% = 1 TO 10
        b% = k% * m% + i%
        IF k% * m% > i% THEN
          a% = a% + 1
        END IF
      NEXT i%
      END
      """;
    var (slots, preheader, uses) = RunLicmAnalysis(source);
    Assert.Multiple(() => {
      Assert.That(slots, Is.EqualTo(1), "k%*m% is invariant and unconditionally evaluated (statement + condition)");
      Assert.That(preheader, Is.EqualTo(1));
      Assert.That(uses, Is.EqualTo(1), "the IF-condition occurrence reloads the slot");
    });
  }

  [Test]
  public void Licm_GivenInvariantOnlyInsideBranch_WhenAnalyzed_ThenNotHoisted() {
    // a value computed ONLY under the IF must not run unconditionally in the preheader
    const string source = """
      k% = 7
      m% = 13
      FOR i% = 1 TO 10
        IF i% > 5 THEN
          a% = k% * m% + a%
        END IF
      NEXT i%
      END
      """;
    var (slots, _, _) = RunLicmAnalysis(source);
    Assert.That(slots, Is.EqualTo(0), "branch-only expressions stay conditional");
  }

  [Test]
  public void Licm_GivenBranchWritingOperand_WhenAnalyzed_ThenInvariantKilled() {
    // k% is written inside the branch - k%*m% is NOT invariant even though the
    // unconditional statement uses it
    const string source = """
      k% = 7
      m% = 13
      FOR i% = 1 TO 10
        b% = k% * m% + i%
        IF i% > 5 THEN
          k% = k% + 1
        END IF
      NEXT i%
      END
      """;
    var (slots, _, _) = RunLicmAnalysis(source);
    Assert.That(slots, Is.EqualTo(0), "a conditional write to an operand kills the invariant");
  }

  [Test]
  public void Licm_GivenInvariantMultiply_WhenAnalyzed_ThenOneSlotWithOnePreheaderAndOneUse() {
    // k%*m% appears twice in the body; both k% and m% are not written in the body.
    // Expected: 1 LICM slot, 1 preheader DEFINE (first occurrence), 1 USE (second).
    // Use plain scalar targets (not array) to avoid array-CSE path interference.
    const string source = """
      k% = 7
      m% = 13
      FOR i% = 1 TO 10
        a% = k% * m% + i%
        b% = k% * m% - i%
      NEXT i%
      END
      """;
    var (slots, preheader, uses) = RunLicmAnalysis(source);
    Assert.Multiple(() => {
      Assert.That(slots, Is.EqualTo(1), "one invariant subexpression (k%*m%) should get one slot");
      Assert.That(preheader, Is.EqualTo(1), "one preheader DEFINE (first body occurrence)");
      Assert.That(uses, Is.EqualTo(1), "one USE mark (second body occurrence reloads the slot)");
    });
  }

  [Test]
  public void Licm_GivenVariantInput_WhenAnalyzed_ThenNoSlots() {
    // k% IS written in the loop body (k% = k% + 1), so k%*m% is NOT invariant.
    // AnalyzeLicm must find zero hoistable expressions.
    const string source = """
      k% = 7
      m% = 13
      DIM a%(1 TO 10)
      FOR i% = 1 TO 10
        k% = k% + 1
        a%(i%) = k% * m% + i%
      NEXT i%
      END
      """;
    var (slots, _, _) = RunLicmAnalysis(source);
    Assert.That(slots, Is.EqualTo(0), "k% is written in the body: k%*m% is NOT invariant, no LICM slot");
  }

  [Test]
  public void Licm_GivenCounterInExpression_WhenAnalyzed_ThenNoSlots() {
    // k%*i% reads the loop counter i%; the counter is always in the written set.
    // The expression is NOT invariant and must not be hoisted.
    const string source = """
      k% = 7
      FOR i% = 1 TO 10
        a% = k% * i%
      NEXT i%
      END
      """;
    var (slots, _, _) = RunLicmAnalysis(source);
    Assert.That(slots, Is.EqualTo(0), "k%*i% reads the loop counter: NOT invariant, no LICM slot");
  }

  [Test]
  public void Licm_GivenCheckedArithmetic_WhenAnalyzed_ThenNoSlots() {
    // under checked arithmetic ($ERROR NUMERIC ON) a multiply could trap;
    // AnalyzeLicm must suppress LICM entirely (checkedArithmetic=true).
    const string source = """
      k% = 7
      m% = 13
      FOR i% = 1 TO 10
        a% = k% * m% + i%
        b% = k% * m% - i%
      NEXT i%
      END
      """;
    var (slots, _, _) = RunLicmAnalysis(source, checkedArithmetic: true);
    Assert.That(slots, Is.EqualTo(0), "checkedArithmetic=true: LICM must be suppressed entirely");
  }

  [Test]
  public void Emit_GivenSpeedOptimized_WhenInvariantMultiplyInLoop_ThenImageDiffersFromGeneric() {
    // With $OPTIMIZE SPEED, LICM hoists k%*m% to the preheader; without SPEED it
    // stays in the body. The emitted images must differ (code is in a different place).
    const string body = """
      k% = 7
      m% = 13
      DIM a%(1 TO 10)
      FOR i% = 1 TO 10
        a%(i%) = k% * m% + i%
      NEXT i%
      PRINT a%(1); a%(10)
      END
      """;
    var generic = Compile(body, Dialect.Pb36);
    var speed   = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    Assert.That(speed, Is.Not.EqualTo(generic),
      "$OPTIMIZE SPEED with a loop-invariant multiply should produce a different image " +
      "(LICM moves the computation to the preheader, changing code layout)");
  }

  #endregion
}
