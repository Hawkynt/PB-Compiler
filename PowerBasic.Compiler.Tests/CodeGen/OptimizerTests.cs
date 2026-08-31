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

  private static byte[] CompileWithBackend(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = true };
    var exe = generator.EmitExecutable();
    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the shape test must exercise routed code");
    });
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

  [TestCase(false)]
  [TestCase(true)]
  public void Emit_GivenOptimizeOff_WhenComparedWithDisabledOptimizer_ThenImagesMatch(bool useBackend) {
    const string body = "DIM total AS INTEGER\nFOR i% = 1 TO 4\n  total = total + i%\nNEXT i%\nPRINT total\nEND";

    static byte[] CompileCase(string source, bool useBackend, bool? optimize, out IReadOnlyList<string> routed) {
      var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model) { UseExperimentalBackend = useBackend };
      if (optimize is { } enabled)
        generator.Optimize = enabled;
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      routed = generator.BackendRoutedNames.ToList();
      return image;
    }

    var fromDirective = CompileCase("$OPTIMIZE OFF\n" + body, useBackend, null, out var directiveRoutes);
    var fromProperty = CompileCase(body, useBackend, false, out var propertyRoutes);

    Assert.That(fromDirective, Is.EqualTo(fromProperty), "$OPTIMIZE OFF must disable every optimization stage");
    if (useBackend)
      Assert.That(directiveRoutes, Is.EquivalentTo(propertyRoutes), "the directive must not change backend eligibility");
  }

  [Test]
  public void Execute_GivenOptimizeSizeAndCallInLoop_WhenRun_ThenBodySurvivesAndMatchesDefault() {
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
    var model = BindModel("LOCATE , 5\nLOCATE 1, 1\nPRINT \"x\"\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(1));
    var model2 = BindModel("LOCATE 1, 1\nLOCATE , 5\nPRINT \"x\"\nEND");
    OptPruner.Prune(model2);
    Assert.That(model2.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(2));
  }

  [Test]
  public void Prune_GivenClsChains_WhenPruned_ThenRedundantWorkFolds() {
    var model = BindModel("CLS\nCLS\nPRINT \"x\"\nEND");
    OptPruner.Prune(model);
    Assert.That(model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "CLS"), Is.EqualTo(1));
    var model2 = BindModel("LOCATE 5, 5\nCLS\nPRINT \"x\"\nEND");
    OptPruner.Prune(model2);
    Assert.That(model2.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.CommandStmt>().Count(c => c.Keyword == "LOCATE"), Is.EqualTo(0), "CLS homes the cursor - the unobserved LOCATE is dead");
  }

  [Test]
  public void Prune_GivenGotoChain_WhenPruned_ThenGotoThreadsToFinalLabel() {
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
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { UseExperimentalBackend = true };
    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"),
        "the REP MOVSD must remain present when the main body comes from the x86-16 backend");
      Assert.That(ContainsSeq(image, 0xF3, 0x66, 0xA5), Is.True,
        "$CPU 80386 + pb36 should emit REP MOVSD block copies");
    });
  }

  [Test]
  public void Emit_GivenLongShiftUnderCpu386_WhenPb36_ThenSingleDwordShift() {
    const string body = "$OPTIMIZE SPEED\nDECLARE FUNCTION Shifted&(BYVAL x&)\n"
      + "PRINT Shifted&(3); Shifted&(5)\nEND\n"
      + "FUNCTION Shifted&(BYVAL x&) NOINLINE\nSHIFT LEFT x&, 4\nShifted& = x&\nEND FUNCTION";
    var with386 = "$CPU 80386\n" + body;
    Assert.That(CountDwordShiftImm(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountDwordShiftImm(Compile(body, Dialect.Pb36))),
      "$CPU 80386 should add a 32-bit dword shift the per-bit-loop version lacks");
  }

  private static int CountDwordShiftImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0xC1)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongDivideByConstantUnderCpu386_WhenPb36_ThenHardwareIdiv() {
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL n AS LONG)\nd 100000007\nd 9\nEND\nSUB d(BYVAL n AS LONG) NOINLINE\nPRINT n \\ 7\nEND SUB";
    const string no386 = "$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL n AS LONG)\nd 100000007\nd 9\nEND\nSUB d(BYVAL n AS LONG) NOINLINE\nPRINT n \\ 7\nEND SUB";
    Assert.That(CountDwordF7(Compile(with386, Dialect.Pb36)),
      Is.GreaterThan(CountDwordF7(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should add a 32-bit IDIV the runtime-call version lacks");
  }

  private static int CountDwordF7(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0xF7)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongDivideRangeKnown_WhenPb36_ThenNarrowedTo16BitIdiv() {
    const string narrowed = "$OPTIMIZE SPEED\nFOR i& = 0 TO 100\nx& = i& \\ 3\nNEXT i&\nPRINT x&\nEND";
    const string runtime = "$OPTIMIZE SPEED\nINPUT j&\nx& = j& \\ 3\nPRINT x&\nEND";
    Assert.That(CountIdivBx(Compile(narrowed, Dialect.Pb36)),
      Is.GreaterThan(CountIdivBx(Compile(runtime, Dialect.Pb36))),
      "a range-known LONG divide should narrow to a 16-bit IDIV BX the runtime-call version lacks");
  }

  private static int CountCmpAxBx(byte[] image) => CountPair(image, 0x39, 0xD8);
  private static int CountSbbDxCx(byte[] image) => CountPair(image, 0x19, 0xCA);
  private static int CountMulBx(byte[] image) => CountPair(image, 0xF7, 0xE3);

  [Test]
  public void Fuse_GivenAdjacentSameBoundForLoops_ThenMergedUnlessScalarCarry() {
    static int Loops(SemanticModel m) => m.MainBody.Count(s => s is global::PowerBasic.Compiler.Syntax.Ast.ForStmt);
    static SemanticModel Bound(string src) =>
      Binder.Bind(Parser.Parse(Lexer.Tokenize(src, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var fusible = Bound("DIM i AS INTEGER\nDIM a(0 TO 9) AS INTEGER, b(0 TO 9) AS INTEGER\nFOR i = 0 TO 9\na(i) = i\nNEXT i\nFOR i = 0 TO 9\nb(i) = a(i) * 2\nNEXT i\nEND");
    var carry = Bound("DIM i AS INTEGER, s AS INTEGER\nDIM a(0 TO 9) AS INTEGER\nFOR i = 0 TO 9\ns = s + i\nNEXT i\nFOR i = 0 TO 9\na(i) = s + i\nNEXT i\nEND");
    OptLoopFusion.Fuse(fusible);
    OptLoopFusion.Fuse(carry);
    Assert.That(Loops(fusible), Is.EqualTo(1), "two same-index-dependent FOR loops fuse into one");
    Assert.That(Loops(carry), Is.EqualTo(2), "a scalar carry blocks fusion");
  }

  [Test]
  public void Emit_GivenAbsIntrinsic_WhenPb36_ThenBranchless() {
    static bool HasBranchlessAbs(byte[] img) {
      for (var i = 0; i < img.Length - 4; ++i)
        if (img[i] == 0x99 && img[i + 1] == 0x31 && img[i + 2] == 0xD0 && img[i + 3] == 0x29 && img[i + 4] == 0xD0)
          return true;
      return false;
    }
    var src = "DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\ny = VAL(z$)\nx = ABS(y)\nPRINT x\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(src, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var opt = new CodeGenerator(model).EmitExecutable();
    var noOpt = new CodeGenerator(model) { Optimize = false }.EmitExecutable();
    Assert.That(HasBranchlessAbs(opt), Is.True, "optimized ABS is branchless cwd/xor/sub");
    Assert.That(HasBranchlessAbs(noOpt), Is.False, "the faithful build keeps the test/JNS/NEG form");
    var ifForm = Compile("DIM a AS INTEGER\nLINE INPUT z$\na = VAL(z$)\nIF a < 0 THEN a = -a\nPRINT a\nEND", Dialect.Pb36);
    Assert.That(HasBranchlessAbs(ifForm), Is.True, "IF a<0 THEN a=-a is branchless abs too");
  }

  [Test]
  public void Emit_GivenCountOnlyFor_WhenPb36Speed_ThenCountsDownNoLimitCompare() {
    static int CmpSi(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if ((img[i] == 0x3B && ((img[i + 1] >> 3) & 7) == 6)
            || ((img[i] == 0x81 || img[i] == 0x83) && img[i + 1] == 0xFE))
          ++n;
      return n;
    }
    var countOnly = Compile("$OPTIMIZE SPEED\nDIM i AS INTEGER\nFOR i = 1 TO 1000\nPRINT \"x\"\nNEXT i\nEND", Dialect.Pb36);
    var readsI = Compile("$OPTIMIZE SPEED\nDIM i AS INTEGER\nFOR i = 1 TO 1000\nPRINT i\nNEXT i\nEND", Dialect.Pb36);
    Assert.That(CmpSi(countOnly), Is.Zero, "a count-only FOR counts down with DEC/JNZ, no limit compare");
    Assert.That(CmpSi(readsI), Is.GreaterThan(0), "a FOR that reads its counter keeps the compare");
  }

  [Test]
  public void Emit_GivenRegisterCounterFor_WhenPb36Speed_ThenRotatedTestedAtBothEnds() {
    static int CmpSi(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if ((img[i] == 0x3B && ((img[i + 1] >> 3) & 7) == 6)
            || ((img[i] == 0x81 || img[i] == 0x83) && img[i + 1] == 0xFE))
          ++n;
      return n;
    }
    var img = Compile("$OPTIMIZE SPEED\nDIM i AS INTEGER\nFOR i = 1 TO 1000\nPRINT i\nNEXT i\nEND", Dialect.Pb36);
    Assert.That(CmpSi(img), Is.EqualTo(2), "the rotated FOR tests its SI counter at the entry guard and at the bottom");
  }

  [Test]
  public void Emit_GivenPreTestedDoLoop_WhenPb36Speed_ThenRotatedConditionAtBothEnds() {
    static int CmpBound(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 3; ++i)
        if ((img[i] == 0x3D && img[i + 1] == 0xE8 && img[i + 2] == 0x03)
            || (img[i] == 0x81 && (img[i + 1] & 0xF8) == 0xF8
              && img[i + 2] == 0xE8 && img[i + 3] == 0x03))
          ++n;
      return n;
    }
    const string loop = "DIM i AS INTEGER\nLINE INPUT z$\ni = VAL(z$)\nDO WHILE i < 1000\nPRINT i\ni = i + 1\nLOOP\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + loop, Dialect.Pb36);
    var plain = Compile(loop, Dialect.Pb36);
    Assert.That(CmpBound(speed), Is.GreaterThan(CmpBound(plain)), "the rotated loop compares its bound at entry and at the bottom");
  }

  [Test]
  public void Emit_GivenCoveredArrayFill_WhenPb36_ThenAllocatesWithoutZeroFill() {
    static bool HasNoZeroAlloc(byte[] img) {
      for (var i = 0; i < img.Length - 3; ++i)
        if (img[i] == 0x89 && img[i + 1] == 0xD8 && img[i + 2] == 0x5B && img[i + 3] == 0xC3)
          return true;
      return false;
    }
    var covered = Compile("DIM n AS INTEGER, i AS INTEGER\nn = 6\nDIM a(1 TO n) AS INTEGER\nFOR i = 1 TO n\na(i) = i\nNEXT i\nPRINT a(1)\nEND", Dialect.Pb36);
    var reads = Compile("DIM n AS INTEGER, i AS INTEGER\nn = 6\nDIM a(1 TO n) AS INTEGER\nFOR i = 1 TO n\na(i) = a(1)\nNEXT i\nPRINT a(1)\nEND", Dialect.Pb36);
    var multi = Compile("DIM n AS INTEGER, i AS INTEGER, t AS LONG\nn = 6\nt = 0\nDIM a(1 TO n) AS INTEGER\nFOR i = 1 TO n\na(i) = i\nt = t + i\nNEXT i\nPRINT t; a(1)\nEND", Dialect.Pb36);
    Assert.That(HasNoZeroAlloc(covered), Is.True, "the covered fill allocates without the zero-fill");
    Assert.That(HasNoZeroAlloc(reads), Is.False, "a fill that reads the array keeps the zero-filling allocation");
    Assert.That(HasNoZeroAlloc(multi), Is.True, "a fill with array-free scalar work alongside the write still qualifies");
  }

  [Test]
  public void Emit_GivenUnrolledCounterMultiply_WhenPb36Speed_ThenFoldedNoImul() {
    static int Imuls(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if (img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)
          ++n;
      return n;
    }
    var img = Compile("$OPTIMIZE SPEED\nDIM i AS INTEGER, s AS INTEGER\ns = 0\nFOR i = 1 TO 4\ns = s + i * i\nNEXT i\nPRINT s\nEND", Dialect.Pb36);
    Assert.That(Imuls(img), Is.Zero, "the unrolled counter folds, so i * i becomes a constant per copy");
  }

  [Test]
  public void Emit_GivenThreeBitMultiplier_WhenPb36Speed_ThenDecomposedNoImul() {
    static int Imuls(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if (img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)
          ++n;
      return n;
    }
    var three = Compile("$OPTIMIZE SPEED\nDIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 11\nPRINT y\nEND", Dialect.Pb36);
    var keep = Compile("$OPTIMIZE SPEED\nDIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 341\nPRINT y\nEND", Dialect.Pb36);
    Assert.That(Imuls(three), Is.LessThan(Imuls(keep)), "x * 11 decomposes to shifts/adds; x * 341 (five bits) keeps IMUL");
  }

  [Test]
  public void Emit_GivenFourBitMultiplier_WhenPb36Speed_ThenCostModelDecomposesOn8086ButKeepsImulOn386() {
    static int Imuls(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if (img[i] == 0xF7 && img[i + 1] is >= 0xE8 and <= 0xEF)
          ++n;
      return n;
    }
    var i8086 = Compile("$OPTIMIZE SPEED\nDIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 23\nPRINT y\nEND", Dialect.Pb36);
    var i386 = Compile("$CPU 80386\n$OPTIMIZE SPEED\nDIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 23\nPRINT y\nEND", Dialect.Pb36);
    Assert.That(Imuls(i8086), Is.LessThan(Imuls(i386)), "8086 decomposes the slow MUL; 386+ keeps the compact IMUL");
  }

  [Test]
  public void Emit_GivenMaxDiamond_WhenPb36_ThenFoldsToTheSameCodeAsTheMaxIntrinsic() {
    const string head = "$OPTIMIZE SPEED\nDIM a AS INTEGER, b AS INTEGER, m AS INTEGER\nLINE INPUT z$\na = VAL(z$)\nb = 7\n";
    var diamond = Compile(head + "IF a > b THEN m = a ELSE m = b\nPRINT m\nEND", Dialect.Pb36);
    var intrinsic = Compile(head + "m = MAX%(a, b)\nPRINT m\nEND", Dialect.Pb36);
    Assert.That(diamond, Is.EqualTo(intrinsic), "the max diamond lowers to the MAX% integer fold");
  }

  [Test]
  public void Emit_GivenBitTestCondition_WhenPb36_ThenTestNotAndPlusTest() {
    static bool Has(byte[] img, params byte[] seq) {
      for (var i = 0; i <= img.Length - seq.Length; ++i) {
        var ok = true;
        for (var j = 0; j < seq.Length; ++j) if (img[i + j] != seq[j]) { ok = false; break; }
        if (ok) return true;
      }
      return false;
    }
    var img = Compile("$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL x%)\nS 5\nS 6\nEND\nSUB S(BYVAL x%) NOINLINE\nIF x% AND 4 THEN PRINT \"y\"\nEND SUB", Dialect.Pb36);
    Assert.That(Has(img, 0xA9, 0x04, 0x00), Is.True, "test ax, 4 - the bit test");
    Assert.That(Has(img, 0x83, 0xE0, 0x04), Is.False, "no and ax, 4 - the mask is not materialized");
  }

  [Test]
  public void Emit_GivenBitTestComparedAgainstZero_WhenPb36_ThenSameTestOnlyPolarityDiffers() {
    static bool Has(byte[] img, params byte[] seq) {
      for (var i = 0; i <= img.Length - seq.Length; ++i) {
        var ok = true;
        for (var j = 0; j < seq.Length; ++j) if (img[i + j] != seq[j]) { ok = false; break; }
        if (ok) return true;
      }
      return false;
    }
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL x%)\nS 5\nS 6\nEND\nSUB S(BYVAL x%) NOINLINE\n";
    var eq = Compile(head + "IF (x% AND 4) = 0 THEN PRINT \"y\" ELSE PRINT \"n\"\nEND SUB", Dialect.Pb36);
    var ne = Compile(head + "IF (x% AND 4) <> 0 THEN PRINT \"y\" ELSE PRINT \"n\"\nEND SUB", Dialect.Pb36);
    Assert.That(Has(eq, 0xA9, 0x04, 0x00), Is.True, "= 0: test ax, 4");
    Assert.That(Has(eq, 0x83, 0xE0, 0x04), Is.False, "= 0: no and ax, 4");
    Assert.That(Has(ne, 0xA9, 0x04, 0x00), Is.True, "<> 0: test ax, 4");
    Assert.That(Has(ne, 0x83, 0xE0, 0x04), Is.False, "<> 0: no and ax, 4");
  }

  [Test]
  public void Emit_GivenBitTestValueAlsoUsed_WhenPb36_ThenMaskIsMaterialized() {
    static bool Has(byte[] img, params byte[] seq) {
      for (var i = 0; i <= img.Length - seq.Length; ++i) {
        var ok = true;
        for (var j = 0; j < seq.Length; ++j) if (img[i + j] != seq[j]) { ok = false; break; }
        if (ok) return true;
      }
      return false;
    }
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL x%)\nS 5\nS 6\nEND\nSUB S(BYVAL x%) NOINLINE\n";
    var img = Compile(head + "IF x% AND 4 THEN PRINT \"y\"\nIF (x% AND 4) = 0 THEN PRINT \"n\"\nEND SUB", Dialect.Pb36);
    Assert.That(Has(img, 0x83, 0xE0, 0x04), Is.True, "the shared `x AND 4` is materialized for its CSE slot");
  }

  [Test]
  public void Emit_GivenOnGoto_WhenPb36_ThenJumpTableForFourTargetsChainForThree() {
    static bool Has(byte[] img, params byte[] seq) {
      for (var i = 0; i <= img.Length - seq.Length; ++i) {
        var ok = true;
        for (var j = 0; j < seq.Length; ++j) if (img[i + j] != seq[j]) { ok = false; break; }
        if (ok) return true;
      }
      return false;
    }
    static bool HasIndexedJump(byte[] img) {
      for (var i = 0; i + 1 < img.Length; ++i)
        if (img[i] == 0xFF && (img[i + 1] & 0x38) == 0x20 && (img[i + 1] & 0xC0) != 0xC0)
          return true;
      return false;
    }
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL n%)\nS 1\nS 3\nEND\nSUB S(BYVAL n%) NOINLINE\n";
    var four = Compile(head + "ON n% GOTO a, b, c, d\nEXIT SUB\na: EXIT SUB\nb: EXIT SUB\nc: EXIT SUB\nd: EXIT SUB\nEND SUB", Dialect.Pb36);
    var three = Compile(head + "ON n% GOTO a, b, c\nEXIT SUB\na: EXIT SUB\nb: EXIT SUB\nc: EXIT SUB\nEND SUB", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(Has(four, 0x83, 0xF8, 0x04), Is.True, "four targets: cmp ax,4 bounds check of the jump table");
      Assert.That(HasIndexedJump(four), Is.True, "four targets: the table is dispatched through an indexed indirect jump");
      Assert.That(HasIndexedJump(three), Is.False, "three targets: a compare chain, so there is no table to jump through");
    });
  }

  [Test]
  public void Emit_GivenLenEqualsZero_WhenPb36_ThenSameHandleTestAsEmptyCompare() {
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL n%)\nS 1\nEND\nSUB S(BYVAL n%) NOINLINE\nDIM s$\ns$ = MID$(\"hi\", 1, n%)\n";
    var lenForm = Compile(head + "IF LEN(s$) = 0 THEN PRINT \"a\" ELSE PRINT \"b\"\nEND SUB", Dialect.Pb36);
    var eqForm = Compile(head + "IF s$ = \"\" THEN PRINT \"a\" ELSE PRINT \"b\"\nEND SUB", Dialect.Pb36);
    Assert.That(lenForm, Is.EqualTo(eqForm), "LEN(s$) = 0 lowers to the same handle test as s$ = \"\"");
  }

  [Test]
  public void Emit_GivenScalarSwap_WhenPb36_ThenInlineXchgNotRuntimeCall() {
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL a%, BYVAL b%)\nS 1, 2\nS 7, 9\nEND\n";
    var scalar = RuntimeSurface(head + "SUB S(BYVAL a%, BYVAL b%) NOINLINE\nSWAP a, b\nPRINT a; b\nEND SUB");
    var record = RuntimeSurface("TYPE P\n  a AS INTEGER\n  b AS INTEGER\n  c AS INTEGER\n  d AS INTEGER\nEND TYPE\n"
      + head + "SUB S(BYVAL a%, BYVAL b%) NOINLINE\nDIM u AS P, v AS P\nu.a = a% : v.a = b%\nSWAP u, v\nPRINT u.a; v.a\nEND SUB");
    Assert.Multiple(() => {
      Assert.That(scalar, Does.Not.Contain("rt_swap"), "a scalar SWAP is exchanged inline - the byte loop is not linked in");
      Assert.That(record, Does.Contain("rt_swap"), "a UDT SWAP does need the byte loop - otherwise the absence above proves nothing");
    });
  }

  [Test]
  public void Emit_GivenIntegerSgn_WhenPb36_ThenBranchlessCwdNegAdcNoFpu() {
    static bool Has(byte[] img, params byte[] seq) {
      for (var i = 0; i <= img.Length - seq.Length; ++i) {
        var ok = true;
        for (var j = 0; j < seq.Length; ++j) if (img[i + j] != seq[j]) { ok = false; break; }
        if (ok) return true;
      }
      return false;
    }
    var img = Compile("$OPTIMIZE SPEED\nDECLARE SUB S(BYVAL x%)\nS 3\nS -4\nEND\nSUB S(BYVAL x%) NOINLINE\nPRINT SGN(x%)\nEND SUB", Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(Has(img, 0x99, 0xF7, 0xD8), Is.True, "cwd; neg ax");
      Assert.That(Has(img, 0x11, 0xD2), Is.True, "adc dx,dx");
      Assert.That(Has(img, 0xD9, 0xE4), Is.False, "no FTST - the FPU path is gone");
    });
  }

  [Test]
  public void Emit_GivenClampIf_WhenPb36_ThenFoldsToTheMinOrMaxIntrinsic() {
    const string head = "$OPTIMIZE SPEED\nDIM x AS INTEGER, hi AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\nhi = 5\n";
    var clampHi = Compile(head + "IF x > hi THEN x = hi\nPRINT x\nEND", Dialect.Pb36);
    var minInt = Compile(head + "x = MIN%(x, hi)\nPRINT x\nEND", Dialect.Pb36);
    Assert.That(clampHi, Is.EqualTo(minInt), "IF x > hi THEN x = hi is MIN%(x, hi)");
    var clampLo = Compile(head + "IF x < hi THEN x = hi\nPRINT x\nEND", Dialect.Pb36);
    var maxInt = Compile(head + "x = MAX%(x, hi)\nPRINT x\nEND", Dialect.Pb36);
    Assert.That(clampLo, Is.EqualTo(maxInt), "IF x < hi THEN x = hi is MAX%(x, hi)");
  }

  [Test]
  public void Emit_GivenLongMinMax_WhenPb36_ThenFoldsWithA32BitCompareNotTheFpu() {
    static int JgJl(byte[] img) {
      var count = 0;
      for (var i = 0; i + 3 < img.Length; ++i)
        if (img[i] == 0x7F && img[i + 2] == 0x7C)
          ++count;
      return count;
    }
    static int FpuCompares(byte[] img) {
      var count = 0;
      for (var i = 0; i + 1 < img.Length; ++i)
        if ((img[i] is 0xD8 or 0xDC && (img[i + 1] & 0x38) is 0x10 or 0x18)
            || (img[i] == 0xDE && img[i + 1] == 0xD9) || (img[i] == 0xD9 && img[i + 1] == 0xE4))
          ++count;
      return count;
    }
    const string body = "\nLINE INPUT z$\na = VAL(z$)\nb = 5\nm = MAX(a, b)\nPRINT m\nEND";
    var longs = Compile("$OPTIMIZE SPEED\nDIM a AS LONG, b AS LONG, m AS LONG" + body, Dialect.Pb36);
    var doubles = Compile("$OPTIMIZE SPEED\nDIM a AS DOUBLE, b AS DOUBLE, m AS DOUBLE" + body, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(JgJl(longs), Is.Positive, "the high-word three-way test - the 32-bit fold's signature");
      Assert.That(JgJl(doubles), Is.Zero, "the DOUBLE form has no half-word compare to test three ways");
      Assert.That(FpuCompares(longs), Is.LessThan(FpuCompares(doubles)), "the LONG fold adds no x87 compare; the DOUBLE one is nothing but");
    });
  }

  [Test]
  public void Emit_GivenLongDiamond_WhenPb36_ThenFoldsToTheSameCodeAsTheLongMaxIntrinsic() {
    const string head = "$OPTIMIZE SPEED\nDIM a AS LONG, b AS LONG, m AS LONG\nLINE INPUT z$\na = VAL(z$)\nb = 5\n";
    var diamond = Compile(head + "IF a > b THEN m = a ELSE m = b\nPRINT m\nEND", Dialect.Pb36);
    var intrinsic = Compile(head + "m = MAX(a, b)\nPRINT m\nEND", Dialect.Pb36);
    Assert.That(diamond, Is.EqualTo(intrinsic), "the LONG max diamond lowers to the LONG MAX fold");
  }

  [Test]
  public void Emit_GivenDiamondWithSideEffectingOperand_WhenPb36_ThenKeepsTheBranch() {
    const string head = "$OPTIMIZE SPEED\nDECLARE FUNCTION F%(BYVAL x%)\nDIM a AS INTEGER, b AS INTEGER, m AS INTEGER\nLINE INPUT z$\na = VAL(z$)\nb = 7\n";
    const string tail = "\nPRINT m\nEND\nFUNCTION F%(BYVAL x%)\nF% = x% + 1\nEND FUNCTION";
    var callDiamond = Compile(head + "IF F%(a) > b THEN m = F%(a) ELSE m = b" + tail, Dialect.Pb36);
    var pureFold = Compile(head + "m = MAX%(a, b)" + tail, Dialect.Pb36);
    Assert.That(callDiamond, Is.Not.EqualTo(pureFold), "a call operand is not folded to the branchless integer keep");
  }

  [Test]
  public void Emit_GivenSixTripLoop_WhenPb36Speed_ThenCostModelUnrollsOn486ButLoopsOn8086() {
    const string body = "$OPTIMIZE SPEED\nDIM s AS INTEGER, i AS INTEGER\ns = 0\nFOR i = 1 TO 6\ns = s + i\nNEXT\nPRINT s\nEND";
    var i8086 = Compile(body, Dialect.Pb36);
    var i486 = Compile("$CPU 80486\n" + body, Dialect.Pb36);
    Assert.That(i486.Length, Is.GreaterThan(i8086.Length),
      "8086 keeps the compact loop; the 486's wider budget unrolls the six copies");
  }

  [Test]
  public void Emit_GivenAdjacentDivAndMod_WhenPb36_ThenSharedSingleIdiv() {
    const string src = "DIM n AS INTEGER, d AS INTEGER, q AS INTEGER, m AS INTEGER\nLINE INPUT a$\nn = VAL(a$)\nLINE INPUT b$\nd = VAL(b$)\nq = n \\ d\nm = n MOD d\nPRINT q; m\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(src, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var opt = new CodeGenerator(model).EmitExecutable();
    var noOpt = new CodeGenerator(model) { Optimize = false }.EmitExecutable();
    static int Idivs(byte[] img) {
      var n = 0;
      for (var i = 0; i < img.Length - 1; ++i)
        if (img[i] == 0xF7 && (img[i + 1] & 0x38) == 0x38)
          ++n;
      return n;
    }
    Assert.That(Idivs(opt), Is.LessThan(Idivs(noOpt)), "the shared divide emits one IDIV, not two");
  }

  [Test]
  public void Emit_GivenIntegerDivideByOne_WhenPb36_ThenFoldedNoIdiv() {
    var one = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x \\ 1\nPRINT y\nEND", Dialect.Pb36);
    var three = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x \\ 3\nPRINT y\nEND", Dialect.Pb36);
    Assert.That(one.Length, Is.LessThan(three.Length), "x \\ 1 folds to x; x \\ 3 keeps the IDIV");
  }

  [Test]
  public void Emit_GivenEqualityIfChain_WhenPb36_ThenSameJumpTableAsSelect() {
    const string body = "\n  r = 100\nELSEIF x = 11 THEN\n  r = 110\nELSEIF x = 12 THEN\n  r = 120\nELSEIF x = 13 THEN\n  r = 130\nELSEIF x = 14 THEN\n  r = 140\nELSE\n  r = 0\nEND IF\nPRINT r\nEND";
    var ifChain = Compile("DIM x AS INTEGER, r AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\nIF x = 10 THEN" + body, Dialect.Pb36);
    var select = Compile("DIM x AS INTEGER, r AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\nSELECT CASE x\nCASE 10\n r = 100\nCASE 11\n r = 110\nCASE 12\n r = 120\nCASE 13\n r = 130\nCASE 14\n r = 140\nCASE ELSE\n r = 0\nEND SELECT\nPRINT r\nEND", Dialect.Pb36);
    Assert.That(ifChain, Is.EqualTo(select), "equality IF-chain compiles to the same jump table as SELECT CASE");
  }

  [Test]
  public void Emit_GivenMultiplyByOne_WhenPb36PlainOptimize_ThenNoImul() {
    var one = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 1\nPRINT y\nEND", Dialect.Pb36);
    var three = Compile("DIM x AS INTEGER, y AS INTEGER\nLINE INPUT z$\nx = VAL(z$)\ny = x * 3\nPRINT y\nEND", Dialect.Pb36);
    Assert.That(one.Length, Is.LessThan(three.Length), "x * 1 folds away without $OPTIMIZE SPEED; x * 3 keeps IMUL");
  }

  [Test]
  public void Emit_GivenSelfXor_WhenPb36_ThenFoldedToZeroSmallerImage() {
    var self = Compile("DIM x AS INTEGER, r AS INTEGER\nLINE INPUT x$\nx = VAL(x$)\nr = x XOR x\nPRINT r\nEND", Dialect.Pb36);
    var distinct = Compile("DIM x AS INTEGER, y AS INTEGER, r AS INTEGER\nLINE INPUT x$\nx = VAL(x$)\ny = 3\nr = x XOR y\nPRINT r\nEND", Dialect.Pb36);
    Assert.That(self.Length, Is.LessThan(distinct.Length), "x XOR x folds to 0; x XOR y keeps the XOR");
  }

  [Test]
  public void Emit_GivenRepeatedLenOfSameString_WhenPb36_ThenCachedSmallerImage() {
    const string three = "DIM s AS STRING, n AS LONG\nLINE INPUT s\nn = LEN(s) + LEN(s) + LEN(s)\nPRINT n\nEND";
    var unit = Parser.Parse(Lexer.Tokenize(three, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var opt = new CodeGenerator(model).EmitExecutable();
    var noOpt = new CodeGenerator(model) { Optimize = false }.EmitExecutable();
    Assert.That(opt.Length, Is.LessThan(noOpt.Length), "repeated LEN(s$) caches to one descriptor read");
  }

  [Test]
  public void Emit_GivenZeroLengthLeftDollar_WhenPb36_ThenFoldedToEmptyNoCall() {
    var zero = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = LEFT$(a, 0)\nPRINT t\nEND", Dialect.Pb36);
    var nonZero = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = LEFT$(a, 2)\nPRINT t\nEND", Dialect.Pb36);
    Assert.That(zero.Length, Is.LessThan(nonZero.Length),
      "LEFT$(a$, 0) folds to the empty string (no StrLeft); LEFT$(a$, 2) keeps the call");
  }

  [Test]
  public void Emit_GivenConcatWithEmptyLiteral_WhenPb36_ThenNoStrCatCall() {
    var empty = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = a + \"\"\nPRINT t\nEND", Dialect.Pb36);
    var nonEmpty = Compile("DIM a AS STRING, t AS STRING\nLINE INPUT a\nt = a + \"x\"\nPRINT t\nEND", Dialect.Pb36);
    Assert.That(empty.Length, Is.LessThan(nonEmpty.Length),
      "a$ + \"\" drops to a plain copy (no StrCat); a$ + \"x\" keeps the concat");
  }

  [Test]
  public void Emit_GivenEmptyStringComparison_WhenPb36_ThenHandleTestNotStrCmp() {
    var empty = Compile("DIM s AS STRING\nLINE INPUT s\nIF s = \"\" THEN PRINT 1\nEND", Dialect.Pb36);
    var nonEmpty = Compile("DIM s AS STRING\nLINE INPUT s\nIF s = \"x\" THEN PRINT 1\nEND", Dialect.Pb36);
    Assert.That(empty.Length, Is.LessThan(nonEmpty.Length),
      "s = \"\" tests the handle (rt_strcmp trimmed); s = \"x\" keeps the StrCmp call");
  }

  [Test]
  public void Emit_GivenUnsignedComparisonAsValue_WhenPb36_ThenBranchlessSbb() {
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
    const string body = "DECLARE FUNCTION Twice&(BYVAL x&)\nPRINT Twice(21)\nEND\nFUNCTION Twice&(BYVAL x&)__\nTwice = x& + x&\nEND FUNCTION";
    Assert.That(Procedures(body.Replace("__", " NOINLINE")), Does.Contain("Twice"), "NOINLINE must keep the procedure");
    Assert.That(Procedures(body.Replace("__", "")), Does.Not.Contain("Twice"), "without it the inliner should absorb it");
  }

  private static IEnumerable<string> Procedures(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return generator.DescribeImage().Procedures.Select(p => p.Name);
  }

  private static IReadOnlyCollection<string> RuntimeSurface(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", dialect), "TEST.BAS", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return generator.DescribeImage().RuntimeLabels.Select(l => l.Name).ToList();
  }

  [Test]
  public void Emit_GivenLongCompareRangeKnown_WhenPb36_ThenNarrowedTo16BitCompare() {
    const string narrowed = "$OPTIMIZE SPEED\nDIM n AS LONG\nFOR i& = 1 TO 100\nIF i& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    const string wide = "$OPTIMIZE SPEED\nDIM n AS LONG\nINPUT k&\nFOR i& = 1 TO 100\nIF k& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    Assert.That(CountSbbDxCx(Compile(narrowed, Dialect.Pb36)), Is.Zero, "a range-known LONG compare should not emit the 32-bit sequence");
    Assert.That(CountCmpAxBx(Compile(narrowed, Dialect.Pb36)), Is.GreaterThan(0), "it should compare in 16 bits instead");
    Assert.That(CountSbbDxCx(Compile(wide, Dialect.Pb36)), Is.GreaterThan(0), "an unknown-range LONG compare must keep the 32-bit sequence");
  }

  [Test]
  public void Emit_GivenLongCompareRangeKnown_WhenOptimizerOff_ThenWideCompareKept() {
    const string source = "DIM n AS LONG\nFOR i& = 1 TO 100\nIF i& < 50& THEN n = n + 1\nNEXT i&\nPRINT n\nEND";
    Assert.That(CountSbbDxCx(Compile(source, Dialect.Pb35)), Is.GreaterThan(0));
  }

  [Test]
  public void Emit_GivenDwordMultiplyRangeKnown_WhenPb36_ThenNarrowedTo16BitMul() {
    const string narrowed = "$ERROR NUMERIC ON\n$OPTIMIZE SPEED\nDIM c AS DWORD\nFOR i& = 1 TO 100\na??? = i&\nb??? = 3\nc = a??? * b???\nNEXT i&\nPRINT c\nEND";
    const string wide = "$ERROR NUMERIC ON\n$OPTIMIZE SPEED\nDIM c AS DWORD\nINPUT k&\nFOR i& = 1 TO 100\na??? = k&\nb??? = 3\nc = a??? * b???\nNEXT i&\nPRINT c\nEND";
    Assert.That(CountMulBx(Compile(narrowed, Dialect.Pb36)),
      Is.GreaterThan(CountMulBx(Compile(wide, Dialect.Pb36))),
      "a range-known DWORD multiply should add a 16-bit MUL BX the runtime-call version lacks");
  }

  [Test]
  public void Execute_GivenLongCompareRangeKnown_WhenPb36_ThenSameResultsAsWide() {
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
    const string body = "$OPTIMIZE SPEED\nDECLARE SUB Bits(BYVAL a&, BYVAL b&)\nBits -1, 7\nBits -2, 1\nEND\n"
      + "SUB Bits(BYVAL a&, BYVAL b&) NOINLINE\nx&& = a&\ny&& = b&\nPRINT x&& OR y&&\nEND SUB";
    var with386 = "$CPU 80386\n" + body;
    static (byte[] Image, IReadOnlyList<string> Routes) CompileRouted(string source) {
      var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model) { UseExperimentalBackend = true };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      return (image, generator.BackendRoutedNames.ToList());
    }
    var native = CompileRouted(with386);
    var baseline = CompileRouted(body);
    Assert.That(native.Routes, Does.Contain("Bits"), "the dword operations must come from routed code");
    Assert.That(CountDwordOrEax(native.Image), Is.GreaterThan(CountDwordOrEax(baseline.Image)),
      "$CPU 80386 should add inline 32-bit OR halves the runtime-call version lacks");
  }

  private static int CountDwordOrEax(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x0B)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenQuadShiftUnderCpu386_WhenPb36_ThenDoublePrecisionShld() {
    const string body = "$OPTIMIZE SPEED\nDECLARE SUB Shifted(BYVAL a&)\nShifted 3\nShifted 5\nEND\n"
      + "SUB Shifted(BYVAL a&) NOINLINE\nx&& = a&\nSHIFT LEFT x&&, 5\nPRINT x&&\nEND SUB";
    static (byte[] Image, IReadOnlyList<string> Routes) CompileRouted(string source) {
      var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
      var model = Binder.Bind(unit, Dialect.Pb36);
      Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
      var generator = new CodeGenerator(model) { UseExperimentalBackend = true };
      var image = generator.EmitExecutable();
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      return (image, generator.BackendRoutedNames.ToList());
    }
    var native = CompileRouted("$CPU 80386\n" + body);
    var baseline = CompileRouted(body);
    Assert.That(native.Routes, Does.Contain("Shifted"), "the SHLD must come from routed code");
    Assert.That(CountShld(native.Image), Is.GreaterThan(CountShld(baseline.Image)),
      "$CPU 80386 should add a 66 0F A4 SHLD the per-bit-loop version lacks");
  }

  private static int CountShld(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x0F && image[i + 2] == 0xA4)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenEraseStaticArrayUnderCpu386_WhenPb36_ThenRepStosd() {
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\na%(1) = 5\nERASE a%\nPRINT a%(1)\nEND";
    const string no386 = "$OPTIMIZE SPEED\nDIM a%(1 TO 10)\na%(1) = 5\nERASE a%\nPRINT a%(1)\nEND";
    Assert.That(CountRepStosd(Compile(with386, Dialect.Pb36)), Is.GreaterThan(CountRepStosd(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should zero-fill the ERASEd array with REP STOSD");
  }

  [Test]
  public void Emit_GivenConstantArrayFillUnderCpu386_WhenPb36_ThenRepStosd() {
    const string with386 = "$CPU 80386\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = 1234\nNEXT i%\nPRINT a%(1)\nEND";
    const string no386 = "$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = 1234\nNEXT i%\nPRINT a%(1)\nEND";
    Assert.That(CountRepStosd(Compile(with386, Dialect.Pb36)), Is.GreaterThan(CountRepStosd(Compile(no386, Dialect.Pb36))),
      "$CPU 80386 should fill the array DWORD-wide with REP STOSD");
  }

  [Test]
  public void Emit_GivenLatticeProvenCondition_WhenPb36_ThenDeadArmNotEmitted() {
    const string folds = "$OPTIMIZE SPEED\np% = 5\nIF q% > 0 THEN p% = 8\nIF p% < 20 THEN\nPRINT \"LIVE\"\nELSE\nPRINT \"DEADXYZ\"\nEND IF\nEND";
    const string nofold = "$OPTIMIZE SPEED\nINPUT p%\nIF p% < 20 THEN\nPRINT \"LIVE\"\nELSE\nPRINT \"DEADXYZ\"\nEND IF\nEND";
    Assert.That(Ascii(Compile(folds, Dialect.Pb36)), Does.Not.Contain("DEADXYZ"), "a lattice-proven-false arm should be dead-code-eliminated");
    Assert.That(Ascii(Compile(nofold, Dialect.Pb36)), Does.Contain("DEADXYZ"), "an unknown condition keeps both arms");
  }

  [Test]
  public void Emit_GivenLatticeBoundedIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    const string bounded = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 20)\nk% = 5\nIF c% > 0 THEN k% = 10\na%(k%) = k%\nEND";
    const string unknown = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 20)\nINPUT k%\na%(k%) = k%\nEND";
    Assert.That(CountRaise9(Compile(bounded, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(unknown, Dialect.Pb36))),
      "a lattice-bounded variable index inside the array bounds should drop the Error-9 check");
  }

  [Test]
  public void Emit_GivenForCounterIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    const string counterIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nFOR i% = 1 TO 10\na%(i%) = i%\nNEXT i%\nEND";
    const string varIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(1 TO 10)\nINPUT k%\nFOR i% = 1 TO 10\na%(k%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(counterIdx, Dialect.Pb36)), Is.Zero,
      "a FOR-counter index inside the array bounds should drop the Error-9 bounds check");
    Assert.That(CountRaise9(Compile(varIdx, Dialect.Pb36)), Is.Positive,
      "an unknown index keeps it - otherwise the assertion above is measuring nothing");
  }

  [Test]
  public void Emit_GivenTwoRangeIndexUnderBoundsOn_WhenPb36_ThenCheckElided() {
    const string twoRange = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 30)\nFOR i% = 2 TO 9\nj% = i% - 1\na%(i% + j%) = i%\nNEXT i%\nEND";
    const string defeated = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 30)\nINPUT j%\nFOR i% = 2 TO 9\na%(i% + j%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(twoRange, Dialect.Pb36)), Is.LessThan(CountRaise9(Compile(defeated, Dialect.Pb36))),
      "an index summing two range-known vars, provably in bounds, should drop the Error-9 check");
  }

  [Test]
  public void Emit_GivenMaskedAndModIndexUnderBoundsOn_WhenPb36_ThenInRangeCheckElided() {
    const string andIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 7)\nFOR i% = 1 TO 50\na%(i% AND 7) = i%\nNEXT i%\nEND";
    const string modIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 7)\nFOR i% = 0 TO 50\na%(i% MOD 8) = i%\nNEXT i%\nEND";
    const string unknownIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 7)\nINPUT k%\nFOR i% = 1 TO 50\na%(k%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(andIdx, Dialect.Pb36)), Is.Zero, "x AND 7 is always in [0,7] - the bounds check should drop");
    Assert.That(CountRaise9(Compile(modIdx, Dialect.Pb36)), Is.Zero, "i% MOD 8 over a non-negative counter is in [0,7] - the bounds check should drop");
    Assert.That(CountRaise9(Compile(unknownIdx, Dialect.Pb36)), Is.Positive, "an unknown index into the same array keeps the check");
  }

  [Test]
  public void Emit_GivenDividedIndexUnderBoundsOn_WhenPb36_ThenInRangeCheckElided() {
    const string idx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 15)\nFOR i% = 0 TO 30\na%(i% \\ 2) = i%\nNEXT i%\nEND";
    const string unknownIdx = "$ERROR BOUNDS ON\n$OPTIMIZE SPEED\nDIM a%(0 TO 15)\nINPUT k%\nFOR i% = 0 TO 30\na%(k%) = i%\nNEXT i%\nEND";
    Assert.That(CountRaise9(Compile(idx, Dialect.Pb36)), Is.Zero, "i% \\ 2 over [0,30] is in [0,15] - the bounds check should drop");
    Assert.That(CountRaise9(Compile(unknownIdx, Dialect.Pb36)), Is.Positive, "an unknown index into the same array keeps the check");
  }

  [Test]
  public void Emit_GivenForCounterAddUnderOverflowOn_WhenPb36_ThenCheckElided() {
    const string counterAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i% = 1 TO 100\nx% = i% + 1\nNEXT i%\nEND";
    const string varAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k%\nFOR i% = 1 TO 100\nx% = k% + 1\nNEXT i%\nEND";
    Assert.That(CountRaise6(Compile(counterAdd, Dialect.Pb36)), Is.LessThan(CountRaise6(Compile(varAdd, Dialect.Pb36))),
      "an in-range FOR-counter add should drop its Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenLongForCounterAddUnderOverflowOn_WhenPb36_ThenCheckElided() {
    const string counterAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i& = 1 TO 100\nx& = i& + 1&\nNEXT i&\nEND";
    const string varAdd = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k&\nFOR i& = 1 TO 100\nx& = k& + 1&\nNEXT i&\nEND";
    Assert.That(CountRaise6(Compile(counterAdd, Dialect.Pb36)), Is.LessThan(CountRaise6(Compile(varAdd, Dialect.Pb36))),
      "an in-range LONG FOR-counter add should drop its 32-bit Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenLongForCounterSubtractUnderOverflowOn_WhenPb36_ThenCheckElided() {
    const string counterSub = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nFOR i& = 1 TO 100\nx& = i& - 1&\nNEXT i&\nEND";
    const string varSub = "$ERROR OVERFLOW ON\n$OPTIMIZE SPEED\nINPUT k&\nFOR i& = 1 TO 100\nx& = k& - 1&\nNEXT i&\nEND";
    Assert.That(CountRaise6(Compile(counterSub, Dialect.Pb36)), Is.LessThan(CountRaise6(Compile(varSub, Dialect.Pb36))),
      "an in-range LONG FOR-counter subtract should drop its 32-bit Error-6 overflow check");
  }

  [Test]
  public void Emit_GivenDivideByForCounter_WhenPb36_ThenZeroGuardElided() {
    const string counterDiv = "$OPTIMIZE SPEED\nFOR i% = 1 TO 10\nx% = 100 \\ i%\nNEXT i%\nPRINT x%\nEND";
    const string varDiv = "$OPTIMIZE SPEED\nDECLARE SUB d(BYVAL k AS INTEGER)\nd 3\nd 7\nEND\nSUB d(BYVAL k AS INTEGER) NOINLINE\nPRINT 100 \\ k\nEND SUB";
    Assert.Multiple(() => {
      Assert.That(CountRaise11(Compile(counterDiv, Dialect.Pb36)), Is.Zero,
        "a divisor whose counter range excludes zero should drop the divide-by-zero guard");
      Assert.That(CountRaise11(Compile(varDiv, Dialect.Pb36)), Is.Positive,
        "an unknown divisor keeps it - otherwise the assertion above is measuring nothing");
    });
  }

  [Test]
  public void Emit_GivenLiteralSelfAppend_WhenPb36Speed_ThenInPlaceNoLiteralAlloc() {
    const string append = "$OPTIMIZE SPEED\ns$ = \"z\"\nFOR i% = 1 TO 3\ns$ = s$ + \"x\"\nNEXT i%\nPRINT s$\nEND";
    const string prepend = "$OPTIMIZE SPEED\ns$ = \"z\"\nFOR i% = 1 TO 3\ns$ = \"x\" + s$\nNEXT i%\nPRINT s$\nEND";
    Assert.That(CountMovDxDs(Compile(append, Dialect.Pb36)), Is.LessThan(CountMovDxDs(Compile(prepend, Dialect.Pb36))),
      "a literal self-append should append in place, not materialize the literal at the call site");
  }

  private static int CountMovDxDs(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8C && image[i + 1] == 0xDA)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenVariableSelfAppend_WhenPb36Speed_ThenCallsInPlaceRoutine() {
    const string withVar = "$OPTIMIZE SPEED\ns$ = \"a\"\nv$ = \"b\"\nFOR i% = 1 TO 3\ns$ = s$ + v$\nNEXT i%\nPRINT s$\nEND";
    const string literal = "$OPTIMIZE SPEED\ns$ = \"a\"\nFOR i% = 1 TO 3\ns$ = s$ + \"x\"\nNEXT i%\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatVar(Compile(withVar, Dialect.Pb36)), Is.GreaterThan(0), "a variable self-append should call rt_strcatvar");
    Assert.That(CountCallsToStrCatVar(Compile(literal, Dialect.Pb36)), Is.Zero, "a literal self-append should not call rt_strcatvar");
  }

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
    const string funcLeft = "$OPTIMIZE SPEED\nx$ = \"hello\"\nv$ = \"!\"\ns$ = LEFT$(x$, 3) + v$\nPRINT s$\nEND";
    const string varLeft = "$OPTIMIZE SPEED\nx$ = \"hello\"\nv$ = \"!\"\ns$ = x$ + v$\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatVar(Compile(funcLeft, Dialect.Pb36)), Is.GreaterThan(CountCallsToStrCatVar(Compile(varLeft, Dialect.Pb36))),
      "a LEFT$/MID$ result is a dead temp whose tail appends in place; a bare-variable left does not");
  }

  [Test]
  public void Emit_GivenNonLeftLeaningConcat_WhenPb36_ThenSingleAllocMultiConcat() {
    const string balanced = "$OPTIMIZE SPEED\na$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\ns$ = (a$ + b$) + (c$ + d$)\nPRINT s$\nEND";
    const string impure = "$OPTIMIZE SPEED\na$=\"a\"\nb$=\"b\"\nc$=\"c\"\ns$ = (a$ + b$) + UCASE$(c$)\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(balanced, Dialect.Pb36)), Is.EqualTo(1),
      "a four-leaf concat tree of plain variables builds with one rt_strcatn allocation");
    Assert.That(CountCallsToStrCatN(Compile(impure, Dialect.Pb36)), Is.Zero,
      "a chain whose operand is a call (shared/volatile buffer) is not pre-staged - it falls back off rt_strcatn");
  }

  [Test]
  public void Emit_GivenConcatChain_WhenPb36_ThenSingleAllocMultiConcat() {
    const string chain = "$OPTIMIZE SPEED\na$ = \"a\"\nb$ = \"b\"\nc$ = \"c\"\ns$ = a$ + b$ + c$\nPRINT s$\nEND";
    const string pair = "$OPTIMIZE SPEED\na$ = \"a\"\nb$ = \"b\"\ns$ = a$ + b$\nPRINT s$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb36)), Is.EqualTo(1),
      "a three-operand concat chain builds with one rt_strcatn allocation");
    Assert.That(CountCallsToStrCatN(Compile(pair, Dialect.Pb36)), Is.Zero,
      "a two-operand concat does not use the multi-concat builder");
  }

  [Test]
  public void Emit_GivenStringSelfAppend_WhenPb36_ThenSmallerThanNonSelf() {
    const string selfAppend = "$OPTIMIZE SPEED\ns$ = \"a\"\nt$ = \"c\"\nx$ = \"b\"\ns$ = s$ + x$\nPRINT s$; t$\nEND";
    const string nonSelf = "$OPTIMIZE SPEED\ns$ = \"a\"\nt$ = \"c\"\nx$ = \"b\"\ns$ = t$ + x$\nPRINT s$; t$\nEND";
    Assert.That(Compile(selfAppend, Dialect.Pb36).Length, Is.LessThan(Compile(nonSelf, Dialect.Pb36).Length),
      "a string self-append should emit less code than the non-self concat");
  }

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

  private static int CountCallsToLen(byte[] image) {
    var head = -1;
    for (var i = 0; i + 8 <= image.Length && head < 0; ++i)
      if (image[i] == 0x85 && image[i + 1] == 0xC0 && image[i + 2] == 0x74
          && image[i + 4] == 0x53 && image[i + 5] == 0x56 && image[i + 6] == 0x89 && image[i + 7] == 0xC3)
        head = i;
    if (head < 0)
      return 0;
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0xE8 && i + 3 + (short)(image[i + 1] | (image[i + 2] << 8)) == head)
        ++count;
    return count;
  }

  private static bool ContainsSeq(byte[] image, params byte[] seq) {
    for (var i = 0; i + seq.Length <= image.Length; ++i) {
      var match = true;
      for (var j = 0; j < seq.Length; ++j)
        if (image[i + j] != seq[j]) { match = false; break; }
      if (match)
        return true;
    }
    return false;
  }

  [Test]
  public void Emit_GivenBoundedRangeCheck_WhenPb36_ThenSingleUnsignedCompare() {
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nIF x% >= 0 AND x% <= 15 THEN PRINT \"y\" ELSE PRINT \"n\"\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(img, 0x83, 0xF8, 0x0F, 0x77), Is.True, "the range check compares AX against the window with an unsigned branch");
    Assert.That(ContainsSeq(img, 0x83, 0xF8, 0x0F, 0x7F), Is.False, "and does not emit the signed two-compare short-circuit");
  }

  [Test]
  public void Emit_GivenOutOfRangeCheck_WhenPb36_ThenSingleUnsignedCompare() {
    var img = Compile("$OPTIMIZE SPEED\nDIM x%\nINPUT x%\nIF x% < 0 OR x% > 15 THEN PRINT \"o\" ELSE PRINT \"i\"\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(img, 0x83, 0xF8, 0x0F, 0x76) || ContainsSeq(img, 0x83, 0xF8, 0x0F, 0x77), Is.True,
      "the out-of-range OR folds to one unsigned compare (cmp ax, 15 / jbe or ja)");
  }

  [Test]
  public void Emit_GivenAscOfSingleCharMid_WhenPb36_ThenReadsDirectlyNotViaSubstring() {
    const string program = "$OPTIMIZE SPEED\nDIM s$, i%, n%, c%\nLINE INPUT s$\nINPUT i%\nINPUT n%\n";
    var direct = Compile(program + "c% = ASC(MID$(s$, i%, 1))\nPRINT c%; n%\nEND", Dialect.Pb36);
    var runtime = Compile(program + "c% = ASC(MID$(s$, i%, n%))\nPRINT c%; n%\nEND", Dialect.Pb36);
    Assert.That(HasCharAtRoutine(direct), Is.True, "a length-1 ASC(MID$) reads the byte directly (rt_charat)");
    Assert.That(HasCharAtRoutine(runtime), Is.False, "a runtime length must allocate the substring - no direct read");
  }

  private static bool HasCharAtRoutine(byte[] image)
    => ContainsSeq(image, 0x53, 0x56, 0x06, 0x50, 0x85, 0xC0);

  [Test]
  public void Emit_GivenSingleCharInstr_WhenPb36_ThenScansBytesNotSubstring() {
    var scan = Compile("$OPTIMIZE SPEED\nDIM s$, p%\nLINE INPUT s$\np% = INSTR(s$, \",\")\nPRINT p%\nEND", Dialect.Pb36);
    var probe = Compile("$OPTIMIZE SPEED\nDIM s$, p%\nLINE INPUT s$\np% = INSTR(s$, \",;\")\nPRINT p%\nEND", Dialect.Pb36);
    Assert.That(scan.SequenceEqual(probe), Is.False, "a single-char INSTR scans bytes; a multi-char one keeps the substring probe");
  }

  [Test]
  public void Emit_GivenRightOneCompare_WhenPb36_ThenReadsLastByteDirectly() {
    var direct = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF RIGHT$(s$, 1) = \"/\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    var substr = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF RIGHT$(s$, 2) = \"x/\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    Assert.That(direct.SequenceEqual(substr), Is.False, "RIGHT$(s$,1) reads the last byte directly; RIGHT$(s$,2) keeps the substring compare");
  }

  [Test]
  public void Emit_GivenLeftOneCompare_WhenPb36_ThenReadsFirstByteDirectly() {
    var direct = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF LEFT$(s$, 1) = \"-\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    var substr = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF LEFT$(s$, 2) = \"-x\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    Assert.That(direct.SequenceEqual(substr), Is.False, "LEFT$(s$,1) reads the first byte directly; LEFT$(s$,2) keeps the substring compare");
  }

  [Test]
  public void Emit_GivenSingleCharMidCompare_WhenPb36_ThenComparesByteNotSubstring() {
    var direct = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF MID$(s$, 1, 1) = \"x\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    var strcmp = Compile("$OPTIMIZE SPEED\nDIM s$, r%\nLINE INPUT s$\nIF MID$(s$, 1, 1) = \"xy\" THEN r% = 1\nPRINT r%\nEND", Dialect.Pb36);
    Assert.That(direct.SequenceEqual(strcmp), Is.False, "a single-char MID$ compare reads the byte directly; a multi-char one keeps the string compare");
  }

  [Test]
  public void Emit_GivenStringEquality_WhenPb36_ThenUsesLengthGuardedCompare() {
    static bool WindowAfterPrologue(byte[] img, params byte[] marker) {
      var head = new byte[] { 0x53, 0x51, 0x52, 0x56, 0x57, 0x06 };
      for (var i = 0; i + head.Length < img.Length; ++i) {
        var atHead = true;
        for (var j = 0; j < head.Length; ++j) if (img[i + j] != head[j]) { atHead = false; break; }
        if (!atHead) continue;
        for (var k = i; k < i + 64 && k + marker.Length <= img.Length; ++k) {
          var m = true;
          for (var j = 0; j < marker.Length; ++j) if (img[k + j] != marker[j]) { m = false; break; }
          if (m) return true;
        }
      }
      return false;
    }
    var eq = Compile("DIM a$, b$\nLINE INPUT a$\nLINE INPUT b$\nIF a$ = b$ THEN PRINT 1 ELSE PRINT 0\nEND", Dialect.Pb36);
    var lt = Compile("DIM a$, b$\nLINE INPUT a$\nLINE INPUT b$\nIF a$ < b$ THEN PRINT 1 ELSE PRINT 0\nEND", Dialect.Pb36);
    Assert.That(WindowAfterPrologue(eq, 0x39, 0xD0, 0x75), Is.True, "`=` uses the length-guarded compare (StrCmpEq)");
    Assert.That(WindowAfterPrologue(lt, 0x39, 0xD1, 0x76), Is.True, "`<` keeps the full StrCmp with its min computation");
  }

  [Test]
  public void Emit_GivenSingleExitFunction_WhenPb36_ThenResultForwardedNotReloaded() {
    static bool HasResultReload(byte[] img) {
      for (var i = 0; i + 4 < img.Length; ++i)
        if (img[i] == 0x8B && img[i + 1] == 0x46 && img[i + 3] == 0x89 && img[i + 4] == 0xEC)
          return true;
      return false;
    }
    var forwarded = Compile("$OPTIMIZE SPEED\nDECLARE FUNCTION a%(x%)\nq% = a%(5)\nPRINT q%\nEND\n"
      + "FUNCTION a%(x%)\n a% = x% + 3\nEND FUNCTION", Dialect.Pb36);
    var reloaded = Compile("$OPTIMIZE SPEED\nDECLARE FUNCTION a%(x%)\nq% = a%(5)\nPRINT q%\nEND\n"
      + "FUNCTION a%(x%)\n IF x% > 99 THEN a% = 0 : EXIT FUNCTION\n a% = x% + 3\nEND FUNCTION", Dialect.Pb36);
    Assert.That(HasResultReload(forwarded), Is.False, "a single-exit function forwards its result (no epilogue reload)");
    Assert.That(HasResultReload(reloaded), Is.True, "a multi-exit function keeps the epilogue reload");
  }

  [Test]
  public void Emit_GivenConstantForLimit_WhenPb36_ThenComparedAgainstImmediate() {
    var constLim = Compile("$OPTIMIZE SPEED\nDIM i%, s%\nFOR i% = 1 TO 100\ns% = s% XOR i%\nNEXT\nPRINT s%\nEND", Dialect.Pb36);
    var varLim = Compile("$OPTIMIZE SPEED\nDIM i%, s%, n%\nn% = 100\nFOR i% = 1 TO n%\ns% = s% XOR i%\nNEXT\nPRINT s%\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(constLim, 0x83, 0xFE, 0x64), Is.True, "a constant limit compares SI against the immediate 100");
    Assert.That(ContainsSeq(constLim, 0x3B, 0xB6) || ContainsSeq(constLim, 0x3B, 0x76), Is.False, "no per-iteration memory limit read");
    Assert.That(ContainsSeq(varLim, 0x3B, 0xB6) || ContainsSeq(varLim, 0x3B, 0x76), Is.True, "a variable limit still reads the limit cell each iteration");
  }

  [Test]
  public void Emit_GivenConstantForLimitOnNestedCounter_WhenPb36_ThenComparedAgainstImmediate() {
    var img = Compile("$OPTIMIZE SPEED\nDIM i%, j%, s%\ns% = 0\nFOR i% = 1 TO 20\nFOR j% = 1 TO 10\ns% = s% XOR j%\nNEXT\nNEXT\nPRINT s%\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(img, 0x83, 0xFF, 0x0A), Is.True,
      "a constant inner limit compares DI against the immediate 10");
  }

  [Test]
  public void Emit_GivenConstantForLimitOn386LongCounter_WhenPb36_ThenComparedAgainstImmediate() {
    var img = Compile("$CPU 80386\n$OPTIMIZE SPEED\nDIM i&, s&\ns& = 0\nFOR i& = 1 TO 100\ns& = s& XOR i&\nNEXT\nPRINT s&\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(img, 0x66, 0x83, 0xFE, 0x64), Is.True,
      "a constant LONG limit compares ESI against the immediate 100");
  }

  [Test]
  public void Emit_GivenConstantForLimitOnMemoryCounter_WhenPb36_ThenComparedAgainstImmediate() {
    var img = Compile("$OPTIMIZE SPEED\nDIM i%, t$\nt$ = \"\"\nFOR i% = 1 TO 100\nt$ = t$ + \"x\"\nNEXT\nPRINT LEN(t$)\nEND", Dialect.Pb36);
    Assert.That(ContainsSeq(img, 0x83, 0xF8, 0x64), Is.True,
      "a constant limit on the memory-counter path compares AX against the immediate 100");
  }

  [Test]
  public void Emit_GivenLoopInvariantLen_WhenPb36_ThenHoistedToOneDescriptorRead() {
    const string invariant = "$OPTIMIZE SPEED\nDIM s AS STRING, i%, n%\ns = \"hello world\"\ni% = 1\n"
      + "WHILE i% <= LEN(s)\nn% = n% + LEN(s)\ni% = i% + 1\nWEND\nPRINT n%\nEND";
    Assert.That(CountCallsToLen(Compile(invariant, Dialect.Pb36)), Is.EqualTo(1),
      "a loop-invariant LEN(s$) used in the condition and body hoists to one rt_len call");
  }

  [Test]
  public void Emit_GivenLenOfStringWrittenInLoop_WhenPb36_ThenNotHoisted() {
    const string variant = "$OPTIMIZE SPEED\nDIM s AS STRING, i%, n%\ns = \"hi\"\ni% = 1\n"
      + "WHILE i% <= LEN(s)\ns = s + \"x\"\nn% = n% + LEN(s)\ni% = i% + 1\nWEND\nPRINT n%\nEND";
    Assert.That(CountCallsToLen(Compile(variant, Dialect.Pb36)), Is.GreaterThan(1),
      "LEN of a string mutated inside the loop is recomputed - the two reads do not collapse to one");
  }

  [Test]
  public void Emit_GivenFourOperandConcatChain_WhenPb36_ThenSingleAllocMultiConcat() {
    const string chain = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\nr$ = a$ & b$ & c$ & d$\nPRINT r$\nEND";
    const string pair = "a$=\"a\"\nb$=\"b\"\nr$ = a$ & b$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb36)), Is.EqualTo(1),
      "a four-operand concat chain takes the single-allocation rt_strcatn path exactly once");
    Assert.That(CountCallsToStrCatN(Compile(pair, Dialect.Pb36)), Is.Zero,
      "a two-operand concat does not use the multi-concat builder");
  }

  [Test]
  public void Emit_GivenThreeOperandConcat_WhenPb36_ThenSingleAllocMultiConcat() {
    const string three = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nr$ = a$ + b$ + c$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(three, Dialect.Pb36)), Is.EqualTo(1),
      "a three-operand concat is the boundary case that fires the single-allocation builder");
  }

  [Test]
  public void Emit_GivenMultiConcat_WhenPb35_ThenNoMultiConcatBuilder() {
    const string chain = "a$=\"a\"\nb$=\"b\"\nc$=\"c\"\nd$=\"d\"\nr$ = a$ & b$ & c$ & d$\nPRINT r$\nEND";
    Assert.That(CountCallsToStrCatN(Compile(chain, Dialect.Pb35)), Is.Zero,
      "pb35 must not take the multi-concat path");
  }

  [Test]
  public void Emit_GivenMultiConcatWithCallOperand_WhenPb36_ThenFallsBackOffTheSingleAllocBuilder() {
    const string withCall = "DECLARE FUNCTION F$()\n"
      + "a$=\"a\"\nc$=\"c\"\nr$ = a$ & F$() & c$\nPRINT r$\nEND\n"
      + "FUNCTION F$()\nF$ = \"x\"\nEND FUNCTION";
    Assert.That(CountCallsToStrCatN(Compile(withCall, Dialect.Pb36)), Is.Zero,
      "a chain containing a call operand is not pre-staged - it falls back off rt_strcatn for correctness");
  }

  private static int CountRaise(byte[] image, byte code) {
    var count = 0;
    for (var i = 0; i + 3 < image.Length; ++i)
      if (image[i] == 0xB8 && image[i + 1] == code && image[i + 2] == 0x00 && image[i + 3] == 0xE8)
        ++count;
    return count;
  }

  private static int CountRaise6(byte[] image) => CountRaise(image, 0x06);
  private static int CountRaise9(byte[] image) => CountRaise(image, 0x09);
  private static int CountRaise11(byte[] image) => CountRaise(image, 0x0B);

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
      if (image[i] == 0xF7 && image[i + 1] == 0xEB)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenCheckedMultiplyByTwo_WhenPb36_ThenKeepsImulForOverflowTrap() {
    const string body = "\nINPUT x%\ny% = x% * 2\nPRINT y%\nEND";
    var checked_ = Compile("$ERROR OVERFLOW ON" + body, Dialect.Pb36);
    var unchecked_ = Compile("$ERROR OVERFLOW OFF" + body, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountImulBx(checked_), Is.GreaterThanOrEqualTo(1),
        "checked x% * 2 must keep IMUL BX for the error-6 overflow trap, not strength-reduce to a shift");
      Assert.That(CountRaise6(checked_), Is.Positive, "and the trap the IMUL is kept for must be in the image");
      Assert.That(CountRaise6(unchecked_), Is.Zero, "without the directive there is no trap - the control that makes the above a measurement");
    });
  }

  [Test]
  public void Emit_GivenAccumulatorLoop_WhenPb36Speed_ThenSmallerFromRegisterResidency() {
    const string body = "s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(speed.Length, Is.LessThan(plain.Length),
      "the register-resident accumulator should shrink the loop versus the memory-cell version");
  }

  [Test]
  public void Emit_GivenConditionalAccumulateLoop_WhenPb36Speed_ThenCounterInSi() {
    const string body = "s% = 0\nFOR i% = 1 TO 10\n  IF i% > 5 THEN s% = s% + i%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountAddSiImm(speed), Is.GreaterThan(CountAddSiImm(plain)),
      "a FOR counter over a clean-IF body should increment in SI");
  }

  [Test]
  public void Emit_GivenBinaryWithProvenConstantOperand_WhenPb36_ThenImmediateAlu() {
    const string proven = "$OPTIMIZE SPEED\nb% = 5\nc% = 0\nFOR i% = 1 TO 10\n  c% = c% + b%\nNEXT i%\nPRINT c%\nEND";
    const string runtime = "$OPTIMIZE SPEED\nDECLARE SUB t(BYVAL k%)\nt 5\nt 7\nEND\nSUB t(BYVAL k%) NOINLINE\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + k%\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountAddAxImm(Compile(proven, Dialect.Pb36)), Is.GreaterThan(CountAddAxImm(Compile(runtime, Dialect.Pb36))),
      "a proven-constant operand should fold into an immediate ALU op (ADD AX, imm); a runtime parameter cannot");
  }

  private static int CountAddAxImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenDirectCellStore_WhenPb36_ThenNoValuePark() {
    const string direct = "$OPTIMIZE SPEED\nDECLARE SUB s(x%)\ns 9\nEND\nSUB s(x%) NOINLINE\n  a% = x%\n  b% = x%\n  d% = x%\n  PRINT a%; b%; d%\nEND SUB";
    const string byref = "$OPTIMIZE SPEED\nDECLARE SUB s(x%)\ns 9\nEND\nSUB s(x%) NOINLINE\n  x% = x% + 1\n  x% = x% + 1\n  x% = x% + 1\n  PRINT x%\nEND SUB";
    Assert.That(CountPushAx(Compile(direct, Dialect.Pb36)), Is.LessThan(CountPushAx(Compile(byref, Dialect.Pb36))),
      "direct-cell stores drop the value park; BYREF stores keep it (one push per store)");
  }

  private static int CountPushAx(byte[] image) {
    var count = 0;
    foreach (var b in image)
      if (b == 0x50)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenNumericPrintInLoopBody_WhenPb36Speed_ThenCounterStaysInSi() {
    const string numeric = "$OPTIMIZE SPEED\ns% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\n  PRINT \"v=\"; s%\nNEXT i%\nEND";
    const string stringVar = "$OPTIMIZE SPEED\nz$ = \"v=\"\ns% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\n  PRINT z$; s%\nNEXT i%\nEND";
    Assert.That(CountAddSiImm(Compile(numeric, Dialect.Pb36)), Is.GreaterThan(CountAddSiImm(Compile(stringVar, Dialect.Pb36))),
      "a numeric/literal PRINT keeps the FOR counter in SI; a string-variable item blocks residency");
  }

  [Test]
  public void Emit_GivenSelectCaseInLoopBody_WhenPb36Speed_ThenCounterStaysInSi() {
    const string intSel = "$OPTIMIZE SPEED\ns% = 0\nFOR i% = 1 TO 10\n  SELECT CASE i%\n  CASE 1, 3, 5\n    s% = s% + i%\n  CASE ELSE\n    s% = s% - 1\n  END SELECT\nNEXT i%\nPRINT s%\nEND";
    const string strSel = "$OPTIMIZE SPEED\nz$ = \"a\"\ns% = 0\nFOR i% = 1 TO 10\n  SELECT CASE z$\n  CASE \"a\"\n    s% = s% + i%\n  END SELECT\nNEXT i%\nPRINT s%\nEND";
    Assert.That(CountAddSiImm(Compile(intSel, Dialect.Pb36)), Is.GreaterThan(CountAddSiImm(Compile(strSel, Dialect.Pb36))),
      "an integer SELECT body keeps the FOR counter in SI; a string SELECT blocks residency");
  }

  [Test]
  public void Emit_GivenLongForLoop_WhenCpu386Speed_ThenCounterInEsi() {
    const string body = "$OPTIMIZE SPEED\ns& = 0\nFOR i& = 1 TO 100\n  s& = s& + i&\n  PRINT s&\nNEXT i&\nPRINT s&\nEND";
    var with386 = CompileWithBackend("$CPU 80386\n" + body, Dialect.Pb36);
    var no386 = CompileWithBackend(body, Dialect.Pb36);
    Assert.That(CountAddEsiImm(with386), Is.GreaterThan(CountAddEsiImm(no386)),
      "a LONG FOR counter should increment in ESI (66 83 C6) under $CPU 80386");
  }

  [Test]
  public void Emit_GivenLongAccumulatorLoop_WhenCpu386Speed_ThenAccumulatorInEdi() {
    const string withAcc = "$CPU 80386\n$OPTIMIZE SPEED\ns& = 0\nFOR i& = 1 TO 100\n  s& = s& + i&\n  PRINT s&\nNEXT i&\nPRINT s&\nEND";
    const string noAcc = "$CPU 80386\n$OPTIMIZE SPEED\nFOR i& = 1 TO 100\n  PRINT i&\nNEXT i&\nEND";
    Assert.That(CountAddEdiEsi(CompileWithBackend(withAcc, Dialect.Pb36)),
      Is.GreaterThan(CountAddEdiEsi(CompileWithBackend(noAcc, Dialect.Pb36))),
      "a LONG accumulator joins the ESI counter in EDI (66 01 F7) under $CPU 80386");
  }

  private static int CountAddEdiEsi(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x01 && image[i + 2] == 0xF7)
        ++count;
    return count;
  }

  private static int CountAddEsiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x66 && image[i + 1] == 0x83 && image[i + 2] == 0xC6)
        ++count;
    return count;
  }

  // 83 C6 = ADD SI, imm8; 46 = INC SI. Both prove the FOR counter is resident in SI.
  private static int CountAddSiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i < image.Length; ++i)
      if (image[i] == 0x46 || (i + 1 < image.Length && image[i] == 0x83 && image[i + 1] == 0xC6))
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenNestedIntegerLoops_WhenPb36Speed_ThenInnerCounterInDi() {
    const string body = "s% = 0\nFOR i% = 1 TO 8\n  FOR j% = 1 TO 8\n    s% = s% + i%\n  NEXT j%\nNEXT i%\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountAddDiImm(speed), Is.GreaterThan(CountAddDiImm(plain)),
      "the inner FOR counter should increment in DI (ADD DI, imm) under SPEED");
  }

  private static int CountAddDiImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC7)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenDoLoopAccumulator_WhenPb36Speed_ThenAccumulatorInSi() {
    const string body = "s% = 0\ni% = 1\nDO\n  s% = s% + i%\n  i% = i% + 1\nLOOP UNTIL i% > 10\nPRINT s%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountMovSiAx(speed), Is.GreaterThan(CountMovSiAx(plain)),
      "a DO-loop accumulator should be written in SI (MOV SI, AX) under SPEED");
  }

  [Test]
  public void Emit_GivenDoLoopTwoAccumulators_WhenPb36Speed_ThenSecondInDi() {
    const string body = "s% = 0\np% = 1\ni% = 1\nDO\n  s% = s% + i%\n  p% = p% + 2\n  i% = i% + 1\nLOOP UNTIL i% > 8\nPRINT s%; p%\nEND";
    var speed = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    var plain = Compile(body, Dialect.Pb36);
    Assert.That(CountMovDiAx(speed), Is.GreaterThan(CountMovDiAx(plain)),
      "a second DO-loop accumulator should live in DI (MOV DI, AX) under SPEED");
  }

  private static int CountMovDiAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x89 && image[i + 1] == 0xF8)
        ++count;
    return count;
  }

  private static int CountMovSiAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x89 && image[i + 1] == 0xF0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Speed_ThenShiftAddReplacesImul() {
    const string source = "$OPTIMIZE SPEED\nx% = 11\nT x%\ny% = x% * 3\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "x% * 3 under SPEED should be a shift-add chain, no IMUL BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThirteen_WhenPb36Speed_ThenThreeTermDecomposition() {
    const string source = "$OPTIMIZE SPEED\nx% = 11\nT x%\ny% = x% * 13\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "x% * 13 decomposes into three shift-add terms, no IMUL BX");
  }

  [Test]
  public void Emit_GivenDataNeverRead_WhenPb36_ThenDataBytesOmitted() {
    const string withRead = "DIM q%\nREAD q%\nDATA 1111, 2222, 3333, 4444, 5555, 6666, 7777, 8888\nPRINT q%\nEND";
    const string noRead = "DATA 1111, 2222, 3333, 4444, 5555, 6666, 7777, 8888\nPRINT \"hi\"\nEND";
    Assert.That(Compile(noRead, Dialect.Pb36).Length, Is.LessThan(Compile(withRead, Dialect.Pb36).Length),
      "DATA that is never read should not emit its bytes");
  }

  [Test]
  public void Emit_GivenModularMultiplyByVariable_WhenPb36Speed_ThenReadsMemoryOperandNoImulBx() {
    const string source = "$OPTIMIZE SPEED\nx% = 11\nz% = 7\nT x%\nT z%\ny% = x% * z%\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.Zero, "a variable*variable multiply should IMUL the direct-memory right operand, not stage it through BX");
  }

  [Test]
  public void Emit_GivenModularMultiplyByThree_WhenPb36Default_ThenKeepsImul() {
    const string source = "x% = 11\nT x%\ny% = x% * 3\nT y%\nEND" + _TOUCH_END;
    var pb36 = Compile(_TOUCH + source, Dialect.Pb36);
    Assert.That(CountImulBx(pb36), Is.EqualTo(1), "without $OPTIMIZE SPEED the compact IMUL BX is kept");
  }

  [Test]
  public void Emit_GivenModularAddConstant_WhenPb36_ThenFewerBytesThanVariableAdd() {
    var constAdd = Compile(_TOUCH + "x% = 100\nT x%\ny% = x% + 7\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    var varAdd = Compile(_TOUCH + "x% = 100\nz% = 7\nT x%\nT z%\ny% = x% + z%\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(constAdd.Length, Is.LessThan(varAdd.Length),
      "v% + const should fold to one immediate ALU op, smaller than a two-operand add");
  }

  private static int CountMovBxAx(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if ((image[i] == 0x8B && image[i + 1] == 0xD8) || (image[i] == 0x89 && image[i + 1] == 0xC3))
        ++count;
    return count;
  }

  private const string _TOUCH = "DECLARE SUB T(a%)\n";
  private const string _TOUCH_END = "\nSUB T(a%) NOINLINE\nEND SUB";
  private const string _TOUCHL = "DECLARE SUB TL(a&)\n";
  private const string _TOUCHL_END = "\nSUB TL(a&) NOINLINE\nEND SUB";

  [Test]
  public void Emit_GivenBitwiseMaskConstant_WhenPb36_ThenFoldsToImmediateNoRegisterLoad() {
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
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + n%\n  NEXT i%\n  PRINT c%\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  c% = 0\n  FOR i% = 1 TO 10\n    c% = c% + (n% * i%)\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountMovBxAx(Compile(mem, Dialect.Pb36)), Is.LessThan(CountMovBxAx(Compile(staged, Dialect.Pb36))),
      "a direct-cell right operand is read as an ALU memory operand, not staged through BX");
  }

  private static int CountAndAxMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x23 && image[i + 1] == 0x06)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenCompareWithMemoryRightOperand_WhenPb36_ThenCmpMemoryOperand() {
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  c% = 0\n  FOR i% = 1 TO 10\n    IF i% > n% THEN c% = c% + 1\n  NEXT i%\n  PRINT c%\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  c% = 0\n  FOR i% = 1 TO 10\n    IF i% > (n% * i%) THEN c% = c% + 1\n  NEXT i%\n  PRINT c%\nEND SUB";
    Assert.That(CountCmpMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountCmpMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell compare operand is read as a CMP memory operand (CMP AX,[n%]); a staged operand is CMP AX,BX");
  }

  private static int CountCmpMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x3B && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenSelfModifyStore_WhenPb36_ThenMemoryReadModifyWrite() {
    const string rmw = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  a% = n%\n  a% = a% + 1\n  PRINT a%\nEND SUB";
    const string nonrmw = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  a% = n%\n  b% = a% + 1\n  PRINT a%; b%\nEND SUB";
    Assert.That(CountIncMem(Compile(rmw, Dialect.Pb36)), Is.GreaterThan(CountIncMem(Compile(nonrmw, Dialect.Pb36))),
      "a self-increment of a direct cell becomes INC [mem]; an increment into a different target does not");
  }

  private static int CountIncMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xFF && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenIncrWithAmount_WhenPb36_ThenMemoryAddImmediate() {
    const string direct = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  a% = n%\n  INCR a%, 5\n  INCR a%, 6\n  PRINT a%\nEND SUB";
    const string array = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  DIM z%(0 TO 3)\n  z%(1) = n%\n  INCR z%(1), 5\n  INCR z%(1), 6\n  PRINT z%(1)\nEND SUB";
    Assert.That(CountAddMemImm(Compile(direct, Dialect.Pb36)), Is.GreaterThan(CountAddMemImm(Compile(array, Dialect.Pb36))),
      "INCR of a direct cell with a constant amount uses ADD [mem],imm; an array element does not");
  }

  private static int CountAddMemImm(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x83 && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatBinaryWithDirectCellOperand_WhenPb36_ThenFpuMemoryOperand() {
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  b! = n% + 1\n  r! = a! + b!\n  r! = r! + b!\n  PRINT r!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  b! = n% + 1\n  r! = a! + (b! * a!)\n  r! = r! + (b! * a!)\n  PRINT r!\nEND SUB";
    Assert.That(CountFaddMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFaddMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell float operand is added as an FPU memory operand (FADD m32); a staged operand uses FADDP");
  }

  private static int CountFaddMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatCompareWithDirectCellOperand_WhenPb36_ThenFcompMemoryOperand() {
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  b! = n% + 1\n  IF a! < b! THEN PRINT \"lt\"\n  IF a! > b! THEN PRINT \"gt\"\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  b! = n% + 1\n  IF a! < (b! * a!) THEN PRINT \"lt\"\n  IF a! > (b! * a!) THEN PRINT \"gt\"\nEND SUB";
    Assert.That(CountFcompMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFcompMem(Compile(staged, Dialect.Pb36))),
      "a direct-cell float compare operand uses FCOMP m32; a staged operand uses FXCH;FCOMPP");
  }

  private static int CountFcompMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0x18 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatTimesIntegerCell_WhenPb36_ThenFpuIntegerMemoryOperand() {
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  x! = n%\n  i% = n% + 1\n  x! = x! + i%\n  x! = x! + i%\n  PRINT x!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  x! = n%\n  i% = n% + 1\n  x! = x! + (i% + 1)\n  x! = x! + (i% + 1)\n  PRINT x!\nEND SUB";
    Assert.That(CountFiaddMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFiaddMem(Compile(staged, Dialect.Pb36))),
      "a signed-integer direct-cell operand is added to a float with FIADD m16; a staged operand uses FILD;FADDP");
  }

  private static int CountFiaddMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xDE or 0xDA && (image[i + 1] & 0x38) == 0 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenFloatTimesConstant_WhenPb36_ThenFpuConstantMemoryOperand() {
    const string mem = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  r! = a! * 1.5\n  r! = r! * 2.5\n  PRINT r!\nEND SUB";
    const string staged = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL n%)\ns 3\ns 5\nEND\nSUB s(BYVAL n%) NOINLINE\n  a! = n%\n  b! = n% + 1\n  r! = a! * (b! + b!)\n  r! = r! * (b! + b!)\n  PRINT r!\nEND SUB";
    Assert.That(CountFmulMem(Compile(mem, Dialect.Pb36)), Is.GreaterThan(CountFmulMem(Compile(staged, Dialect.Pb36))),
      "a float constant operand multiplies via an FPU memory operand (FMUL qword [f_n]); an expression operand uses FMULP");
  }

  private static int CountFmulMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] is 0xD8 or 0xDC && (image[i + 1] & 0x38) == 0x08 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongOpWithDirectCellOperand_WhenPb36_ThenLoadsRightWithoutStaging() {
    const string mem = "DECLARE SUB s(BYVAL a AS LONG, BYVAL b AS LONG)\ns 7, 3\ns 100, 200\nEND\nSUB s(BYVAL a AS LONG, BYVAL b AS LONG) NOINLINE\n  r& = a AND b\n  r& = r OR b\n  r& = r XOR b\n  PRINT r&\nEND SUB";
    const string staged = "DECLARE SUB s(BYVAL a AS LONG, b AS LONG)\nDIM q AS LONG\nq = 3\ns 7, q\nq = 200\ns 100, q\nEND\nSUB s(BYVAL a AS LONG, b AS LONG) NOINLINE\n  r& = a AND b\n  r& = r OR b\n  r& = r XOR b\n  PRINT r&\nEND SUB";
    Assert.That(CountMovBxAx(Compile(mem, Dialect.Pb36)), Is.LessThan(CountMovBxAx(Compile(staged, Dialect.Pb36))),
      "a LONG direct-cell right operand loads into BX:CX from memory; a BYREF operand stages through MOV BX,AX");
  }

  [Test]
  public void Emit_GivenCompareConstant_WhenPb36_ThenFoldsToImmediate() {
    var pb36 = Compile(_TOUCH + "x% = 100\nT x%\ny% = (x% = 5)\nT y%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(CountMovBxAx(pb36), Is.Zero, "comparison against a constant should fold to CMP AX,imm");
  }

  private static int CountMovCxMem(byte[] image) {
    var count = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8B && (image[i + 1] & 0x38) == 0x08 && (image[i + 1] & 0xC0) != 0xC0)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenLongBitwiseConstant_WhenPb36_ThenFoldsToImmediatePairNoRegisterLoad() {
    var constMask = Compile(_TOUCHL + "a& = &H1234\nTL a&\nb& = a& AND 255\nTL b&\nEND" + _TOUCHL_END, Dialect.Pb36);
    var varMask = Compile(_TOUCHL + "a& = &H1234\nm& = 255\nTL a&\nTL m&\nb& = a& AND m&\nTL b&\nEND" + _TOUCHL_END, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovCxMem(constMask), Is.Zero, "a& AND 255 should fold to immediate pair ops, no high-word load into CX");
      Assert.That(CountMovCxMem(varMask), Is.GreaterThanOrEqualTo(1), "a& AND m& loads the second operand's high word into CX straight from memory (MOV CX,[m&+2])");
    });
  }

  [Test]
  public void Emit_GivenLongEqualsConstant_WhenPb36_ThenFoldsWithoutRegisterLoad() {
    var constEq = Compile(_TOUCHL + _TOUCH + "p& = 7\nTL p&\ny% = (p& = 123456)\nT y%\nEND" + _TOUCHL_END + _TOUCH_END, Dialect.Pb36);
    var varEq = Compile(_TOUCHL + _TOUCH + "p& = 7\nq& = 123456\nTL p&\nTL q&\ny% = (p& = q&)\nT y%\nEND" + _TOUCHL_END + _TOUCH_END, Dialect.Pb36);
    Assert.Multiple(() => {
      Assert.That(CountMovCxMem(constEq), Is.Zero, "p& = const should fold the comparand, no high-word load");
      Assert.That(CountMovCxMem(varEq), Is.GreaterThanOrEqualTo(1), "p& = q& loads the comparand's high word straight from memory (MOV CX,[q&+2])");
    });
  }

  [Test]
  public void Emit_GivenModularIncrementByOne_WhenPb36_ThenUsesIncNotAddImmediate() {
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
      hasOrAxAx |= pb36[i] == 0x09 && pb36[i + 1] == 0xC0;
      hasCmpAxZero |= pb36[i] == 0x3D && pb36[i + 1] == 0x00 && pb36[i + 2] == 0x00;
    }
    Assert.Multiple(() => {
      Assert.That(hasOrAxAx, Is.True, "x% = 0 should test via OR AX,AX");
      Assert.That(hasCmpAxZero, Is.False, "x% = 0 should not emit CMP AX,0");
    });
  }

  [Test]
  public void Emit_GivenMultiplyByZeroWithFunctionOperand_WhenPb36_ThenOperandStillEvaluated() {
    const string source = "DECLARE FUNCTION F%\nx% = F% * 0\nPRINT x%\nEND\nFUNCTION F%\n  PRINT \"SIDE-EFFECT-MARKER\"\n  F% = 7\nEND FUNCTION";
    var pb36 = Compile(source, Dialect.Pb36);
    Assert.That(Ascii(pb36), Does.Contain("SIDE-EFFECT-MARKER"));
  }

  #endregion

  #region O6b - induction-variable array store ($OPTIMIZE SPEED)

  private static int CountPointerStepByTwo(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x83 && image[i + 1] == 0xC3 && image[i + 2] == 0x02)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenArrayStoreForLoop_WhenPb36Speed_ThenFewerElementSizeMultiplies() {
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL m%)\ns 9\ns 7\nEND\nSUB s(BYVAL m%) NOINLINE\nDIM a%(0 TO 9)\n";
    const string tail = "\nPRINT a%(3)\nEND SUB";
    var affine = Compile(head + "FOR i% = 0 TO m%\n  a%(i%) = i%\nNEXT i%" + tail, Dialect.Pb36);
    var indirect = Compile(head + "DIM p%(0 TO 9)\nFOR i% = 0 TO m%\n  a%(p%(i%)) = i%\nNEXT i%" + tail, Dialect.Pb36);
    Assert.That(CountElementScaleByTwoAnyRegister(affine), Is.LessThan(CountElementScaleByTwoAnyRegister(indirect)),
      "O6b should walk the elements, dropping the per-iteration scale a non-affine subscript has to keep");
  }

  [Test]
  public void Execute_GivenArrayStoreForLoop_WhenPb36Speed_ThenCorrectValues() {
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
    Assert.That(CountPointerStepByTwo(speed), Is.Zero,
      "when expr references a%, O6b declines and no pointer stepping appears");
    Assert.That(CountElementScaleByTwo(speed), Is.GreaterThanOrEqualTo(CountElementScaleByTwo(plain)),
      "the per-iteration subscript scale is still there");
  }

  [Test]
  public void Emit_GivenArrayStoreWithBoundsCheck_WhenPb36Speed_ThenSameAsPlain() {
    const string body = """
      DIM a%(0 TO 9)
      FOR i% = 0 TO 9
        a%(i%) = i%
      NEXT i%
      PRINT a%(3)
      END
      """;
    var checked_ = Compile("$OPTIMIZE SPEED\n$ERROR BOUNDS ON\n" + body, Dialect.Pb36);
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
    var speed = Compile("$OPTIMIZE SPEED\n" + _TOUCH + "x% = 7\nT x%\ny% = x% \\ 10\nz% = x% MOD 10\nT y%\nT z%\nEND" + _TOUCH_END, Dialect.Pb36);
    Assert.That(CountIdivBx(speed), Is.Zero, "x% \\ 10 under SPEED should be a reciprocal multiply, no IDIV BX");
  }

  [Test]
  public void Emit_GivenPowerOfTwoDivides_WhenPb36_ThenIdivDisappears() {
    const string head = "INPUT a%\nPRINT a% \\ ";
    var powerOfTwo = Compile(head + "8\nPRINT a% MOD 8\nPRINT a% \\ 2\nPRINT a% MOD 2\nEND", Dialect.Pb36);
    var other = Compile(head + "3\nPRINT a% MOD 3\nPRINT a% \\ 5\nPRINT a% MOD 5\nEND", Dialect.Pb36);
    Assert.That(CountIdivBx(powerOfTwo), Is.LessThan(CountIdivBx(other)),
      "pb36 should shift/mask power-of-two \\ and MOD instead of IDIV BX; a divisor it cannot decompose keeps the divide");
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

  private static int CountElementScaleByTwo(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x6B && image[i + 1] == 0xC0 && image[i + 2] == 0x02)
        ++count;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xD1 && image[i + 1] == 0xE0)
        ++count;
    return count;
  }

  private static int CountElementScaleByTwoAnyRegister(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x6B && (image[i + 1] & 0xC0) == 0xC0 && image[i + 2] == 0x02)
        ++count;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0xD1 && image[i + 1] is >= 0xE0 and <= 0xE7)
        ++count;
    return count;
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenPb36Speed_ThenPerIterationImulRemoved() {
    const string head = "$OPTIMIZE SPEED\nDECLARE SUB s(BYVAL m%)\ns 10\ns 8\nEND\nSUB s(BYVAL m%) NOINLINE\nDIM a%(1 TO 10)\n";
    const string tail = "\nPRINT x%\nEND SUB";
    var affine = Compile(head + "DIM x%\nFOR i% = 1 TO m%\n  x% = a%(i%)\nNEXT i%" + tail, Dialect.Pb36);
    var indirect = Compile(head + "DIM p%(1 TO 10)\nDIM x%\nFOR i% = 1 TO m%\n  x% = a%(p%(i%))\nNEXT i%" + tail, Dialect.Pb36);
    Assert.That(CountElementScaleByTwoAnyRegister(affine), Is.LessThan(CountElementScaleByTwoAnyRegister(indirect)),
      "IVSR should eliminate the per-iteration subscript scale a non-affine subscript has to keep");
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenBoundsChecking_ThenNoIvsr() {
    const string body = "$ERROR BOUNDS ON\nDIM a%(1 TO 5)\nDIM x%\nFOR i% = 1 TO 5\n  x% = a%(i%)\nNEXT i%\nPRINT x%\nEND";
    var checked_ = Compile("$OPTIMIZE SPEED\n" + body, Dialect.Pb36);
    Assert.That(CountElementScaleByTwo(checked_), Is.GreaterThanOrEqualTo(1),
      "$ERROR BOUNDS ON must keep the address recomputation path (with the range check), not step a blind pointer");
  }

  [Test]
  public void Emit_GivenArrayReadLoop_WhenMultiStatementBody_ThenNoIvsr() {
    const string body = "$OPTIMIZE SPEED\nDIM a%(1 TO 5)\nDIM x%\nFOR i% = 1 TO 5\n  x% = a%(i%)\n  PRINT x%\nNEXT i%\nEND";
    var image = Compile(body, Dialect.Pb36);
    Assert.That(CountElementScaleByTwo(image), Is.GreaterThanOrEqualTo(1),
      "two-statement body must not trigger IVSR; the address IMUL must still appear per-iteration");
  }

  #endregion

  #region O14 - tail-call optimization

  [Test]
  public void Execute_GivenDeepTailRecursion_WhenPb36_ThenConstantStack() {
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
      Assert.That(out36, Is.EqualTo(" 21  7  57\n"));
      Assert.That(out36, Is.EqualTo(out35));
      Assert.That(pb36.Length, Is.LessThan(pb35.Length), "the inlined image sheds the call frame");
    });
  }

  [Test]
  public void Emit_GivenEveryCallInlines_WhenPb36_ThenProcedurePurged() {
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
    Assert.That(inlined.Length, Is.LessThan(kept.Length), "fully-inlined procedure should be purged from the image");
  }

  [Test]
  public void Emit_GivenTrivialTypeMethod_WhenPb36_ThenInlinedThroughByRefReceiverAndPurged() {
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
    Assert.That(output, Is.EqualTo(" 222\n"));
  }

  [Test]
  public void Emit_GivenIneligibleCallees_WhenPb36_ThenRealCallKept() {
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
    var nopRun = 0; var maxRun = 0;
    foreach (var b in image) { nopRun = b == 0x90 ? nopRun + 1 : 0; maxRun = System.Math.Max(maxRun, nopRun); }
    Assert.That(maxRun, Is.GreaterThan(0), "an alignment NOP pad should be present");
  }

  #endregion

  #region LICM - loop-invariant code motion ($OPTIMIZE SPEED)

  private static (int slots, int preheaderCount, int useMarks) RunLicmAnalysis(string source, bool checkedArithmetic = false) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var loop = model.MainBody.OfType<PowerBasic.Compiler.Syntax.Ast.ForStmt>().FirstOrDefault();
    Assert.That(loop, Is.Not.Null, "source must contain a FOR loop");
    var name = (PowerBasic.Compiler.Syntax.Ast.NameExpr)loop!.Variable;
    var counter = model.VariableBindings[name];
    var r = PowerBasic.Compiler.CodeGen.OptCommonSubexpr.AnalyzeLicm(loop.Body, counter, 0, checkedArithmetic, model);
    return (r.SlotCount, r.Preheader.Count, r.Marks.Values.Count(m => !m.IsDefine));
  }

  [Test]
  public void Licm_GivenBodyWithIfBlock_WhenAnalyzed_ThenUnconditionalInvariantsStillHoist() {
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
