using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0345CommonDenominatorFactoringTests {

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("f", result, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void GivenLaterReciprocal_ThenSharedReciprocalReplacesItDirectly() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var d = new IrArgument(IrType.F64, 1, "d");
    var function = Function(IrType.F64, x, d);
    var block = function.Entry!;
    var quotient = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    var existingReciprocal = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), d));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, quotient, existingReciprocal));
    block.Append(new IrRet(sum));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    var divisions = function.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FDiv).ToArray();
    var products = function.AllInstructions.OfType<IrBinary>()
      .Where(binary => binary.Op == IrBinaryOp.FMul).ToArray();

    Assert.Multiple(() => {
      Assert.That(divisions, Has.Length.EqualTo(1));
      Assert.That(products, Has.Length.EqualTo(1));
      Assert.That(sum.Rhs, Is.SameAs(divisions[0]));
      Assert.That(existingReciprocal.Parent, Is.Null);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void GivenRepeatedReciprocals_ThenNoMultiplyByOneIsIntroduced() {
    var d = new IrArgument(IrType.F64, 0, "d");
    var function = Function(IrType.F64, d);
    var block = function.Entry!;
    var first = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), d));
    var second = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), d));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, first, second));
    block.Append(new IrRet(sum));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(1));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(first.Parent, Is.SameAs(block));
      Assert.That(second.Parent, Is.Null);
      Assert.That(sum.Lhs, Is.SameAs(first));
      Assert.That(sum.Rhs, Is.SameAs(first));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void GivenNoReciprocalPermission_ThenRepeatedDivisionsRemainStrict() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var d = new IrArgument(IrType.F64, 2, "d");
    var function = Function(IrType.F64, x, y, d);
    var block = function.Entry!;
    var left = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    var right = block.Append(new IrBinary(IrBinaryOp.FDiv, y, d));
    block.Append(new IrRet(block.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    Assert.That(FpFastMath.Run(function, IrFastMathFlags.None), Is.Zero);

    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(left.Parent, Is.SameAs(block));
      Assert.That(right.Parent, Is.SameAs(block));
    });
  }

  [Test]
  public void GivenDifferentSsaDivisors_ThenDivisionsAreNotFactoredTogether() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var d0 = new IrArgument(IrType.F64, 2, "d0");
    var d1 = new IrArgument(IrType.F64, 3, "d1");
    var function = Function(IrType.F64, x, y, d0, d1);
    var block = function.Entry!;
    var left = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d0));
    var right = block.Append(new IrBinary(IrBinaryOp.FDiv, y, d1));
    block.Append(new IrRet(block.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void GivenCallBetweenDivisions_ThenFactoringDoesNotCrossTheCallBarrier() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var d = new IrArgument(IrType.F64, 2, "d");
    var barrier = new IrFunction("opaque", IrType.Void);
    var function = Function(IrType.F64, x, y, d);
    var block = function.Entry!;
    var left = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrCall(IrType.Void, barrier, []));
    var right = block.Append(new IrBinary(IrBinaryOp.FDiv, y, d));
    block.Append(new IrRet(block.Append(new IrBinary(IrBinaryOp.FAdd, left, right))));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrBinary>().Count(binary => binary.Op == IrBinaryOp.FDiv), Is.EqualTo(2));
      Assert.That(function.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.FMul), Is.False);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }
}
