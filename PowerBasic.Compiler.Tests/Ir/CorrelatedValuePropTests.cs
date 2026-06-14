using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Correlated value propagation: facts from <c>if (x == C)</c> flow into the guarded region.</summary>
[TestFixture]
public sealed class CorrelatedValuePropTests {

  /// <summary>entry: if x==5 goto t else e ; t: r = x + 10 ; ret r ; e: ret x.</summary>
  private static (IrFunction Fn, IrBinary Add) BuildGuarded() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var entry = fn.CreateBlock("entry");
    var t = fn.CreateBlock("t");
    var e = fn.CreateBlock("e");
    var be = new IrBuilder(entry);
    be.CondBr(be.Cmp(IrCmpPred.Eq, x, IrBuilder.ConstI32(5)), t, e);
    var bt = new IrBuilder(t);
    var add = bt.Add(x, IrBuilder.ConstI32(10));
    bt.Ret(add);
    new IrBuilder(e).Ret(x);
    return (fn, add);
  }

  [Test]
  public void Run_ReplacesTheGuardedVariableWithTheConstant() {
    var (fn, add) = BuildGuarded();

    var changed = CorrelatedValueProp.Run(fn);

    Assert.That(changed, Is.GreaterThanOrEqualTo(1));
    Assert.That(add.Lhs, Is.InstanceOf<IrConstantInt>());          // x became 5 inside the guard
    Assert.That(((IrConstantInt)add.Lhs).Value, Is.EqualTo(5));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_ThenInstCombine_FoldsTheGuardedExpression() {
    var (fn, _) = BuildGuarded();

    CorrelatedValueProp.Run(fn);
    InstCombine.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 15"));   // 5 + 10
  }

  [Test]
  public void Run_DoesNotPropagateIntoTheElseSide() {
    // e returns x unchanged - the equality fact only holds on the true edge
    var (fn, _) = BuildGuarded();

    CorrelatedValueProp.Run(fn);

    var e = fn.Blocks.First(b => b.Label == "e");
    Assert.That(((IrRet)e.Terminator!).Value, Is.InstanceOf<IrArgument>());   // still x, not a constant
  }
}
