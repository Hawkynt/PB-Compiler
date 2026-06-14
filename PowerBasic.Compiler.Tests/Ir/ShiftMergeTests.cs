using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>InstCombine shift-chain merging.</summary>
[TestFixture]
public sealed class ShiftMergeTests {

  private static IrFunction Shifted(IrBinaryOp op, int a, int b) {
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var bld = new IrBuilder(fn.CreateBlock("entry"));
    var inner = bld.Binary(op, x, IrBuilder.ConstI32(a));
    bld.Ret(bld.Binary(op, inner, IrBuilder.ConstI32(b)));
    return fn;
  }

  [Test]
  public void ShlChain_Merges() {
    var fn = Shifted(IrBinaryOp.Shl, 2, 1);   // (x << 2) << 1 -> x << 3
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("shl i32 %x, 3"));
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void LShrChain_Merges() {
    var fn = Shifted(IrBinaryOp.LShr, 3, 2);  // (x >>> 3) >>> 2 -> x >>> 5
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("lshr i32 %x, 5"));
  }

  [Test]
  public void ShiftChain_OutOfRange_IsLeftAlone() {
    var fn = Shifted(IrBinaryOp.Shl, 20, 20);  // 40 >= 32 bits: not merged
    InstCombine.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void MulThenMul_ViaStrengthReduction_FoldsToOneShift() {
    // x * 4 * 2 -> (x<<2)<<1 -> x<<3
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(b.Mul(b.Mul(x, IrBuilder.ConstI32(4)), IrBuilder.ConstI32(2)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("shl i32 %x, 3"));
  }
}
