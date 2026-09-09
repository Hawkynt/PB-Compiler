using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0340 — contraction permission belongs only to an actual multiply-add/subtract pair.</summary>
[TestFixture]
public sealed class FmaContractionTests {

  private static IrFunction Function(params IrArgument[] arguments) {
    var function = new IrFunction("f", IrType.F64, arguments);
    function.CreateBlock("entry");
    return function;
  }

  [Test]
  public void Fma_GivenStandaloneMultiplyAndAdd_ThenContractPermissionIsNotApplied() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var d = new IrArgument(IrType.F64, 3, "d");
    var function = Function(a, b, c, d);
    var block = function.Entry!;
    var product = block.Append(new IrBinary(IrBinaryOp.FMul, a, b));
    var sum = block.Append(new IrBinary(IrBinaryOp.FAdd, c, d));
    block.Append(new IrRet(sum));

    FpFastMath.Run(function, IrFastMathFlags.AllowContract);

    Assert.Multiple(() => {
      Assert.That(product.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(sum.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(LlvmEmitter.Emit(function), Does.Not.Contain(" contract "));
    });
  }

  [Test]
  public void Fma_GivenDivision_ThenContractPermissionIsNotApplied() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var function = Function(a, b);
    var block = function.Entry!;
    var division = block.Append(new IrBinary(IrBinaryOp.FDiv, a, b));
    block.Append(new IrRet(division));

    FpFastMath.Run(function, IrFastMathFlags.AllowContract);

    Assert.Multiple(() => {
      Assert.That(division.FastMathFlags, Is.EqualTo(IrFastMathFlags.None));
      Assert.That(LlvmEmitter.Emit(function), Does.Not.Contain(" contract "));
    });
  }

  [Test]
  public void Fma_GivenMultiplySubtract_ThenContractionParticipantsCarryContract() {
    var a = new IrArgument(IrType.F64, 0, "a");
    var b = new IrArgument(IrType.F64, 1, "b");
    var c = new IrArgument(IrType.F64, 2, "c");
    var function = Function(a, b, c);
    var block = function.Entry!;
    var product = block.Append(new IrBinary(IrBinaryOp.FMul, a, b));
    var difference = block.Append(new IrBinary(IrBinaryOp.FSub, c, product));
    block.Append(new IrRet(difference));

    FpFastMath.Run(function, IrFastMathFlags.AllowContract);
    var llvm = LlvmEmitter.Emit(function);

    Assert.Multiple(() => {
      Assert.That(product.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowContract));
      Assert.That(difference.FastMathFlags, Is.EqualTo(IrFastMathFlags.AllowContract));
      Assert.That(llvm, Does.Contain("fmul contract double"));
      Assert.That(llvm, Does.Contain("fsub contract double"));
    });
  }
}
