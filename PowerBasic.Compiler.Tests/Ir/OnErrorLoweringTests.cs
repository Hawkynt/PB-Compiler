using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>ON ERROR</c> / <c>RESUME</c> in the IR lowering.
///
/// PB's error handling is the one construct whose control flow a CFG cannot express: arming a handler
/// writes a code address into a runtime cell, and a fault anywhere afterwards - including inside a
/// runtime routine, where this compiler emitted no instruction at all - lands on it. The edge is real
/// but invisible.
///
/// So the tests here are as much about what the lowering REFUSES to let happen as about what it
/// builds. A function that arms a handler is marked <see cref="IrFunction.HasErrorHandler"/> and the
/// optimizer must decline to touch it: every pass in the pipeline reasons from the CFG, and on this
/// function the CFG lies. The failure mode if that guarantee slips is the worst kind - the handler
/// looks unreachable and is deleted, or a variable the handler reads is constant-folded to the value
/// that reaches the fall-through edge. Neither shows up as a crash.
/// </summary>
[TestFixture]
public sealed class OnErrorLoweringTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IrFunction MainOf(IrModule m) => m.FindFunction("main")!;

  private static IEnumerable<IrCall> Calls(IrFunction f)
    => f.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>();

  private static bool CallsRuntime(IrFunction f, string name)
    => Calls(f).Any(c => (c.Callee as IrFunction)?.Name == name);

  private const string _handled = """
    ON ERROR GOTO Trap
    a% = 1
    ERROR 6
    PRINT "after"
    GOTO Done
    Trap:
    PRINT "trapped"; ERR
    RESUME Done
    Done:
    END
    """;

  [Test]
  public void Lower_GivenOnErrorGoto_ThenArmsTheHandlerWithItsBlockAddress() {
    var main = MainOf(Lower(_handled));

    var arm = Calls(main).FirstOrDefault(c => (c.Callee as IrFunction)?.Name == "rt_onerr_arm");
    Assert.That(arm, Is.Not.Null, "ON ERROR GOTO has to arm something");
    Assert.That(arm!.Args.Single(), Is.InstanceOf<IrBlockAddress>(), "the handler is named by its address");
    var target = ((IrBlockAddress)arm.Args.Single()).Block;
    Assert.That(main.Blocks, Does.Contain(target), "and that block belongs to this function");
  }

  /// <summary>
  /// The guarantee the rest of the correctness argument rests on. If a pass ever runs over one of
  /// these functions it will be reasoning from a graph that is missing the fault edge.
  /// </summary>
  [Test]
  public void Optimize_GivenAFunctionWithAHandler_ThenNoPassTouchesIt() {
    var module = Lower(_handled);
    var main = MainOf(module);
    Assert.That(main.HasErrorHandler, Is.True, "arming a handler has to mark the function");

    var before = main.Blocks.Select(b => (b.Label, Count: b.Instructions.Count)).ToList();
    IrPassManager.Standard().RunOnModule(module);

    Assert.That(MainOf(module).Blocks.Select(b => (b.Label, Count: b.Instructions.Count)), Is.EqualTo(before),
      "the optimizer changed a function whose control flow it cannot see");
  }

  [Test]
  public void Lower_GivenAHandlerBlock_ThenItSurvivesEvenWithNoVisiblePredecessor() {
    // the handler is reached ONLY by a fault here - nothing branches or falls into it
    var module = Lower("""
      ON ERROR GOTO Trap
      ERROR 6
      END
      Trap:
      PRINT "trapped"
      END
      """);
    IrPassManager.Standard().RunOnModule(module);

    var arm = Calls(MainOf(module)).First(c => (c.Callee as IrFunction)?.Name == "rt_onerr_arm");
    var handler = ((IrBlockAddress)arm.Args.Single()).Block;
    Assert.That(MainOf(module).Blocks, Does.Contain(handler), "a block only a fault reaches is still live");
    Assert.That(handler.Instructions, Is.Not.Empty);
  }

  [Test]
  public void Lower_GivenOnErrorGotoZero_ThenDisarms() {
    Assert.That(CallsRuntime(MainOf(Lower("""
      ON ERROR GOTO Trap
      ON ERROR GOTO 0
      PRINT "x"
      END
      Trap:
      END
      """)), "rt_onerr_disarm"), Is.True);
  }

  [Test]
  public void Lower_GivenResumeToALabel_ThenItIsAnOrdinaryBranch() {
    var main = MainOf(Lower(_handled));

    Assert.That(CallsRuntime(main, "rt_err_clear"), Is.True, "RESUME clears ERR on the way out");
    // RESUME <label> names its destination, so it costs no per-statement bookkeeping at all
    Assert.That(CallsRuntime(main, "rt_resume_mark"), Is.False);
  }

  /// <summary>
  /// <c>RESUME NEXT</c> goes back to a statement the FAULT chose, not one the source names - so each
  /// statement has to publish where it begins and where the next one does. That is also why it cannot
  /// be an IR branch: the destination is a value in a runtime cell.
  /// </summary>
  [Test]
  public void Lower_GivenResumeNext_ThenEveryStatementPublishesItsBoundaries() {
    var main = MainOf(Lower("""
      ON ERROR GOTO Trap
      a% = 1
      ERROR 6
      PRINT "after"
      END
      Trap:
      RESUME NEXT
      """));

    var marks = Calls(main).Where(c => (c.Callee as IrFunction)?.Name == "rt_resume_mark").ToList();
    Assert.That(marks, Is.Not.Empty, "RESUME NEXT needs statement boundaries to resume at");
    Assert.That(marks.All(m => m.Args.Count() == 2 && m.Args.All(a => a is IrBlockAddress)),
      "each boundary is a pair of block addresses - this statement's start and the next one's");
    Assert.That(CallsRuntime(main, "rt_resume_next"), Is.True);
  }

  [Test]
  public void Lower_GivenOnErrorResumeNext_ThenArmsInlineModeAndTracksBoundaries() {
    var main = MainOf(Lower("""
      ON ERROR RESUME NEXT
      ERROR 6
      PRINT "kept going"
      ON ERROR GOTO 0
      END
      """));

    Assert.That(CallsRuntime(main, "rt_onerr_resume_next"), Is.True);
    Assert.That(CallsRuntime(main, "rt_resume_mark"), Is.True);
  }

  [Test]
  public void Lower_GivenErrAndErl_ThenTheyReadTheRuntimeCells() {
    var main = MainOf(Lower("""
      ON ERROR GOTO Trap
      ERROR 6
      END
      Trap:
      PRINT ERR; ERL
      END
      """));

    var loaded = main.Blocks.SelectMany(b => b.Instructions).OfType<IrLoad>()
      .Select(l => (l.Pointer as IrGlobalVariable)?.Name).ToList();
    Assert.That(loaded, Does.Contain("rt_err"));
    Assert.That(loaded, Does.Contain("rt_erl"));
  }

  [Test]
  public void Lower_GivenErrclear_ThenTheErrorCellIsZeroed() {
    var main = MainOf(Lower("""
      ON ERROR GOTO Trap
      ERROR 6
      ERRCLEAR
      PRINT ERR
      END
      Trap:
      RESUME NEXT
      """));

    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrStore>()
      .Any(s => (s.Pointer as IrGlobalVariable)?.Name == "rt_err" && s.Value is IrConstantInt { Value: 0 }));
  }

  /// <summary>
  /// A program with no error handling must not pay for any of this - no marking, no boundaries, and
  /// above all no loss of optimization.
  /// </summary>
  [Test]
  public void Lower_GivenNoErrorHandling_ThenNothingIsMarkedAndTheOptimizerStillRuns() {
    var module = Lower("""
      a% = 1
      b% = a% + 1
      PRINT b%
      """);
    var main = MainOf(module);

    Assert.That(main.HasErrorHandler, Is.False);
    Assert.That(CallsRuntime(main, "rt_resume_mark"), Is.False);
    Assert.That(IrPassManager.Standard().Run(main), Is.GreaterThan(0), "an ordinary function is still optimized");
  }
}
