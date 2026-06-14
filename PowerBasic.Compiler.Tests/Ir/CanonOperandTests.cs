using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>InstCombine operand canonicalization: sub-to-add and constant-to-RHS for comparisons.</summary>
[TestFixture]
public sealed class CanonOperandTests {

  private static IrFunction OneArg(IrType type, Func<IrBuilder, IrArgument, IrValue> build) {
    var x = new IrArgument(type, 0, "x");
    var fn = new IrFunction("f", type, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(build(b, x));
    return fn;
  }

  [Test]
  public void SubConstant_BecomesAddNegatedConstant() {
    var fn = OneArg(IrType.I32, (b, x) => b.Sub(x, IrBuilder.ConstI32(5)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("add i32 %x, -5"));
    Assert.That(text, Does.Not.Contain("sub"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void SubChain_AfterCanonicalization_MergesConstants() {
    // (x - 3) - 4 -> add x, -7
    var fn = OneArg(IrType.I32, (b, x) => b.Sub(b.Sub(x, IrBuilder.ConstI32(3)), IrBuilder.ConstI32(4)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("add i32 %x, -7"));
  }

  [Test]
  public void Comparison_ConstantOnLeft_IsCanonicalizedToTheRight() {
    // icmp slt 10, x  ->  icmp sgt x, 10
    var fn = new IrFunction("f", IrType.I1, [new IrArgument(IrType.I32, 0, "x")]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(b.Cmp(IrCmpPred.Slt, IrBuilder.ConstI32(10), fn.Parameters[0]));

    InstCombine.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("icmp sgt i32 %x, 10"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
