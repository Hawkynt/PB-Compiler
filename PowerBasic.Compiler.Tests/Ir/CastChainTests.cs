using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>InstCombine cast-chain simplification.</summary>
[TestFixture]
public sealed class CastChainTests {

  private static IrFunction Build(IrType argType, IrType retType, Func<IrBuilder, IrArgument, IrValue> body) {
    var x = new IrArgument(argType, 0, "x");
    var fn = new IrFunction("f", retType, [x]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    b.Ret(body(b, x));
    return fn;
  }

  [Test]
  public void SExtOfSExt_Combines() {
    // sext(sext(x:i8 -> i16) -> i32)  ->  sext(x -> i32)
    var fn = Build(IrType.I8, IrType.I32, (b, x) => b.SExt(b.SExt(x, IrType.I16), IrType.I32));
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCast>().Count(), Is.EqualTo(1));
    Assert.That(IrPrinter.Print(fn), Does.Contain("sext i8 %x to i32"));
  }

  [Test]
  public void TruncOfExt_BackToOriginalWidth_RoundTripsToTheValue() {
    // trunc(sext(x:i16 -> i32) -> i16)  ->  x
    var fn = Build(IrType.I16, IrType.I16, (b, x) => b.Trunc(b.SExt(x, IrType.I32), IrType.I16));
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCast>(), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("ret i16 %x"));
  }

  [Test]
  public void TruncOfWideExt_ToAnIntermediateWidth_StaysAWidening() {
    // trunc(zext(x:i8 -> i32) -> i16)  ->  zext(x -> i16)
    var fn = Build(IrType.I8, IrType.I16, (b, x) => b.Trunc(b.ZExt(x, IrType.I32), IrType.I16));
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("zext i8 %x to i16"));
  }

  [Test]
  public void TruncOfTrunc_Combines() {
    // trunc(trunc(x:i32 -> i16) -> i8)  ->  trunc(x -> i8)
    var fn = Build(IrType.I32, IrType.I8, (b, x) => b.Trunc(b.Trunc(x, IrType.I16), IrType.I8));
    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("trunc i32 %x to i8"));
    Assert.That(fn.AllInstructions.OfType<IrCast>().Count(), Is.EqualTo(1));
  }
}
