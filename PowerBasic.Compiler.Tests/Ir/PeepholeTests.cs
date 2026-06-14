using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Additional sound InstCombine peephole identities.</summary>
[TestFixture]
public sealed class PeepholeTests {

  private static IrFunction OneArg(IrType type, Func<IrBuilder, IrArgument, IrValue> build) {
    var x = new IrArgument(type, 0, "x");
    var fn = new IrFunction("f", type, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(build(b, x));
    return fn;
  }

  private static IrFunction TwoArg(Func<IrBuilder, IrArgument, IrArgument, IrValue> build) {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.I32, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(build(b, x, y));
    return fn;
  }

  private static string Optimized(IrFunction fn) {
    InstCombine.Run(fn);
    Dce.Run(fn);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    return IrPrinter.Print(fn);
  }

  [Test]
  public void AddXX_BecomesShiftLeftOne() =>
    Assert.That(Optimized(OneArg(IrType.I32, (b, x) => b.Add(x, x))), Does.Contain("shl i32 %x, 1"));

  [Test]
  public void MulByMinusOne_BecomesNegate() {
    var text = Optimized(OneArg(IrType.I32, (b, x) => b.Mul(x, new IrConstantInt(IrType.I32, -1))));
    Assert.That(text, Does.Contain("sub i32 0, %x"));
    Assert.That(text, Does.Not.Contain("mul"));
  }

  [Test]
  public void DoubleNegate_CancelsOut() {
    // 0 - (0 - x) -> x
    var text = Optimized(OneArg(IrType.I32, (b, x) => b.Sub(IrBuilder.ConstI32(0), b.Sub(IrBuilder.ConstI32(0), x))));
    Assert.That(text, Does.Contain("ret i32 %x"));
    Assert.That(text, Does.Not.Contain("sub"));
  }

  [Test]
  public void AddThenSubtractSameOperand_Cancels() {
    // (x + y) - x -> y
    var text = Optimized(TwoArg((b, x, y) => b.Sub(b.Add(x, y), x)));
    Assert.That(text, Does.Contain("ret i32 %y"));
  }

  [Test]
  public void AndAbsorbsOr() {
    // x & (x | y) -> x
    var text = Optimized(TwoArg((b, x, y) => b.And(x, b.Or(x, y))));
    Assert.That(text, Does.Contain("ret i32 %x"));
    Assert.That(text, Does.Not.Contain("and"));
  }

  [Test]
  public void OrAbsorbsAnd() {
    // x | (x & y) -> x
    var text = Optimized(TwoArg((b, x, y) => b.Or(x, b.And(x, y))));
    Assert.That(text, Does.Contain("ret i32 %x"));
    Assert.That(text, Does.Not.Contain("or"));
  }
}
