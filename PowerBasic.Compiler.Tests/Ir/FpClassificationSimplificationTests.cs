using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0346 — strict floating-point classification and range simplification.</summary>
[TestFixture]
public sealed class FpClassificationSimplificationTests {

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("f", result, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void Classification_GivenBranchRefinedAffineDomain_ThenThresholdComparisonFolds() {
    var n = new IrArgument(IrType.I16, 0, "n");
    var function = Function(IrType.I1, n);
    var entry = function.Entry!;
    var inRange = function.CreateBlock("in.range");
    var other = function.CreateBlock("other");
    var guard = entry.Append(new IrCmp(IrCmpPred.Sge, n, new IrConstantInt(IrType.I16, 10)));
    entry.Append(new IrCondBr(guard, inRange, other));

    var floating = inRange.Append(new IrCast(IrCastOp.SIToFP, n, IrType.F64));
    var shifted = inRange.Append(new IrBinary(IrBinaryOp.FAdd, floating, new IrConstantFloat(IrType.F64, 0.5)));
    var comparison = inRange.Append(new IrCmp(IrCmpPred.Foge, shifted, new IrConstantFloat(IrType.F64, 10.5)));
    var ret = inRange.Append(new IrRet(comparison));
    other.Append(new IrRet(new IrConstantInt(IrType.I1, 0)));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenDisjointIntegerDerivedDomains_ThenComparisonFolds() {
    var a = new IrArgument(IrType.U8, 0, "a");
    var b = new IrArgument(IrType.U8, 1, "b");
    var function = Function(IrType.I1, a, b);
    var block = function.Entry!;
    var af = block.Append(new IrCast(IrCastOp.UIToFP, a, IrType.F64));
    var bf = block.Append(new IrCast(IrCastOp.UIToFP, b, IrType.F64));
    var shifted = block.Append(new IrBinary(IrBinaryOp.FAdd, af, new IrConstantFloat(IrType.F64, 300)));
    var comparison = block.Append(new IrCmp(IrCmpPred.Fogt, shifted, bf));
    var ret = block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenBranchRefinedNegativeIntegerConvertedToF80_ThenSignComparisonFolds() {
    var n = new IrArgument(IrType.I16, 0, "n");
    var function = Function(IrType.I1, n);
    var entry = function.Entry!;
    var negative = function.CreateBlock("negative");
    var other = function.CreateBlock("other");
    var guard = entry.Append(new IrCmp(IrCmpPred.Slt, n, new IrConstantInt(IrType.I16, 0)));
    entry.Append(new IrCondBr(guard, negative, other));

    var floating = negative.Append(new IrCast(IrCastOp.SIToFP, n, IrType.F80));
    var comparison = negative.Append(new IrCmp(IrCmpPred.Folt, floating, new IrConstantFloat(IrType.F80, 0)));
    var ret = negative.Append(new IrRet(comparison));
    other.Append(new IrRet(new IrConstantInt(IrType.I1, 0)));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenFiniteIntegerCastComparedWithPositiveInfinity_ThenComparisonFolds() {
    var n = new IrArgument(IrType.I32, 0, "n");
    var function = Function(IrType.I1, n);
    var block = function.Entry!;
    var floating = block.Append(new IrCast(IrCastOp.SIToFP, n, IrType.F80));
    var comparison = block.Append(new IrCmp(IrCmpPred.Folt, floating,
      new IrConstantFloat(IrType.F80, double.PositiveInfinity)));
    var ret = block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenNaNConstant_ThenOrderedComparisonIsAlwaysFalse() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var function = Function(IrType.I1, x);
    var block = function.Entry!;
    var comparison = block.Append(new IrCmp(IrCmpPred.Foge, x,
      new IrConstantFloat(IrType.F64, double.NaN)));
    var ret = block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.Zero);
  }

  [Test]
  public void Classification_GivenZeroExtendedSignedConstant_ThenSignedMeaningIsPreserved() {
    var function = Function(IrType.I1);
    var block = function.Entry!;
    var minusOneBits = new IrConstantInt(IrType.I8, 0xff);
    var floating = block.Append(new IrCast(IrCastOp.SIToFP, minusOneBits, IrType.F80));
    var comparison = block.Append(new IrCmp(IrCmpPred.Folt, floating, new IrConstantFloat(IrType.F80, 0)));
    var ret = block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }

  [Test]
  public void Classification_GivenUnconstrainedSquareInStrictMode_ThenNaNPreventsSignFold() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var function = Function(IrType.I1, x);
    var block = function.Entry!;
    var square = block.Append(new IrBinary(IrBinaryOp.FMul, x, x));
    var comparison = block.Append(new IrCmp(IrCmpPred.Foge, square, new IrConstantFloat(IrType.F64, 0)));
    block.Append(new IrRet(comparison));

    Assert.That(FpSimplify.Run(function), Is.Zero);
    Assert.That(comparison.Parent, Is.SameAs(block));
  }

  [Test]
  public void Classification_GivenUnconstrainedSquareUnderSpeed_ThenNoNaNsMakesSignFoldLegal() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var function = Function(IrType.I1, x);
    var block = function.Entry!;
    var square = block.Append(new IrBinary(IrBinaryOp.FMul, x, x));
    var comparison = block.Append(new IrCmp(IrCmpPred.Foge, square, new IrConstantFloat(IrType.F64, 0)));
    var ret = block.Append(new IrRet(comparison));

    IrPassManager.Standard(optimizeForSpeed: true, includeModulePasses: false).RunToFixpoint(function);

    Assert.That(ret.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)ret.Value!).Value, Is.EqualTo(1));
  }
}
