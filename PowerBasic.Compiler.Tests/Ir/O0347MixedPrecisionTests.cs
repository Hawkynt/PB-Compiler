using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0347MixedPrecisionTests {

  [TestCase(IrBinaryOp.FAdd)]
  [TestCase(IrBinaryOp.FSub)]
  [TestCase(IrBinaryOp.FMul)]
  public void Narrow_GivenFiniteBinary32OperandsComputedInBinary64_ThenBasicOperationNarrowsExactly(IrBinaryOp op) {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var function = Function(IrType.F32, a, b);
    var block = function.Entry!;
    var af = block.Append(new IrCast(IrCastOp.SIToFP, a, IrType.F32));
    var bf = block.Append(new IrCast(IrCastOp.SIToFP, b, IrType.F32));
    var aw = block.Append(new IrCast(IrCastOp.FPExt, af, IrType.F64));
    var bw = block.Append(new IrCast(IrCastOp.FPExt, bf, IrType.F64));
    var wide = block.Append(new IrBinary(op, aw, bw));
    var trunc = block.Append(new IrCast(IrCastOp.FPTrunc, wide, IrType.F32));
    var ret = block.Append(new IrRet(trunc));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    Assert.That(ret.Value, Is.TypeOf<IrBinary>());
    var narrowed = (IrBinary)ret.Value!;
    Assert.Multiple(() => {
      Assert.That(narrowed.Op, Is.EqualTo(op));
      Assert.That(narrowed.Type, Is.EqualTo(IrType.F32));
      Assert.That(narrowed.Lhs, Is.SameAs(af));
      Assert.That(narrowed.Rhs, Is.SameAs(bf));
      Assert.That(wide.Parent, Is.Null);
      Assert.That(trunc.Parent, Is.Null);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Narrow_GivenFiniteNonZeroBinary32Divisor_ThenDivisionNarrowsExactly() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var function = Function(IrType.F32, a);
    var block = function.Entry!;
    var af = block.Append(new IrCast(IrCastOp.SIToFP, a, IrType.F32));
    var divisor = new IrConstantFloat(IrType.F32, 3.0);
    var aw = block.Append(new IrCast(IrCastOp.FPExt, af, IrType.F64));
    var bw = block.Append(new IrCast(IrCastOp.FPExt, divisor, IrType.F64));
    var wide = block.Append(new IrBinary(IrBinaryOp.FDiv, aw, bw));
    var trunc = block.Append(new IrCast(IrCastOp.FPTrunc, wide, IrType.F32));
    var ret = block.Append(new IrRet(trunc));

    Assert.That(FpSimplify.Run(function), Is.EqualTo(1));

    var narrowed = ret.Value as IrBinary;
    Assert.Multiple(() => {
      Assert.That(narrowed, Is.Not.Null);
      Assert.That(narrowed!.Op, Is.EqualTo(IrBinaryOp.FDiv));
      Assert.That(narrowed.Type, Is.EqualTo(IrType.F32));
      Assert.That(narrowed.Lhs, Is.SameAs(af));
      Assert.That(narrowed.Rhs, Is.SameAs(divisor));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Narrow_GivenDivisorMayBeZero_ThenStrictDivisionRemainsWide() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var function = Function(IrType.F32, a, b);
    var block = function.Entry!;
    var af = block.Append(new IrCast(IrCastOp.SIToFP, a, IrType.F32));
    var bf = block.Append(new IrCast(IrCastOp.SIToFP, b, IrType.F32));
    var aw = block.Append(new IrCast(IrCastOp.FPExt, af, IrType.F64));
    var bw = block.Append(new IrCast(IrCastOp.FPExt, bf, IrType.F64));
    var wide = block.Append(new IrBinary(IrBinaryOp.FDiv, aw, bw));
    var trunc = block.Append(new IrCast(IrCastOp.FPTrunc, wide, IrType.F32));
    block.Append(new IrRet(trunc));

    Assert.Multiple(() => {
      Assert.That(FpSimplify.Run(function), Is.Zero);
      Assert.That(wide.Parent, Is.SameAs(block));
      Assert.That(trunc.Parent, Is.SameAs(block));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Narrow_GivenBinary64InputNotOriginatingInBinary32_ThenDoesNotChangePrecision() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var function = Function(IrType.F32, a, b);
    var block = function.Entry!;
    var wide = block.Append(new IrBinary(IrBinaryOp.FAdd, a, b));
    var trunc = block.Append(new IrCast(IrCastOp.FPTrunc, wide, IrType.F32));
    block.Append(new IrRet(trunc));

    Assert.Multiple(() => {
      Assert.That(FpSimplify.Run(function), Is.Zero);
      Assert.That(wide.Parent, Is.SameAs(block));
      Assert.That(trunc.Parent, Is.SameAs(block));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("o0347", result, arguments);
    function.CreateBlock("entry");
    return function;
  }
}
