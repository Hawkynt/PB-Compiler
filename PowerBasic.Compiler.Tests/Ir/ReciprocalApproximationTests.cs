using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0341 — reciprocal approximation lowering into LLVM target-selected estimates.</summary>
[TestFixture]
public sealed class ReciprocalApproximationTests {

  private static IrFunction Function(IrType result, params IrArgument[] arguments) {
    var function = new IrFunction("f", result, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void GivenSpeedEligibleSingleDivision_ThenEmitterRequestsMatchingTargetEstimate() {
    var x = new IrArgument(IrType.F32, 0, "x");
    var d = new IrArgument(IrType.F32, 1, "d");
    var function = Function(IrType.F32, x, d);
    var block = function.Entry!;
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.Fast);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(llvm, Does.Contain("\"reciprocal-estimates\"=\"divf\""));
      Assert.That(llvm, Does.Contain("fdiv reassoc nnan ninf nsz arcp float"));
    });
  }

  [Test]
  public void GivenEligibleSingleAndDoubleDivisions_ThenEmitterRequestsBothEstimateKinds() {
    var xf = new IrArgument(IrType.F32, 0, "xf");
    var df = new IrArgument(IrType.F32, 1, "df");
    var xd = new IrArgument(IrType.F64, 2, "xd");
    var dd = new IrArgument(IrType.F64, 3, "dd");
    var function = Function(IrType.F64, xf, df, xd, dd);
    var block = function.Entry!;
    block.Append(new IrBinary(IrBinaryOp.FDiv, xf, df));
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, xd, dd));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.Fast);

    Assert.That(LlvmEmitter.Emit(function), Does.Contain("\"reciprocal-estimates\"=\"divf,divd\""));
  }

  [Test]
  public void GivenReciprocalPermissionWithoutNoInfContract_ThenEmitterDoesNotForceEstimateCodegen() {
    var x = new IrArgument(IrType.F64, 0, "x");
    var d = new IrArgument(IrType.F64, 1, "d");
    var function = Function(IrType.F64, x, d);
    var block = function.Entry!;
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.AllowReciprocal);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(llvm, Does.Contain("fdiv arcp double"));
      Assert.That(llvm, Does.Not.Contain("reciprocal-estimates"));
    });
  }

  [Test]
  public void GivenExtendedPrecisionDivision_ThenEmitterLeavesEstimateSelectionDisabled() {
    var x = new IrArgument(IrType.F80, 0, "x");
    var d = new IrArgument(IrType.F80, 1, "d");
    var function = Function(IrType.F80, x, d);
    var block = function.Entry!;
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, x, d));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.Fast);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(llvm, Does.Contain("fdiv reassoc nnan ninf nsz arcp x86_fp80"));
      Assert.That(llvm, Does.Not.Contain("reciprocal-estimates"));
    });
  }
}
