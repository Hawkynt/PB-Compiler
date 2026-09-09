using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0337/O0338 — exact polynomial and reciprocal sequence rewrites.</summary>
[TestFixture]
public sealed class ArithmeticIdiomOptimizationTests {

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
}
