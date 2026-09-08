using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0342 — reciprocal square-root approximation contract.</summary>
[TestFixture]
public sealed class FpRsqrtOptimizationTests {

  [Test]
  public void Rsqrt_GivenFullRelaxedContract_ThenBothOperationsExposeLlvmRsqrtPermissions() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = new IrFunction("f", IrType.F64, [x]);
    var block = function.CreateBlock("entry");
    var root = block.Append(new IrCall(IrType.F64, sqrt, [x]));
    var reciprocal = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), root));
    block.Append(new IrRet(reciprocal));

    FpFastMath.Run(function,
      IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowReciprocal | IrFastMathFlags.AllowContract);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(root.FastMathFlags,
        Is.EqualTo(IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowContract));
      Assert.That(reciprocal.FastMathFlags,
        Is.EqualTo(IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowReciprocal | IrFastMathFlags.AllowContract));
      Assert.That(llvm, Does.Contain("call contract afn double @llvm.sqrt.f64"));
      Assert.That(llvm, Does.Contain("fdiv arcp contract afn double"));
    });
  }

  [Test]
  public void Rsqrt_GivenNoContractPermission_ThenApproximateDivisionIsNotInvented() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = new IrFunction("f", IrType.F64, [x]);
    var block = function.CreateBlock("entry");
    var root = block.Append(new IrCall(IrType.F64, sqrt, [x]));
    var reciprocal = block.Append(new IrBinary(IrBinaryOp.FDiv, new IrConstantFloat(IrType.F64, 1), root));
    block.Append(new IrRet(reciprocal));

    FpFastMath.Run(function, IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowReciprocal);

    Assert.Multiple(() => {
      Assert.That(root.FastMathFlags, Is.EqualTo(IrFastMathFlags.ApproxFunc));
      Assert.That(reciprocal.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowReciprocal));
      Assert.That(root.FastMathFlags & IrFastMathFlags.AllowContract, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(reciprocal.FastMathFlags & IrFastMathFlags.ApproxFunc, Is.EqualTo(IrFastMathFlags.None));
    });
  }

  [Test]
  public void OrdinaryDivision_GivenApproximateFunctionPermission_ThenAfnDoesNotLeakOutsideRsqrtPattern() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var d = new IrArgument(IrType.F64, 1, "d");
    var function = new IrFunction("f", IrType.F64, [x, d]);
    var block = function.CreateBlock("entry");
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.ApproxFunc | IrFastMathFlags.AllowReciprocal);

    Assert.That(division.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowReciprocal));
  }

  [Test]
  public void StandaloneSqrt_GivenContractPermission_ThenContractDoesNotLeakOutsideRsqrtPattern() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = new IrFunction("f", IrType.F64, [x]);
    var block = function.CreateBlock("entry");
    var root = block.Append(new IrCall(IrType.F64, sqrt, [x]));
    block.Append(new IrRet(root));

    FpFastMath.Run(function, IrFastMathFlags.AllowContract);

    Assert.That(root.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
  }
}
