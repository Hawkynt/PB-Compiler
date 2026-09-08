using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0352ConversionRangeCheckElimTests {

  [Test]
  public void Run_GivenAffineFloatFromBoundedInteger_WhenConversionGuardCannotFire_ThenBothBoundsAreFolded() {
    var value = new IrArgument(IrType.U8, 0, "value");
    var fn = new IrFunction("f", IrType.Void, [value]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("trap");
    var exit = fn.CreateBlock("exit");
    var be = new IrBuilder(entry);
    var floating = be.Cast(IrCastOp.UIToFP, value, IrType.F64);
    var scaled = be.Binary(IrBinaryOp.FMul, floating, IrBuilder.ConstFloat(IrType.F64, 0.5));
    var below = be.Cmp(IrCmpPred.Folt, scaled, IrBuilder.ConstFloat(IrType.F64, -0.5));
    var above = be.Cmp(IrCmpPred.Fogt, scaled, IrBuilder.ConstFloat(IrType.F64, 255.5));
    var outside = be.Or(below, above);
    be.CondBr(outside, trap, exit);
    new IrBuilder(trap).Br(exit);
    new IrBuilder(exit).Ret();

    var changed = ConversionRangeCheckElim.Run(fn);

    Assert.That(changed, Is.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(outside.Lhs, Is.InstanceOf<IrConstantInt>());
      Assert.That(((IrConstantInt)outside.Lhs).Value, Is.Zero);
      Assert.That(outside.Rhs, Is.InstanceOf<IrConstantInt>());
      Assert.That(((IrConstantInt)outside.Rhs).Value, Is.Zero);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenRoundedAffineDomain_WhenConversionGuardCannotFire_ThenComparisonIsFolded() {
    var value = new IrArgument(IrType.U8, 0, "value");
    var fn = new IrFunction("f", IrType.Void, [value]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("trap");
    var exit = fn.CreateBlock("exit");
    var be = new IrBuilder(entry);
    var wide = be.Cast(IrCastOp.UIToFP, value, IrType.F64);
    var shifted = be.Binary(IrBinaryOp.FAdd, wide, IrBuilder.ConstFloat(IrType.F64, 0.25));
    var rounded = be.Cast(IrCastOp.FPTrunc, shifted, IrType.F32);
    var comparison = be.Cmp(IrCmpPred.Fogt, rounded, IrBuilder.ConstFloat(IrType.F32, 255.5));
    be.CondBr(comparison, trap, exit);
    new IrBuilder(trap).Br(exit);
    new IrBuilder(exit).Ret();

    var changed = ConversionRangeCheckElim.Run(fn);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(((IrCondBr)entry.Terminator!).Condition, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrCondBr)entry.Terminator!).Condition).Value, Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenAffineDomainThatCrossesConversionBoundary_WhenRun_ThenGuardIsPreserved() {
    var value = new IrArgument(IrType.U8, 0, "value");
    var fn = new IrFunction("f", IrType.Void, [value]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("trap");
    var exit = fn.CreateBlock("exit");
    var be = new IrBuilder(entry);
    var floating = be.Cast(IrCastOp.UIToFP, value, IrType.F64);
    var scaled = be.Binary(IrBinaryOp.FMul, floating, IrBuilder.ConstFloat(IrType.F64, 2.0));
    var comparison = be.Cmp(IrCmpPred.Fogt, scaled, IrBuilder.ConstFloat(IrType.F64, 255.5));
    be.CondBr(comparison, trap, exit);
    new IrBuilder(trap).Br(exit);
    new IrBuilder(exit).Ret();

    Assert.That(ConversionRangeCheckElim.Run(fn), Is.Zero);
    Assert.That(((IrCondBr)entry.Terminator!).Condition, Is.SameAs(comparison));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
