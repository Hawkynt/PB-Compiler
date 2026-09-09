using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0061 — reassociation. The pass is judged on what it EXPOSES, not on the tree it builds: a
/// canonical chain is only worth having because constant folding and value numbering can then see
/// through it. So most of these assert the downstream result.
/// </summary>
[TestFixture]
public sealed class ReassociateTests {

  private static (IrFunction Fn, IrBasicBlock Entry, IrArgument A, IrArgument B) Function() {
    var a = new IrArgument(IrType.I16, 0, "a");
    var b = new IrArgument(IrType.I16, 1, "b");
    var fn = new IrFunction("f", IrType.I16, [a, b]);
    return (fn, fn.AddBlock(new IrBasicBlock("entry")), a, b);
  }

  private static IrConstantInt Const(long v) => new(IrType.I16, v);

  [Test]
  public void Chain_GivenConstantsSeparatedByAVariable_ThenTheyFoldIntoOne() {
    // (a + 1) + 2  -  the constants are not adjacent, so instcombine alone cannot fold them
    var (fn, entry, a, _) = Function();
    var inner = entry.Append(new IrBinary(IrBinaryOp.Add, a, Const(1)));
    var outer = entry.Append(new IrBinary(IrBinaryOp.Add, inner, Const(2)));
    entry.Append(new IrRet(outer));

    Assert.That(Reassociate.Run(fn), Is.EqualTo(1));
    Dce.Run(fn);

    var adds = fn.AllInstructions.OfType<IrBinary>().ToList();
    Assert.That(adds, Has.Count.EqualTo(1), "the two adds should have become one");
    Assert.That(adds[0].Rhs, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)adds[0].Rhs).Value, Is.EqualTo(3));
  }

  [Test]
  public void Chain_GivenOperandsInDifferentOrders_ThenGvnNumbersThemAsOne() {
    // (a + b) + c  and  (b + c) + a  are the same value written two ways
    var (fn, entry, a, b) = Function();
    var c = fn.AddParameter(new IrArgument(IrType.I16, 2, "c"));

    var left = entry.Append(new IrBinary(IrBinaryOp.Add, a, b));
    var first = entry.Append(new IrBinary(IrBinaryOp.Add, left, c));
    var right = entry.Append(new IrBinary(IrBinaryOp.Add, b, c));
    var second = entry.Append(new IrBinary(IrBinaryOp.Add, right, a));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Sub, first, second))));

    Assert.That(Gvn.Run(fn), Is.Zero, "as written, GVN cannot see the two chains are equal");

    Reassociate.Run(fn);
    Assert.That(Gvn.Run(fn), Is.GreaterThan(0), "canonicalized, the second chain should be numbered to the first");
  }

  [Test]
  public void Chain_GivenItRunsTwice_ThenTheSecondRunChangesNothing() {
    var (fn, entry, a, b) = Function();
    var inner = entry.Append(new IrBinary(IrBinaryOp.Mul, b, Const(3)));
    var outer = entry.Append(new IrBinary(IrBinaryOp.Mul, inner, a));
    entry.Append(new IrRet(outer));

    Reassociate.Run(fn);
    Assert.That(Reassociate.Run(fn), Is.Zero, "the canonical form must be a fixpoint");
  }

  [Test]
  public void Chain_GivenAFloatingPointChain_ThenItIsLeftAlone() {
    // reassociating floats changes the answer; that is a separate, opt-in optimization
    var x = new IrArgument(IrType.F64, 0, "x");
    var y = new IrArgument(IrType.F64, 1, "y");
    var fn = new IrFunction("g", IrType.F64, [x, y]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var inner = entry.Append(new IrBinary(IrBinaryOp.FAdd, x, new IrConstantFloat(IrType.F64, 1e20)));
    var outer = entry.Append(new IrBinary(IrBinaryOp.FAdd, inner, y));
    entry.Append(new IrRet(outer));

    Assert.That(Reassociate.Run(fn), Is.Zero);
  }

  [Test]
  public void Chain_GivenAnInteriorValueUsedTwice_ThenItIsNotDissolved() {
    // t = a + b is printed as well as used; flattening it away would delete a value someone needs
    var (fn, entry, a, b) = Function();
    var shared = entry.Append(new IrBinary(IrBinaryOp.Add, a, b));
    var outer = entry.Append(new IrBinary(IrBinaryOp.Add, shared, Const(1)));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Sub, outer, shared))));

    Reassociate.Run(fn);
    Assert.That(shared.Parent, Is.Not.Null, "a shared subexpression must survive");
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenTheFunctionIsSkipped() {
    var (fn, entry, a, _) = Function();
    fn.HasErrorHandler = true;
    var inner = entry.Append(new IrBinary(IrBinaryOp.Add, a, Const(1)));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, inner, Const(2)))));

    Assert.That(Reassociate.Run(fn), Is.Zero);
  }
}
