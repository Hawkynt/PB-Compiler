using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class BooleanCompareCanonicalizationTests {

  private static IrConstantInt Bool(bool value) => new(IrType.I1, value ? 1 : 0);

  [TestCase(IrCmpPred.Eq, true)]
  [TestCase(IrCmpPred.Ne, false)]
  public void BoolCompare_GivenIdentityForm_CollapsesToOperand(IrCmpPred pred, bool constant) {
    var value = new IrArgument(IrType.I1, 0, "value");
    var fn = new IrFunction("f", IrType.I1, [value]);
    var entry = fn.CreateBlock("entry");
    var cmp = entry.Append(new IrCmp(pred, value, Bool(constant)));
    entry.Append(new IrRet(cmp));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(((IrRet)entry.Terminator!).Value, Is.SameAs(value));
    Assert.That(fn.AllInstructions.OfType<IrCmp>(), Is.Empty);
  }

  [TestCase(IrCmpPred.Eq, false)]
  [TestCase(IrCmpPred.Ne, true)]
  public void BoolCompare_GivenNegatedForm_BecomesBooleanXor(IrCmpPred pred, bool constant) {
    var value = new IrArgument(IrType.I1, 0, "value");
    var fn = new IrFunction("f", IrType.I1, [value]);
    var entry = fn.CreateBlock("entry");
    var cmp = entry.Append(new IrCmp(pred, value, Bool(constant)));
    entry.Append(new IrRet(cmp));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var not = ((IrRet)entry.Terminator!).Value as IrBinary;
    Assert.That(not, Is.Not.Null);
    Assert.That(not!.Op, Is.EqualTo(IrBinaryOp.Xor));
    Assert.That(not.Lhs, Is.SameAs(value));
    Assert.That(not.Rhs, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)not.Rhs).ZeroExtended, Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrCmp>(), Is.Empty);
  }

  [Test]
  public void NegatedIntegerComparison_InvertsPredicateInsteadOfMaterializingBooleanNot() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var fn = new IrFunction("f", IrType.I1, [value]);
    var entry = fn.CreateBlock("entry");
    var inner = entry.Append(new IrCmp(IrCmpPred.Slt, value, new IrConstantInt(IrType.I16, 10)));
    var outer = entry.Append(new IrCmp(IrCmpPred.Eq, inner, Bool(false)));
    entry.Append(new IrRet(outer));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrBinary>(), Is.Empty);
    var cmp = fn.AllInstructions.OfType<IrCmp>().Single();
    Assert.That(cmp.Pred, Is.EqualTo(IrCmpPred.Sge));
    Assert.That(cmp.Lhs, Is.SameAs(value));
    Assert.That(((IrConstantInt)cmp.Rhs).Value, Is.EqualTo(10));
    Assert.That(((IrRet)entry.Terminator!).Value, Is.SameAs(cmp));
  }

  [Test]
  public void NegatedOrderedFloatComparison_PreservesNaNSemanticsWithBooleanNot() {
    var left = new IrArgument(IrType.F64, 0, "left");
    var right = new IrArgument(IrType.F64, 1, "right");
    var fn = new IrFunction("f", IrType.I1, [left, right]);
    var entry = fn.CreateBlock("entry");
    var inner = entry.Append(new IrCmp(IrCmpPred.Folt, left, right));
    var outer = entry.Append(new IrCmp(IrCmpPred.Eq, inner, Bool(false)));
    entry.Append(new IrRet(outer));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var cmp = fn.AllInstructions.OfType<IrCmp>().Single();
    Assert.That(cmp.Pred, Is.EqualTo(IrCmpPred.Folt),
      "!ordered-less is not ordered-greater-or-equal when either operand is NaN");
    var not = fn.AllInstructions.OfType<IrBinary>().Single();
    Assert.That(not.Op, Is.EqualTo(IrBinaryOp.Xor));
    Assert.That(not.Lhs, Is.SameAs(cmp));
    Assert.That(((IrRet)entry.Terminator!).Value, Is.SameAs(not));
  }
}
