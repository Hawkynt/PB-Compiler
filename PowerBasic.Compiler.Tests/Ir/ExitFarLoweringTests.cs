using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>EXIT FAR</c> in the IR lowering.
///
/// PB's second non-local jump, and the one whose name argues for the wrong reading: it is not a far
/// RETURN and pops nothing. <c>EXIT FAR AT label</c> records a target offset plus the SP and BP of the
/// frame it belongs to; a bare <c>EXIT FAR</c> at any call depth afterwards puts that frame back and
/// jumps, abandoning everything in between without unwinding it.
///
/// So it inherits <c>ON ERROR</c>'s two structural obligations, and the tests are about those rather
/// than about instruction counts. Arming must be an INTRINSIC the back end expands in place, because
/// it captures the caller's own SP and BP and a real call would capture the callee's; and the function
/// that arms must be marked <see cref="IrFunction.HasErrorHandler"/>, because the block it names is
/// entered by an edge the CFG does not have. Without the mark a pass may delete the landing block as
/// unreachable, or promote a variable to a register the landing would find holding something else -
/// neither of which looks like a failure until the program runs.
/// </summary>
[TestFixture]
public sealed class ExitFarLoweringTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IEnumerable<IrCall> Calls(IrFunction f)
    => f.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>();

  private const string _unwinds = """
    DECLARE SUB Leave()
    EXIT FAR AT Home
    Leave
    PRINT "not reached"
    Home:
    PRINT "home"
    END
    SUB Leave()
      EXIT FAR
    END SUB
    """;

  [Test]
  public void Lower_GivenExitFarAt_ThenArmsTheUnwindPointWithItsBlockAddress() {
    var main = Lower(_unwinds).FindFunction("main")!;

    var arm = Calls(main).FirstOrDefault(c => (c.Callee as IrFunction)?.Name == "rt_efar_arm");
    Assert.That(arm, Is.Not.Null, "EXIT FAR AT has to arm something");
    Assert.That(arm!.Args.Single(), Is.InstanceOf<IrBlockAddress>(), "the landing point is named by its address");
    Assert.That(main.Blocks, Does.Contain(((IrBlockAddress)arm.Args.Single()).Block),
      "and that block belongs to the function that armed it");
  }

  /// <summary>
  /// The bare form transfers control and does not come back, so what follows it in the same block is
  /// unreachable - spelled as such rather than left to fall through into the next statement.
  /// </summary>
  [Test]
  public void Lower_GivenABareExitFar_ThenItIsATransferThatDoesNotReturn() {
    var leave = Lower(_unwinds).FindFunction("Leave")!;

    var go = Calls(leave).FirstOrDefault(c => (c.Callee as IrFunction)?.Name == "rt_efar_go");
    Assert.That(go, Is.Not.Null, "a bare EXIT FAR has to jump through the armed cell");
    Assert.That(go!.Args, Is.Empty, "it takes no argument - the target is the cell the arm wrote");
    Assert.That(go.Parent!.Instructions.Last(), Is.InstanceOf<IrUnreachable>(),
      "nothing after it in the block can run");
  }

  /// <summary>
  /// The guarantee everything else rests on: the function holding the landing point is off-limits to
  /// every CFG-based pass, because on it the CFG is missing its most important edge.
  /// </summary>
  [Test]
  public void Optimize_GivenAFunctionThatArmsAnUnwindPoint_ThenNoPassTouchesIt() {
    var module = Lower(_unwinds);
    var main = module.FindFunction("main")!;
    Assert.That(main.HasErrorHandler, Is.True, "arming an unwind point has to mark the function");

    var before = main.Blocks.Select(b => (b.Label, Count: b.Instructions.Count)).ToList();
    IrPassManager.Standard().RunOnModule(module);

    Assert.That(module.FindFunction("main")!.Blocks.Select(b => (b.Label, Count: b.Instructions.Count)),
      Is.EqualTo(before), "the optimizer changed a function whose control flow it cannot see");
  }

  /// <summary>
  /// A landing point nothing branches to survives. Here the arm names a label that only the unwind can
  /// reach, so a reachability pass reading the CFG alone would delete it.
  /// </summary>
  [Test]
  public void Lower_GivenALandingPointNothingBranchesTo_ThenItSurvivesOptimization() {
    var module = Lower("""
      DECLARE SUB Leave()
      EXIT FAR AT Home
      Leave
      END
      Home:
      PRINT "home"
      END
      SUB Leave()
        EXIT FAR
      END SUB
      """);
    IrPassManager.Standard().RunOnModule(module);

    var main = module.FindFunction("main")!;
    var arm = Calls(main).First(c => (c.Callee as IrFunction)?.Name == "rt_efar_arm");
    var landing = ((IrBlockAddress)arm.Args.Single()).Block;
    Assert.That(main.Blocks, Does.Contain(landing), "a block only the unwind reaches is still live");
    Assert.That(landing.Instructions, Is.Not.Empty);
  }

  /// <summary>
  /// Why the lowering never has to reconcile an <c>AT</c> label with another procedure's blocks: a
  /// label reference resolves in the procedure it is written in, so naming one from elsewhere is a
  /// FRONT END error and no module is ever built. The lowering keeps a guard for it anyway, but this
  /// is the reason the guard is unreachable rather than merely untested.
  /// </summary>
  [Test]
  public void Bind_GivenExitFarAtALabelInAnotherProcedure_ThenTheFrontEndRejectsIt() {
    var source = """
      DECLARE SUB Arm()
      Arm
      Home:
      END
      SUB Arm()
        EXIT FAR AT Home
      END SUB
      """;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);

    Assert.That(model.Errors.Select(e => e.Message), Has.Some.Contains("undefined label Home"),
      "EXIT FAR AT can only name a label of its own procedure");
  }
}
