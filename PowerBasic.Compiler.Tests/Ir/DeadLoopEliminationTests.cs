using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Deleting a counted loop nobody can observe - and, just as much a part of the contract, NOT
/// deleting one under <c>$OPTIMIZE SIZE</c>, where a busy-wait is taken at its word.
///
/// <para>
/// The gate is the interesting half. Every other pass here can be judged by "does the program still
/// print the same thing", and this one passes that test whether or not it should have fired: a delay
/// loop prints nothing either way. So the tests have to assert on the code, which is the one place in
/// this suite where that is the right question rather than a proxy for it.
/// </para>
/// </summary>
[TestFixture]
public sealed class DeadLoopEliminationTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>Lowers and optimizes exactly as <see cref="CodeGenerator"/> does, recovery sweeps and all.</summary>
  private static IrFunction Optimized(string source, bool forSpeed) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Recover();
    IrPassManager.Standard(forSpeed).RunOnModule(module!);
    Recover();
    IrPassManager.Standard(forSpeed).RunOnModule(module!);
    return module!.Functions.Single(fn => fn.Name == "main");

    void Recover() {
      foreach (var fn in module!.Functions)
        if (!fn.IsDeclaration)
          IntegerRecovery.Run(fn);
    }
  }

  /// <summary>True when some block branches to itself or to one before it - a back edge.</summary>
  private static bool HasLoop(IrFunction fn) {
    var order = new Dictionary<IrBasicBlock, int>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < fn.Blocks.Count; ++i)
      order[fn.Blocks[i]] = i;
    return fn.Blocks.Any(block => block.Terminator is { } terminator
        && terminator.Successors.Any(successor => order.TryGetValue(successor, out var to) && to <= order[block]));
  }

  private const string _CLOSED_ACCUMULATOR = """
    DIM i AS INTEGER
    DIM t AS INTEGER
    FOR i = 1 TO 400
      t = t + 2
    NEXT i
    PRINT t
    END
    """;

  private const string _DELAY_LOOP = """
    DIM i AS INTEGER
    PRINT "before"
    FOR i = 1 TO 30000
    NEXT i
    PRINT "after"
    END
    """;

  /// <summary>
  /// The loop <see cref="RecurrenceClosedForm"/> emptied. Its answer is already in the exit block;
  /// the four hundred iterations that produced it are what goes.
  /// </summary>
  [Test]
  public void EmptiedLoop_WhenOptimizingForSpeed_ThenTheIterationsAreGone() {
    var main = Optimized(_CLOSED_ACCUMULATOR, forSpeed: true);
    Assert.That(HasLoop(main), Is.False, "the loop should be gone:\n" + IrPrinter.Print(main));
    Assert.That(IrPrinter.Print(main), Does.Contain("i16 800"), "and its answer should remain");
  }

  [Test]
  public void EmptiedLoop_WhenOptimizingForSize_ThenItIsLeftAlone() {
    var main = Optimized(_CLOSED_ACCUMULATOR, forSpeed: false);
    Assert.That(HasLoop(main), Is.True, "without SPEED the loop stays:\n" + IrPrinter.Print(main));
  }

  /// <summary>
  /// A delay loop is the same shape written on purpose, so under SPEED it goes too. That is the
  /// trade the gate exists to make, and it is asserted rather than left implied.
  /// </summary>
  [Test]
  public void DelayLoop_WhenOptimizingForSpeed_ThenItGoesToo() {
    Assert.That(HasLoop(Optimized(_DELAY_LOOP, forSpeed: true)), Is.False);
    Assert.That(HasLoop(Optimized(_DELAY_LOOP, forSpeed: false)), Is.True, "SIZE must keep the wait");
  }

  /// <summary>
  /// Unswitching gives the overflow and non-overflow loops one conditional preheader. Deleting the
  /// empty clone must not replace that chooser and make the clone containing <c>rt_error</c>
  /// unreachable.
  /// </summary>
  [Test]
  public void UnswitchClone_GivenTheOtherCloneRaises_ThenTheSharedPreheaderIsNotRetargeted() {
    var main = Optimized("""
      $ERROR OVERFLOW ON
      INPUT k%
      FOR i% = 1 TO 100
        x% = k% + 1
      NEXT i%
      PRINT "survived"
      END
      """, forSpeed: true);

    Assert.That(main.AllInstructions.OfType<IrCall>()
      .Any(call => (call.Callee as IrFunction)?.Name == "rt_error"), Is.True,
      "the optimized IR must retain the loop-invariant Error 6 path:\n" + IrPrinter.Print(main));
  }

  /// <summary>
  /// The loops that must survive SPEED: one whose body prints, and one whose counter is read after
  /// it. Neither is unobservable, and a pass that deleted either would still pass a test that only
  /// looked at delay loops.
  /// </summary>
  [TestCase("printing body", """
    DIM i AS INTEGER
    FOR i = 1 TO 20
      PRINT i;
    NEXT i
    PRINT
    END
    """)]
  [TestCase("counter read after", """
    DIM i AS INTEGER
    FOR i = 1 TO 20
    NEXT i
    PRINT i
    END
    """)]
  [TestCase("array written", """
    DIM a(0 TO 19) AS INTEGER
    DIM i AS INTEGER
    FOR i = 0 TO 19
      a(i) = i * 3
    NEXT i
    PRINT a(7)
    END
    """)]
  public void ObservableLoop_WhenOptimizingForSpeed_ThenItSurvives(string name, string source) {
    var main = Optimized(source, forSpeed: true);
    Assert.That(HasLoop(main) || IrPrinter.Print(main).Contains("rt_print"), Is.True,
      $"'{name}' lost its effect:\n" + IrPrinter.Print(main));
  }

  /// <summary>
  /// The loop this pass may see is not always entered unconditionally, and the preheader's OTHER edge
  /// is not this pass's to spend.
  ///
  /// <para>
  /// <c>$ERROR OVERFLOW ON</c> puts a check in the loop body that does not depend on the counter, so
  /// LICM hoists it and <see cref="LoopUnswitch"/> clones the loop on it: one copy that traps every
  /// iteration and one that does nothing at all, chosen by a <c>condbr</c> in the preheader. The empty
  /// copy is genuinely dead and deleting it is right - but rewiring the preheader by REPLACING its
  /// terminator takes the branch with it, and the trapping copy becomes unreachable. The program then
  /// runs to completion instead of raising Error 6, which is a silent miscompile: the whole point of
  /// arming the check is that it fires.
  /// </para>
  /// </summary>
  [Test]
  public void UnswitchedLoop_WhenTheEmptyCloneGoes_ThenTheTrappingCloneIsStillReached() {
    var main = Optimized("""
      $ERROR OVERFLOW ON
      INPUT k%
      FOR i% = 1 TO 100
        x% = k% + 1
      NEXT i%
      PRINT "done"
      END
      """, forSpeed: true);

    Assert.That(IrVerifier.Verify(main), Is.Empty);
    Assert.That(main.AllInstructions.OfType<IrCall>().Any(call =>
        (call.Callee as IrFunction)?.Name == "rt_error"
        && call.Args.FirstOrDefault() is IrConstantInt { Value: 6 }), Is.True,
      "the Error 6 raise must survive:\n" + IrPrinter.Print(main));
  }

  /// <summary>
  /// A loop is not unobservable just because its body writes nothing: the body can LEAVE. An
  /// <c>EXIT SUB</c> inside the loop lowers to a <c>ret</c> in the middle of the region, and
  /// <c>CountedLoop.CollectRegion</c> absorbs that block without complaint because a <c>ret</c> has no
  /// successors to walk. Deleting the region deleted the early return with it, so a procedure that had
  /// to leave on the first iteration ran on to its final <c>PRINT</c> instead.
  ///
  /// <para>
  /// Asserted on the emitted program rather than on the shape, because the whole defect is that the
  /// shape looked fine: it produced a well-formed function that returned down the wrong path.
  /// </para>
  /// </summary>
  [Test]
  public void LoopWithAnEarlyReturn_WhenOptimizingForSpeed_ThenTheReturnSurvives() {
    const string source = """
      $OPTIMIZE SPEED
      DECLARE FUNCTION Given%(BYVAL v%)
      DECLARE SUB Walk(BYVAL n%)
      Walk Given%(2)
      Walk Given%(7)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      SUB Walk(BYVAL n%) NOINLINE
        DIM i AS INTEGER
        FOR i = 1 TO 10
          IF n% > 5 THEN
            EXIT SUB
          END IF
        NEXT i
        PRINT "done"; n%
      END SUB
      """;

    Assert.That(RunBothWays(source), Is.EqualTo("done 2"),
      "n = 7 leaves on the first iteration, so only the n = 2 call may print");
  }

  /// <summary>
  /// The same rule from the other side: <c>EXIT LOOP</c> is a second edge from inside the region to
  /// the exit block, and both the deletion and <see cref="LoopUnswitch"/>'s clone-and-rewire assume
  /// the header's edge is the only one. Unswitching appended LCSSA phis with exactly two incomings -
  /// one per cloned header - and left the break's own incoming naming a block the rewrite had deleted,
  /// so the exit carried a phi with no incomings at all: <c>i</c> read back as 0 for every input, on
  /// IR the verifier rejects.
  /// </summary>
  [Test]
  public void LoopWithABreakToItsExit_WhenOptimizingForSpeed_ThenTheCounterIsWhatTheBreakLeft() {
    const string source = """
      $OPTIMIZE SPEED
      DECLARE FUNCTION Given%(BYVAL v%)
      DECLARE SUB Walk(BYVAL n%)
      Walk Given%(2)
      Walk Given%(7)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      SUB Walk(BYVAL n%) NOINLINE
        DIM i AS INTEGER
        i = 0
        WHILE i < 10
          i = i + 1
          IF n% > 5 THEN
            EXIT LOOP
          END IF
        WEND
        PRINT "done"; n%; i
      END SUB
      """;

    Assert.That(RunBothWays(source), Is.EqualTo("done 2  10 |done 7  1"),
      "the loop runs out for n = 2 and breaks on the first iteration for n = 7");
  }

  /// <summary>
  /// Runs the program through BOTH back ends and asserts they agree, answering with what they printed.
  /// The routed path is where these two defects lived; the direct emitter is the reference.
  /// </summary>
  private static string RunBothWays(string source) {
    var direct = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));
    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("Walk"), "the back end did not take the procedure under test");
    var directOutput = Cpu8086.Run(directImage).Output.Trim().Replace("\r\n", "|");
    Assert.That(Cpu8086.Run(routedImage).Output.Trim().Replace("\r\n", "|"), Is.EqualTo(directOutput));
    return directOutput;
  }

  /// <summary>
  /// And whatever it deletes, the program still prints what it printed. Rendered back to BASIC and
  /// run, because deleting code is supposed to change the code.
  /// </summary>
  [TestCase(_CLOSED_ACCUMULATOR)]
  [TestCase(_DELAY_LOOP)]
  [TestCase("""
    DIM i AS INTEGER
    DIM s AS INTEGER
    FOR i = 1 TO 5
      s = s + i
    NEXT i
    PRINT s
    END
    """)]
  public void Deleted_GivenTheProgramIsRun_ThenItStillPrintsTheSame(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    foreach (var fn in module!.Functions)
      if (!fn.IsDeclaration)
        IntegerRecovery.Run(fn);
    IrPassManager.Standard(optimizeForSpeed: true).RunOnModule(module);
    Assert.That(Run(IrBasicWriter.Write(module)), Is.EqualTo(Run(source)));
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }
}
