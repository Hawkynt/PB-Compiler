using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Control-flow safety regressions for O0350 overflow-check coalescing.</summary>
[TestFixture]
public sealed class O0350OverflowCheckCoalescingTests {

  [Test]
  public void GivenContinuationReachableWithoutFirstGuard_WhenCoalescing_ThenChecksStaySeparate() {
    var chooseGuard = new IrArgument(IrType.I1, 0, "chooseGuard");
    var firstOverflow = new IrArgument(IrType.I1, 1, "firstOverflow");
    var secondOverflow = new IrArgument(IrType.I1, 2, "secondOverflow");
    var fn = new IrFunction("f", IrType.Void, [chooseGuard, firstOverflow, secondOverflow]);
    var error = new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var entry = fn.CreateBlock("entry");
    var guarded = fn.CreateBlock("guarded");
    var bypass = fn.CreateBlock("bypass");
    var firstTrap = fn.CreateBlock("first.trap");
    var middle = fn.CreateBlock("middle");
    var secondTrap = fn.CreateBlock("second.trap");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).CondBr(chooseGuard, guarded, bypass);
    new IrBuilder(guarded).CondBr(firstOverflow, firstTrap, middle);
    new IrBuilder(bypass).Br(middle);
    var firstTrapBuilder = new IrBuilder(firstTrap);
    firstTrapBuilder.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    firstTrapBuilder.Br(middle);
    new IrBuilder(middle).CondBr(secondOverflow, secondTrap, exit);
    var secondTrapBuilder = new IrBuilder(secondTrap);
    secondTrapBuilder.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    secondTrapBuilder.Br(exit);
    new IrBuilder(exit).Ret();

    var changed = OverflowCheckCoalescing.Run(fn);

    Assert.That(changed, Is.Zero);
    Assert.That(guarded.Terminator, Is.TypeOf<IrCondBr>());
    Assert.That(((IrCondBr)middle.Terminator!).Condition, Is.SameAs(secondOverflow));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void GivenFirstTrapSharedByAnotherPath_WhenCoalescing_ThenChecksStaySeparateAndSsaRemainsValid() {
    var chooseGuard = new IrArgument(IrType.I1, 0, "chooseGuard");
    var x = new IrArgument(IrType.I16, 1, "x");
    var secondOverflow = new IrArgument(IrType.I1, 2, "secondOverflow");
    var fn = new IrFunction("f", IrType.Void, [chooseGuard, x, secondOverflow]);
    var error = new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var entry = fn.CreateBlock("entry");
    var guarded = fn.CreateBlock("guarded");
    var other = fn.CreateBlock("other");
    var firstTrap = fn.CreateBlock("first.trap");
    var middle = fn.CreateBlock("middle");
    var secondTrap = fn.CreateBlock("second.trap");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).CondBr(chooseGuard, guarded, other);
    var guardedBuilder = new IrBuilder(guarded);
    var firstOverflow = guardedBuilder.Cmp(IrCmpPred.Eq, x, new IrConstantInt(IrType.I16, 0));
    guardedBuilder.CondBr(firstOverflow, firstTrap, middle);
    new IrBuilder(other).Br(firstTrap);
    var firstTrapBuilder = new IrBuilder(firstTrap);
    firstTrapBuilder.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    firstTrapBuilder.Br(middle);
    new IrBuilder(middle).CondBr(secondOverflow, secondTrap, exit);
    var secondTrapBuilder = new IrBuilder(secondTrap);
    secondTrapBuilder.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    secondTrapBuilder.Br(exit);
    new IrBuilder(exit).Ret();

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var changed = OverflowCheckCoalescing.Run(fn);

    Assert.That(changed, Is.Zero);
    Assert.That(guarded.Terminator, Is.TypeOf<IrCondBr>());
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
