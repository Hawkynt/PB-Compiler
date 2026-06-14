using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>InstCombine strength reduction: power-of-two multiply/divide/remainder become shifts and masks.</summary>
[TestFixture]
public sealed class StrengthReductionTests {

  private static IrFunction Unary(IrType type, Func<IrBuilder, IrArgument, IrValue> build) {
    var x = new IrArgument(type, 0, "x");
    var fn = new IrFunction("f", type, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(build(b, x));
    return fn;
  }

  [Test]
  public void Mul_ByPowerOfTwo_BecomesShiftLeft() {
    var fn = Unary(IrType.I32, (b, x) => b.Mul(x, IrBuilder.ConstI32(8)));   // x * 8 -> x << 3

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("shl i32 %x, 3"));
    Assert.That(text, Does.Not.Contain("mul"));
  }

  [Test]
  public void UnsignedDivide_ByPowerOfTwo_BecomesLogicalShiftRight() {
    var fn = Unary(IrType.I32, (b, x) => b.Binary(IrBinaryOp.UDiv, x, IrBuilder.ConstI32(16)));  // x / 16 -> x >>> 4

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("lshr i32 %x, 4"));
  }

  [Test]
  public void UnsignedRemainder_ByPowerOfTwo_BecomesMask() {
    var fn = Unary(IrType.I32, (b, x) => b.Binary(IrBinaryOp.URem, x, IrBuilder.ConstI32(32)));  // x % 32 -> x & 31

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("and i32 %x, 31"));
  }

  [Test]
  public void SignedDivide_ByPowerOfTwo_IsLeftAlone() {
    // signed division by a power of two needs a rounding correction, so it must not become a plain ashr
    var fn = Unary(IrType.I32, (b, x) => b.SDiv(x, IrBuilder.ConstI32(8)));

    InstCombine.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("sdiv"));
  }

  [Test]
  public void Mul_ByNonPowerOfTwo_IsLeftAsMultiply() {
    var fn = Unary(IrType.I32, (b, x) => b.Mul(x, IrBuilder.ConstI32(10)));

    InstCombine.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("mul i32 %x, 10"));
  }
}
