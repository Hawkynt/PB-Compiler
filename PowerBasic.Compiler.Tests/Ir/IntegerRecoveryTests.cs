using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// IntegerRecovery rewrites the floating-point form the front end emits for integral +/-/* back to
/// integer arithmetic when the result is stored as an integer - giving such functions a genuine
/// integer IR that the in-house x86-16 back end can select. Sound because the stored result is mod-2^N
/// either way.
/// </summary>
[TestFixture]
public sealed class IntegerRecoveryTests {

  [Test]
  public void Run_GivenFloatFormOfIntegerExpression_ThenRewritesToIntegerArithmetic() {
    // F%(a%, b%) = a%*2 + b%*3, in the front end's float form:
    //   fptosi( fadd( fmul(sitofp a, 2.0), fmul(sitofp b, 3.0) ) ) to i16
    var a = new IrArgument(IrType.I16, 0);
    var b = new IrArgument(IrType.I16, 1);
    var fn = new IrFunction("F", IrType.I16, [a, b]);
    var entry = fn.CreateBlock("entry");
    var fa = entry.Append(new IrCast(IrCastOp.SIToFP, a, IrType.F32));
    var m1 = entry.Append(new IrBinary(IrBinaryOp.FMul, fa, new IrConstantFloat(IrType.F32, 2)));
    var fb = entry.Append(new IrCast(IrCastOp.SIToFP, b, IrType.F32));
    var m2 = entry.Append(new IrBinary(IrBinaryOp.FMul, fb, new IrConstantFloat(IrType.F32, 3)));
    var sum = entry.Append(new IrBinary(IrBinaryOp.FAdd, m1, m2));
    var cast = entry.Append(new IrCast(IrCastOp.FPToSI, sum, IrType.I16));
    var ret = entry.Append(new IrRet(cast));

    var recovered = IntegerRecovery.Run(fn);

    Assert.That(recovered, Is.EqualTo(1), "the fptosi(float-tree) is recovered to integer arithmetic");
    // the return value is now an integer ADD of two integer MULs (no floating-point ops in the tree)
    Assert.That(ret.Value, Is.InstanceOf<IrBinary>());
    var add = (IrBinary)ret.Value!;
    Assert.That(add.Op, Is.EqualTo(IrBinaryOp.Add));
    Assert.That(add.Type, Is.EqualTo(IrType.I16));
    Assert.That(add.Lhs, Is.InstanceOf<IrBinary>().And.Property("Op").EqualTo(IrBinaryOp.Mul));
    Assert.That(((IrBinary)add.Lhs).Lhs, Is.SameAs(a), "the recovered multiply reads the original i16 argument directly");
  }

  [Test]
  public void Run_GivenGenuineFloatComputation_ThenLeavesItAlone() {
    // a function returning a real float result (no fptosi-to-int) is not touched
    var x = new IrArgument(IrType.F32, 0);
    var fn = new IrFunction("g", IrType.F32, [x]);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.FMul, x, new IrConstantFloat(IrType.F32, 2)))));

    Assert.That(IntegerRecovery.Run(fn), Is.EqualTo(0));
  }
}
