using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0344 — floating-point reassociation under the explicit fast-math contract.</summary>
[TestFixture]
public sealed class FpReassociationTests {

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("f", result, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void Reassociation_GivenSerialAddition_ThenOldChainIsRemovedAfterBalancing() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var d = new IrArgument(IrType.F64, 3, "d");
    var function = Function(IrType.F64, a, b, c, d);
    var block = function.Entry!;
    var ab = block.Append(new IrBinary(IrBinaryOp.FAdd, a, b));
    var abc = block.Append(new IrBinary(IrBinaryOp.FAdd, ab, c));
    var abcd = block.Append(new IrBinary(IrBinaryOp.FAdd, abc, d));
    var ret = block.Append(new IrRet(abcd));

    FpFastMath.Run(function, IrFastMathFlags.Reassociate);

    var arithmetic = function.AllInstructions.OfType<IrBinary>().ToArray();
    Assert.Multiple(() => {
      Assert.That(ret.Value, Is.Not.SameAs(abcd));
      Assert.That(arithmetic, Has.Length.EqualTo(3));
      Assert.That(ab.Parent, Is.Null);
      Assert.That(abc.Parent, Is.Null);
      Assert.That(abcd.Parent, Is.Null);
    });
  }

  [Test]
  public void Reassociation_GivenSignedZeroFreedom_ThenMixedAddSubtractChainBalances() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var d = new IrArgument(IrType.F64, 3, "d");
    var function = Function(IrType.F64, a, b, c, d);
    var block = function.Entry!;
    var ab = block.Append(new IrBinary(IrBinaryOp.FAdd, a, b));
    var abc = block.Append(new IrBinary(IrBinaryOp.FSub, ab, c));
    var abcd = block.Append(new IrBinary(IrBinaryOp.FAdd, abc, d));
    var ret = block.Append(new IrRet(abcd));

    FpFastMath.Run(function, IrFastMathFlags.Fast);

    var root = ret.Value as IrBinary;
    Assert.That(root, Is.Not.Null);
    var left = root!.Lhs as IrBinary;
    var right = root.Rhs as IrBinary;
    Assert.Multiple(() => {
      Assert.That(root.Op, Is.EqualTo(IrBinaryOp.FSub));
      Assert.That(left, Is.Not.Null);
      Assert.That(left!.Op, Is.EqualTo(IrBinaryOp.FAdd));
      Assert.That(left.Lhs, Is.SameAs(a));
      Assert.That(left.Rhs, Is.SameAs(b));
      Assert.That(right, Is.Not.Null);
      Assert.That(right!.Op, Is.EqualTo(IrBinaryOp.FSub));
      Assert.That(right.Lhs, Is.SameAs(c));
      Assert.That(right.Rhs, Is.SameAs(d));
      Assert.That(ab.Parent, Is.Null);
      Assert.That(abc.Parent, Is.Null);
      Assert.That(abcd.Parent, Is.Null);
      Assert.That(function.AllInstructions.OfType<IrBinary>().All(binary =>
        (binary.FastMathFlags & (IrFastMathFlags.Reassociate | IrFastMathFlags.NoSignedZeros))
          == (IrFastMathFlags.Reassociate | IrFastMathFlags.NoSignedZeros)), Is.True);
      Assert.That(function.AllInstructions.OfType<IrBinary>().All(binary =>
        (binary.FastMathFlags & (IrFastMathFlags.AllowReciprocal | IrFastMathFlags.ApproxFunc)) == 0), Is.True);
    });
  }

  [Test]
  public void Reassociation_GivenNoSignedZeroFreedom_ThenSubtractionChainKeepsItsGrouping() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var d = new IrArgument(IrType.F64, 3, "d");
    var function = Function(IrType.F64, a, b, c, d);
    var block = function.Entry!;
    var ab = block.Append(new IrBinary(IrBinaryOp.FAdd, a, b));
    var abc = block.Append(new IrBinary(IrBinaryOp.FSub, ab, c));
    var abcd = block.Append(new IrBinary(IrBinaryOp.FAdd, abc, d));
    var ret = block.Append(new IrRet(abcd));

    FpFastMath.Run(function, IrFastMathFlags.Reassociate);

    Assert.Multiple(() => {
      Assert.That(ret.Value, Is.SameAs(abcd));
      Assert.That(ab.Parent, Is.SameAs(block));
      Assert.That(abc.Parent, Is.SameAs(block));
      Assert.That(abcd.Parent, Is.SameAs(block));
    });
  }
}
