using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class PhiBranchFoldingTests {

  private static IrConstantInt Bool(bool value) => new(IrType.I1, value ? 1 : 0);
  private static IrConstantInt I16(int value) => new(IrType.I16, value);

  [Test]
  public void PhiCondition_GivenConstantIncomingEdges_ThreadsBothPredecessors() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var fn = new IrFunction("f", IrType.I16, [choose]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var test = fn.CreateBlock("test");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");

    entry.Append(new IrCondBr(choose, left, right));
    left.Append(new IrBr(test));
    right.Append(new IrBr(test));
    var condition = test.AppendPhi(new IrPhi(IrType.I1));
    condition.AddIncoming(Bool(true), left);
    condition.AddIncoming(Bool(false), right);
    test.Append(new IrCondBr(condition, yes, no));
    yes.Append(new IrRet(I16(11)));
    no.Append(new IrRet(I16(22)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var changes = SimplifyCfg.Run(fn);

    Assert.That(changes, Is.GreaterThan(0));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(test.Parent, Is.Null, "the now-unreachable decision block should be collected");
    var branch = entry.Terminator as IrCondBr;
    Assert.That(branch, Is.Not.Null);
    Assert.That((branch!.IfTrue.Terminator as IrRet)?.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrRet)branch.IfTrue.Terminator!).Value!).Value, Is.EqualTo(11));
    Assert.That((branch.IfFalse.Terminator as IrRet)?.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrRet)branch.IfFalse.Terminator!).Value!).Value, Is.EqualTo(22));
  }

  [Test]
  public void PhiCondition_GivenOneConstantIncomingEdge_TranslatesSuccessorPhi() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var unknown = new IrArgument(IrType.I1, 1, "unknown");
    var fn = new IrFunction("f", IrType.I16, [choose, unknown]);
    var entry = fn.CreateBlock("entry");
    var known = fn.CreateBlock("known");
    var dynamic = fn.CreateBlock("dynamic");
    var test = fn.CreateBlock("test");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");

    entry.Append(new IrCondBr(choose, known, dynamic));
    known.Append(new IrBr(test));
    dynamic.Append(new IrBr(test));

    var condition = test.AppendPhi(new IrPhi(IrType.I1));
    condition.AddIncoming(Bool(true), known);
    condition.AddIncoming(unknown, dynamic);
    var value = test.AppendPhi(new IrPhi(IrType.I16));
    value.AddIncoming(I16(10), known);
    value.AddIncoming(I16(20), dynamic);
    test.Append(new IrCondBr(condition, yes, no));

    var result = yes.AppendPhi(new IrPhi(IrType.I16));
    result.AddIncoming(value, test);
    yes.Append(new IrRet(result));
    no.Append(new IrRet(I16(-1)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(known.Successors.Single(), Is.SameAs(yes));
    var resultPhi = yes.Phis.Single();
    Assert.That(resultPhi.IncomingFrom(known), Is.TypeOf<IrConstantInt>(),
      "the bypass edge must receive the predecessor-specific value, not the phi defined in test");
    Assert.That(((IrConstantInt)resultPhi.IncomingFrom(known)!).Value, Is.EqualTo(10));
    Assert.That(resultPhi.IncomingBlocks, Does.Contain(known));
  }

  [Test]
  public void PhiCondition_GivenExecutableWorkInJoin_DoesNotBypassIt() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var fn = new IrFunction("f", IrType.I16, [choose]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var right = fn.CreateBlock("right");
    var test = fn.CreateBlock("test");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");

    entry.Append(new IrCondBr(choose, left, right));
    left.Append(new IrBr(test));
    right.Append(new IrBr(test));
    var condition = test.AppendPhi(new IrPhi(IrType.I1));
    condition.AddIncoming(Bool(true), left);
    condition.AddIncoming(Bool(false), right);
    test.Append(new IrBinary(IrBinaryOp.Add, I16(1), I16(2)));
    test.Append(new IrCondBr(condition, yes, no));
    yes.Append(new IrRet(I16(1)));
    no.Append(new IrRet(I16(0)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That((left.Terminator as IrBr)?.Target, Is.SameAs(test));
    Assert.That((right.Terminator as IrBr)?.Target, Is.SameAs(test));
    Assert.That(test.Parent, Is.SameAs(fn));
  }
}
