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
