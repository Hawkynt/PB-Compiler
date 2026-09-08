using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0337/O0338 — exact polynomial and reciprocal sequence rewrites.</summary>
[TestFixture]
public sealed class ArithmeticIdiomOptimizationTests {

  private sealed class NeverReuseCost : IIrArithmeticCostModel {
    public bool PreferReciprocalReuse(IrType type, int divisionCount) => false;
  }

  [Test]
  public void IntegerCubic_GivenRepeatedLiteralPowers_ThenHornerUsesFewerMultiplies() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("poly", IrType.I16, [x]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var x2a = entry.Append(new IrBinary(IrBinaryOp.Mul, x, x));
    var x3 = entry.Append(new IrBinary(IrBinaryOp.Mul, x2a, x));
    var x2b = entry.Append(new IrBinary(IrBinaryOp.Mul, x, x));
    var threeX2 = entry.Append(new IrBinary(IrBinaryOp.Mul, x2b, new IrConstantInt(IrType.I16, 3)));
    var fiveX = entry.Append(new IrBinary(IrBinaryOp.Mul, x, new IrConstantInt(IrType.I16, 5)));
    var first = entry.Append(new IrBinary(IrBinaryOp.Add, x3, threeX2));
    var second = entry.Append(new IrBinary(IrBinaryOp.Add, first, fiveX));
    var root = entry.Append(new IrBinary(IrBinaryOp.Add, second, new IrConstantInt(IrType.I16, 7)));
    entry.Append(new IrRet(root));

    Assert.That(PolynomialEvaluation.Run(fn), Is.EqualTo(1));
    Dce.Run(fn);
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.Mul), Is.EqualTo(3));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void FloatPolynomial_GivenTheSameAlgebraicShape_ThenRoundingRulesKeepItUntouched() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var fn = new IrFunction("floatPoly", IrType.F64, [x]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var x2 = entry.Append(new IrBinary(IrBinaryOp.FMul, x, x));
    var x3 = entry.Append(new IrBinary(IrBinaryOp.FMul, x2, x));
    entry.Append(new IrRet(x3));

    Assert.That(PolynomialEvaluation.Run(fn), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(2));
  }

  [Test]
  public void RepeatedDivision_GivenAnExactPowerOfTwoDivisor_ThenBothUseItsReciprocal() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var fn = new IrFunction("scale", IrType.F64, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F64, 8.0)));
    var right = entry.Append(new IrBinary(IrBinaryOp.FDiv, y, new IrConstantFloat(IrType.F64, 8.0)));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));
    var multiplies = fn.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FMul)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FDiv), Is.False);
      Assert.That(multiplies, Has.Count.EqualTo(2));
      Assert.That(multiplies, Has.All.Matches<IrBinary>(binary =>
        binary.Rhs is IrConstantFloat { Value: 0.125 }),
        "both divisions should use the exact reciprocal constant rather than a runtime reciprocal");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenAnExactF80PowerOfTwo_ThenTheExtendedOperationsUseAnExactReciprocal() {
    var x = new IrArgument(IrType.F80, 0, "x");
    var y = new IrArgument(IrType.F80, 1, "y");
    var fn = new IrFunction("scale80", IrType.F80, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F80, 8.0)));
    var right = entry.Append(new IrBinary(IrBinaryOp.FDiv, y, new IrConstantFloat(IrType.F80, 8.0)));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));

    var multiplies = fn.AllInstructions.OfType<IrBinary>().Where(binary => binary.Op == IrBinaryOp.FMul).ToList();
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FDiv), Is.False);
      Assert.That(multiplies, Has.Count.EqualTo(2));
      Assert.That(multiplies, Has.All.Matches<IrBinary>(binary =>
        binary.Type.Equals(IrType.F80) && binary.Rhs is IrConstantFloat { Type: { Bits: 80 }, Value: 0.125 }));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenAnExactDivisorUnderFastMath_ThenGeneratedMultipliesKeepArithmeticFlags() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var fn = new IrFunction("fastExact", IrType.F64, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F64, 8.0)) {
      FastMathFlags = IrFastMathFlags.Fast,
    });
    var right = entry.Append(new IrBinary(IrBinaryOp.FDiv, y, new IrConstantFloat(IrType.F64, 8.0)) {
      FastMathFlags = IrFastMathFlags.Fast,
    });
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));

    var expected = IrFastMathFlags.Reassociate | IrFastMathFlags.NoNaNs | IrFastMathFlags.NoInfs
      | IrFastMathFlags.NoSignedZeros | IrFastMathFlags.AllowContract;
    var multiplies = fn.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FMul)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(multiplies, Has.Count.EqualTo(2));
      Assert.That(multiplies, Has.All.Matches<IrBinary>(binary => binary.FastMathFlags == expected));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenAThirdDivisor_ThenStrictFpKeepsTheDivisions() {
    var x = new IrArgument(IrType.F32, 0, "x");
    var y = new IrArgument(IrType.F32, 1, "y");
    var fn = new IrFunction("strict", IrType.F32, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F32, 3.0)));
    var right = entry.Append(new IrBinary(IrBinaryOp.FDiv, y, new IrConstantFloat(IrType.F32, 3.0)));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.Zero);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
  }

  [Test]
  public void RepeatedDivision_GivenArcpAcrossDominatedBlocks_ThenOneRuntimeReciprocalIsShared() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("crossBlock", IrType.F64, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    entry.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));

    var divisions = fn.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FDiv)
      .ToList();
    var multiplies = fn.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FMul)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(divisions, Has.Count.EqualTo(1));
      Assert.That(divisions[0].Lhs, Is.TypeOf<IrConstantFloat>());
      Assert.That(((IrConstantFloat)divisions[0].Lhs).Value, Is.EqualTo(1.0));
      Assert.That(divisions[0].Rhs, Is.SameAs(divisor));
      Assert.That(divisions[0].FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowReciprocal));
      Assert.That(multiplies, Has.Count.EqualTo(2));
      Assert.That(multiplies, Has.All.Matches<IrBinary>(binary =>
        (binary.FastMathFlags & IrFastMathFlags.AllowReciprocal) == 0));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenArcpF80AcrossDominatedBlocks_ThenTheRuntimeReciprocalKeepsExtendedType() {
    var x = new IrArgument(IrType.F80, 0, "x");
    var y = new IrArgument(IrType.F80, 1, "y");
    var divisor = new IrArgument(IrType.F80, 2, "d");
    var fn = new IrFunction("crossBlock80", IrType.F80, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    entry.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));

    var reciprocal = fn.AllInstructions.OfType<IrBinary>().Single(binary => binary.Op == IrBinaryOp.FDiv);
    Assert.Multiple(() => {
      Assert.That(reciprocal.Type, Is.EqualTo(IrType.F80));
      Assert.That(reciprocal.Lhs, Is.TypeOf<IrConstantFloat>());
      Assert.That(((IrConstantFloat)reciprocal.Lhs).Type, Is.EqualTo(IrType.F80));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenTargetCostDeclinesReuse_ThenArcpAloneDoesNotForceTheTransform() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("costed", IrType.F64, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    var right = entry.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn, new NeverReuseCost()), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenBitIdenticalNonExactConstantsAndArcp_ThenOneRuntimeReciprocalIsShared() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var fn = new IrFunction("constantThirds", IrType.F64, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, new IrConstantFloat(IrType.F64, 3.0)) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    entry.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, new IrConstantFloat(IrType.F64, 3.0)) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenArcpAcrossSiblingBranches_ThenItDoesNotSpeculateAReciprocal() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var x = new IrArgument(IrType.F64, 1, "x");
    var y = new IrArgument(IrType.F64, 2, "y");
    var divisor = new IrArgument(IrType.F64, 3, "d");
    var fn = new IrFunction("siblings", IrType.F64, [condition, x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var ifTrue = fn.AddBlock(new IrBasicBlock("true"));
    var ifFalse = fn.AddBlock(new IrBasicBlock("false"));
    entry.Append(new IrCondBr(condition, ifTrue, ifFalse));
    var left = ifTrue.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    ifTrue.Append(new IrRet(left));
    var right = ifFalse.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    ifFalse.Append(new IrRet(right));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenACallBetweenArcpDivisions_ThenItDoesNotReuseAcrossTheBarrier() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("callBarrier", IrType.F64, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var middle = fn.AddBlock(new IrBasicBlock("middle"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var opaque = new IrFunction("opaque", IrType.Void);
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    entry.Append(new IrBr(middle));
    middle.Append(new IrCall(IrType.Void, opaque, []));
    middle.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void RepeatedDivision_GivenAnInvariantDivisorInACanonicalLoop_ThenReciprocalIsLazilyHoistedBehindEntryGuard() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("loop", IrType.Void, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var header = fn.AddBlock(new IrBasicBlock("header"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 4)));
    header.Append(new IrCondBr(test, body, exit));

    var left = body.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    var right = body.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) { FastMathFlags = IrFastMathFlags.AllowReciprocal });
    body.Append(new IrBinary(IrBinaryOp.FAdd, left, right));
    var next = body.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    body.Append(new IrBr(header));
    counter.AddIncoming(next, body);
    exit.Append(new IrRet());

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(3));

    var init = fn.Blocks.Single(block => block.Label.StartsWith("recip.init", StringComparison.Ordinal));
    var reciprocal = init.Instructions.OfType<IrBinary>().Single(binary => binary.Op == IrBinaryOp.FDiv);
    Assert.Multiple(() => {
      Assert.That(entry.Terminator, Is.TypeOf<IrCondBr>(), "the original zero-trip test must guard reciprocal formation");
      Assert.That(((IrCondBr)entry.Terminator!).IfFalse, Is.SameAs(exit));
      Assert.That(((IrCondBr)entry.Terminator!).IfTrue, Is.SameAs(init));
      Assert.That(counter.IncomingBlocks, Does.Contain(init));
      Assert.That(counter.IncomingBlocks, Does.Not.Contain(entry));
      Assert.That(reciprocal.Rhs, Is.SameAs(divisor));
      Assert.That(body.Instructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(body.Instructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FDiv), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenSpeedAndCrossBlockDivisions_ThenO0338ConsumesTheArcpContract() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var divisor = new IrArgument(IrType.F64, 2, "d");
    var fn = new IrFunction("pipeline", IrType.F64, [x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var next = fn.AddBlock(new IrBasicBlock("next"));
    var left = entry.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor));
    entry.Append(new IrBr(next));
    var right = next.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor));
    next.Append(new IrRet(next.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    IrPassManager.Standard(optimizeForSpeed: true, includeModulePasses: false).RunToFixpoint(fn);

    Assert.Multiple(() => {
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FMul), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }
}
