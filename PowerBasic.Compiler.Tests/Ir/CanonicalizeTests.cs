using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>InstCombine canonicalization: double-NOT elimination and constant reassociation.</summary>
[TestFixture]
public sealed class CanonicalizeTests {

  private static IrFunction WithArg(IrType type, Func<IrBuilder, IrArgument, IrValue> build) {
    var x = new IrArgument(type, 0, "x");
    var fn = new IrFunction("f", type, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(build(b, x));
    return fn;
  }

  [Test]
  public void DoubleComplement_NotNotX_BecomesX() {
    // xor(xor(x, -1), -1) -> x  (the shape Eqv/Imp/NOT lowering produces)
    var fn = WithArg(IrType.I32, (b, x) => b.Xor(b.Xor(x, new IrConstantInt(IrType.I32, -1)), new IrConstantInt(IrType.I32, -1)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(fn.AllInstructions.OfType<IrBinary>(), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 %x"));
  }

  [Test]
  public void ConstantReassociation_AddChain_MergesConstants() {
    // (x + 3) + 4 -> x + 7
    var fn = WithArg(IrType.I32, (b, x) => b.Add(b.Add(x, IrBuilder.ConstI32(3)), IrBuilder.ConstI32(4)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(1));
    Assert.That(IrPrinter.Print(fn), Does.Contain("add i32 %x, 7"));
  }

  [Test]
  public void ConstantReassociation_XorChain_MergesConstants() {
    // (x ^ 12) ^ 10 -> x ^ 6
    var fn = WithArg(IrType.I32, (b, x) => b.Xor(b.Xor(x, IrBuilder.ConstI32(12)), IrBuilder.ConstI32(10)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("xor i32 %x, 6"));
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void ConstantReassociation_AndChain_MergesConstants() {
    // (x & 14) & 6 -> x & 6
    var fn = WithArg(IrType.I32, (b, x) => b.And(b.And(x, IrBuilder.ConstI32(14)), IrBuilder.ConstI32(6)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("and i32 %x, 6"));
  }

  [Test]
  public void RunForFaithfulSelection_GivenConstantComparison_ThenRetainsTheComparison() {
    var fn = new IrFunction("main", IrType.I1);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    var comparison = builder.Cmp(IrCmpPred.Slt,
      new IrConstantInt(IrType.I16, 200), new IrConstantInt(IrType.I16, 300));
    comparison.IsSourceCondition = true;
    builder.Ret(comparison);

    InstCombine.RunForFaithfulSelection(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrCmp>().Single(), Is.SameAs(comparison));
      Assert.That(fn.AllInstructions.OfType<IrRet>().Single().Value, Is.SameAs(comparison));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }
}
