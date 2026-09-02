using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ForwardingBlockElisionTests {

  private static IrConstantInt I16(int value) => new(IrType.I16, value);

  [Test]
  public void PhiOnlyForwarder_GivenConditionalAndUnconditionalPredecessors_TranslatesSuccessorPhi() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var fn = new IrFunction("f", IrType.I16, [choose]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var bridge = fn.CreateBlock("bridge");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrCondBr(choose, bridge, left));
    left.Append(new IrBr(bridge));

    var bridgeValue = bridge.AppendPhi(new IrPhi(IrType.I16));
    bridgeValue.AddIncoming(I16(10), entry);
    bridgeValue.AddIncoming(I16(20), left);
    bridge.Append(new IrBr(exit));

    var result = exit.AppendPhi(new IrPhi(IrType.I16));
    result.AddIncoming(bridgeValue, bridge);
    exit.Append(new IrRet(result));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var changes = SimplifyCfg.Run(fn);

    Assert.That(changes, Is.GreaterThan(0));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(bridge.Parent, Is.Null);
    Assert.That(((IrCondBr)entry.Terminator!).IfTrue, Is.SameAs(exit));
    Assert.That(((IrBr)left.Terminator!).Target, Is.SameAs(exit));
    Assert.That(result.IncomingBlocks, Is.EquivalentTo(new[] { entry, left }));
    Assert.That(((IrConstantInt)result.IncomingFrom(entry)!).Value, Is.EqualTo(10));
    Assert.That(((IrConstantInt)result.IncomingFrom(left)!).Value, Is.EqualTo(20));
  }

  [Test]
  public void Forwarder_GivenPredecessorAlreadyTargetsSuccessor_DoesNotCreateDuplicatePhiEdge() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var fn = new IrFunction("f", IrType.I16, [choose]);
    var entry = fn.CreateBlock("entry");
    var bridge = fn.CreateBlock("bridge");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrCondBr(choose, bridge, exit));
    bridge.Append(new IrBr(exit));

    var result = exit.AppendPhi(new IrPhi(IrType.I16));
    result.AddIncoming(I16(10), entry);
    result.AddIncoming(I16(20), bridge);
    exit.Append(new IrRet(result));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(bridge.Parent, Is.SameAs(fn));
    Assert.That(((IrCondBr)entry.Terminator!).IfTrue, Is.SameAs(bridge));
    Assert.That(result.IncomingBlocks, Is.EquivalentTo(new[] { entry, bridge }));
  }

  [Test]
  public void PhiOnlyForwarder_GivenPhiUsedBySuccessorInstruction_DoesNotMoveDefinition() {
    var choose = new IrArgument(IrType.I1, 0, "choose");
    var fn = new IrFunction("f", IrType.I16, [choose]);
    var entry = fn.CreateBlock("entry");
    var left = fn.CreateBlock("left");
    var bridge = fn.CreateBlock("bridge");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrCondBr(choose, bridge, left));
    left.Append(new IrBr(bridge));

    var bridgeValue = bridge.AppendPhi(new IrPhi(IrType.I16));
    bridgeValue.AddIncoming(I16(10), entry);
    bridgeValue.AddIncoming(I16(20), left);
    bridge.Append(new IrBr(exit));

    var sum = exit.Append(new IrBinary(IrBinaryOp.Add, bridgeValue, I16(1)));
    exit.Append(new IrRet(sum));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(bridge.Parent, Is.SameAs(fn), "the bridge phi cannot be deleted while a non-phi use still needs it");
    Assert.That(((IrCondBr)entry.Terminator!).IfTrue, Is.SameAs(bridge));
    Assert.That(((IrBr)left.Terminator!).Target, Is.SameAs(bridge));
  }

  [Test]
  public void Forwarder_GivenSwitchPredecessor_LeavesUnsupportedEdgeShapeAlone() {
    var selector = new IrArgument(IrType.I16, 0, "selector");
    var fn = new IrFunction("f", IrType.I16, [selector]);
    var entry = fn.CreateBlock("entry");
    var bridge = fn.CreateBlock("bridge");
    var other = fn.CreateBlock("other");
    var exit = fn.CreateBlock("exit");

    var sw = entry.Append(new IrSwitch(selector, other));
    sw.AddCase(1, bridge);
    other.Append(new IrBr(bridge));
    bridge.Append(new IrBr(exit));
    exit.Append(new IrRet(I16(7)));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    SimplifyCfg.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(bridge.Parent, Is.SameAs(fn));
    Assert.That(sw.TargetFor(1), Is.SameAs(bridge));
  }
}
