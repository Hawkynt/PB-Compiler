using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Value-based middle-end passes over the IR: constant folding, instcombine, DCE.</summary>
[TestFixture]
public sealed class IrPassesTests {

  private static int Count<T>(IrFunction fn) => fn.AllInstructions.OfType<T>().Count();

  #region constant folding

  [Test]
  public void ConstFold_IntegerAdd_WrapsToTypeWidth() {
    var folded = IrConstFold.TryFold(new IrBinary(IrBinaryOp.Add, new IrConstantInt(IrType.I16, 32767), new IrConstantInt(IrType.I16, 1)));
    Assert.That(folded, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(-32768));     // 16-bit two's-complement wrap
  }

  [Test]
  public void ConstFold_UnsignedDivide_UsesUnsignedSemantics() {
    // i16 0xFFFF udiv 0x0002 = 32767 ; sdiv would give 0
    var folded = IrConstFold.TryFold(new IrBinary(IrBinaryOp.UDiv, new IrConstantInt(IrType.I16, -1), new IrConstantInt(IrType.I16, 2)));
    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(32767));
  }

  [Test]
  public void ConstFold_DivideByZero_IsDeclined() {
    Assert.That(IrConstFold.TryFold(new IrBinary(IrBinaryOp.SDiv, new IrConstantInt(IrType.I32, 5), new IrConstantInt(IrType.I32, 0))), Is.Null);
    Assert.That(IrConstFold.TryFold(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), new IrConstantFloat(IrType.F64, 0))), Is.Null);
  }

  [Test]
  public void ConstFold_Comparison_YieldsI1() {
    var folded = IrConstFold.TryFold(new IrCmp(IrCmpPred.Slt, new IrConstantInt(IrType.I32, 1), new IrConstantInt(IrType.I32, 2)));
    Assert.That(folded, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)folded!).Type, Is.EqualTo(IrType.I1));
    Assert.That(((IrConstantInt)folded).Value, Is.EqualTo(1));
  }

  #endregion

  #region instcombine

  [Test]
  public void InstCombine_FoldsConstantExpressionToASingleValue() {
    // i32 @f() { ret (2 + 3) * 4 }
    var fn = new IrFunction("f", IrType.I32);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var sum = b.Add(IrBuilder.ConstI32(2), IrBuilder.ConstI32(3));
    var prod = b.Mul(sum, IrBuilder.ConstI32(4));
    b.Ret(prod);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(Count<IrBinary>(fn), Is.EqualTo(0));
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 20"));
  }

  [Test]
  public void InstCombine_AppliesAlgebraicIdentities() {
    // i32 @f(i32 x) { ret (x + 0) * 1 }  ->  ret x
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var add0 = b.Add(x, IrBuilder.ConstI32(0));
    var mul1 = b.Mul(add0, IrBuilder.ConstI32(1));
    b.Ret(mul1);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(Count<IrBinary>(fn), Is.EqualTo(0));
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 %x"));
  }

  [Test]
  public void InstCombine_FoldsXorXxToZeroAndMulByZeroToZero() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I32, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var xx = b.Xor(x, x);                 // -> 0
    var z = b.Mul(x, xx);                 // -> mul x, 0 -> 0
    b.Ret(z);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(Count<IrBinary>(fn), Is.EqualTo(0));
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i32 0"));
  }

  [Test]
  public void InstCombine_FoldsSelfComparison() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.I1, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(b.Cmp(IrCmpPred.Eq, x, x));     // x == x -> true

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i1 1"));
  }

  #endregion

  #region dce

  [Test]
  public void Dce_RemovesUnusedSideEffectFreeInstruction() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var fn = new IrFunction("f", IrType.Void, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Add(x, x);                          // unused
    b.Ret();

    var removed = Dce.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(Count<IrBinary>(fn), Is.EqualTo(0));
  }

  [Test]
  public void Dce_KeepsStoresAndCalls() {
    var p = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.Void, [p]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Store(IrBuilder.ConstI32(7), p);    // side effect: must survive
    b.Ret();

    Dce.Run(fn);

    Assert.That(Count<IrStore>(fn), Is.EqualTo(1));
  }

  #endregion
}
