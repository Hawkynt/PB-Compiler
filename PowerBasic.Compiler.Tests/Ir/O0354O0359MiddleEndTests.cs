using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression coverage for equality saturation and verified arithmetic lowering.</summary>
[TestFixture]
public sealed class O0354O0359MiddleEndTests {

  [Test]
  public void EqualitySaturation_GivenOrOfAndsWithACommonTerm_WhenRun_ThenItFactorsToAndOfOr() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var c = new IrArgument(IrType.I16, 2, "c");
    var fn = new IrFunction("f", IrType.I16, [a, b, c]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    builder.Ret(builder.Or(builder.And(a, b), builder.And(a, c)));

    Assert.That(EqualitySaturation.Run(fn), Is.EqualTo(1));
    Dce.Run(fn);

    var result = (IrBinary)fn.AllInstructions.OfType<IrRet>().Single().Value!;
    Assert.That(result.Op, Is.EqualTo(IrBinaryOp.And));
    Assert.That(result.Operands, Does.Contain(a));
    var varying = result.Operands.OfType<IrBinary>().Single();
    Assert.That(varying.Op, Is.EqualTo(IrBinaryOp.Or));
    Assert.That(varying.Operands, Is.EquivalentTo(new IrValue[] { b, c }));
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void EqualitySaturation_GivenAndOfOrsWithACommonTerm_WhenRun_ThenItFactorsToOrOfAnd() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var c = new IrArgument(IrType.I16, 2, "c");
    var fn = new IrFunction("f", IrType.I16, [a, b, c]);
    var builder = new IrBuilder(fn.CreateBlock("entry"));
    builder.Ret(builder.And(builder.Or(a, b), builder.Or(a, c)));

    Assert.That(EqualitySaturation.Run(fn), Is.EqualTo(1));
    Dce.Run(fn);

    var result = (IrBinary)fn.AllInstructions.OfType<IrRet>().Single().Value!;
    Assert.That(result.Op, Is.EqualTo(IrBinaryOp.Or));
    Assert.That(result.Operands, Does.Contain(a));
    var varying = result.Operands.OfType<IrBinary>().Single();
    Assert.That(varying.Op, Is.EqualTo(IrBinaryOp.And));
    Assert.That(varying.Operands, Is.EquivalentTo(new IrValue[] { b, c }));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void EqualitySaturation_GivenSharedInnerExpression_WhenRun_ThenItDoesNotPriceTheSharedValueAsDisposable() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var sink = new IrArgument(IrType.Ptr, 2, "sink");
    var fn = new IrFunction("f", IrType.I16, [a, b, sink]);
    var entry = fn.CreateBlock("entry");
    var shared = entry.Append(new IrBinary(IrBinaryOp.And, a, b));
    entry.Append(new IrStore(shared, sink));
    var root = entry.Append(new IrBinary(IrBinaryOp.Or, shared, shared));
    entry.Append(new IrRet(root));

    EqualitySaturation.Run(fn);
    Dce.Run(fn);

    Assert.That(shared.Parent, Is.Not.Null, "the store still needs the shared computation");
    Assert.That(fn.AllInstructions.OfType<IrRet>().Single().Value, Is.SameAs(shared));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void VerifiedArithmetic_GivenMultiplyBySeven_WhenRun_ThenVerifiedShiftSubtractReplacesMultiply() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var multiply = entry.Append(new IrBinary(IrBinaryOp.Mul, x, new IrConstantInt(IrType.I16, 7)));
    entry.Append(new IrRet(multiply));

    Assert.That(VerifiedArithmeticLowering.Run(fn), Is.EqualTo(1));

    Assert.That(multiply.Parent, Is.Null);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Mul), Is.False);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Shl), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Sub), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void VerifiedArithmetic_GivenSignedDivideByEight_WhenRun_ThenBiasAndArithmeticShiftReplaceDivide() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var divide = entry.Append(new IrBinary(IrBinaryOp.SDiv, x, new IrConstantInt(IrType.I16, 8)));
    entry.Append(new IrRet(divide));

    Assert.That(VerifiedArithmeticLowering.Run(fn), Is.EqualTo(1));

    Assert.That(divide.Parent, Is.Null);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.SDiv), Is.False);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Count(i => i.Op == IrBinaryOp.AShr), Is.EqualTo(2));
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.And), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void VerifiedArithmetic_GivenSignedRemainderByNegativeEight_WhenRun_ThenRemainderUsesVerifiedQuotientProduct() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var remainder = entry.Append(new IrBinary(IrBinaryOp.SRem, x, new IrConstantInt(IrType.I16, -8)));
    entry.Append(new IrRet(remainder));

    Assert.That(VerifiedArithmeticLowering.Run(fn), Is.EqualTo(1));

    Assert.That(remainder.Parent, Is.Null);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.SRem), Is.False);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(i => i.Op == IrBinaryOp.Shl), Is.True);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void VerifiedArithmetic_GivenDivideByMinusOne_WhenRun_ThenOverflowingMinValueCaseKeepsOriginalDivide() {
    var x = new IrArgument(IrType.I16, 0, "x");
    var fn = new IrFunction("f", IrType.I16, [x]);
    var entry = fn.CreateBlock("entry");
    var divide = entry.Append(new IrBinary(IrBinaryOp.SDiv, x, new IrConstantInt(IrType.I16, -1)));
    entry.Append(new IrRet(divide));

    Assert.That(VerifiedArithmeticLowering.Run(fn), Is.Zero);
    Assert.That(divide.Parent, Is.SameAs(entry));
    Assert.That(fn.AllInstructions.OfType<IrRet>().Single().Value, Is.SameAs(divide));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
