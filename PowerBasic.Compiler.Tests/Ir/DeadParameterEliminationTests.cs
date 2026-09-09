using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0069 — dead formal elimination and bounded literal call-shape cloning.</summary>
[TestFixture]
public sealed class DeadParameterEliminationTests {

  private static IrConstantInt Const(long value) => new(IrType.I16, value);

  [Test]
  public void Parameter_GivenItIsUnread_ThenTheSignatureAndEveryOwnedCallShrink() {
    var module = new IrModule("t");
    var dead = new IrArgument(IrType.I16, 0, "dead");
    var live = new IrArgument(IrType.I16, 1, "live");
    var callee = module.AddFunction(new IrFunction("pick", IrType.I16, [dead, live]));
    callee.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(live));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.I16, callee, [Const(11), Const(22)]));
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.GreaterThan(0));
    Assert.That(callee.Parameters, Has.Count.EqualTo(1));
    Assert.That(callee.Parameters[0], Is.SameAs(live));
    Assert.That(live.Index, Is.Zero);
    Assert.That(dead.Parent, Is.Null);
    Assert.That(call.ArgCount, Is.EqualTo(1));
    Assert.That(((IrConstantInt)call.Args.Single()).Value, Is.EqualTo(22));
    Assert.That(IrVerifier.Verify(callee), Is.Empty);
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Parameter_GivenItsArgumentHasEffects_ThenTheExpressionStillExecutesButIsNotPassed() {
    var module = new IrModule("t");
    var effect = module.AddFunction(new IrFunction("effect", IrType.I16));
    effect.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(Const(7)));

    var ignored = new IrArgument(IrType.I16, 0, "ignored");
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [ignored]));
    sink.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var effectCall = entry.Append(new IrCall(IrType.I16, effect, []));
    var sinkCall = entry.Append(new IrCall(IrType.Void, sink, [effectCall]));
    entry.Append(new IrRet());

    DeadParameterElimination.Run(module);

    Assert.That(sinkCall.ArgCount, Is.Zero);
    Assert.That(effectCall.Parent, Is.SameAs(entry), "dropping an argument must not drop its evaluation");
    var instructions = entry.Instructions.ToList();
    Assert.That(instructions.IndexOf(effectCall), Is.LessThan(instructions.IndexOf(sinkCall)));
    Assert.That(IrVerifier.Verify(main), Is.Empty);
  }

  [Test]
  public void Parameter_GivenAForwardingChain_ThenDeadnessPropagatesToTheFixpoint() {
    var module = new IrModule("t");
    var sinkParameter = new IrArgument(IrType.I16, 0, "sinkValue");
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [sinkParameter]));
    sink.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());

    var bridgeParameter = new IrArgument(IrType.I16, 0, "bridgeValue");
    var bridge = module.AddFunction(new IrFunction("bridge", IrType.Void, [bridgeParameter]));
    var bridgeEntry = bridge.AddBlock(new IrBasicBlock("entry"));
    var forwarded = bridgeEntry.Append(new IrCall(IrType.Void, sink, [bridgeParameter]));
    bridgeEntry.Append(new IrRet());

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var mainEntry = main.AddBlock(new IrBasicBlock("entry"));
    var outerCall = mainEntry.Append(new IrCall(IrType.Void, bridge, [Const(3)]));
    mainEntry.Append(new IrRet());

    DeadParameterElimination.Run(module);

    Assert.That(sink.Parameters, Is.Empty);
    Assert.That(bridge.Parameters, Is.Empty);
    Assert.That(forwarded.ArgCount, Is.Zero);
    Assert.That(outerCall.ArgCount, Is.Zero);
  }

  [Test]
  public void Function_GivenItsAddressEscapes_ThenItsAbiIsLeftAlone() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "unused");
    var callee = module.AddFunction(new IrFunction("callee", IrType.Void, [parameter]));
    callee.AddBlock(new IrBasicBlock("entry")).Append(new IrRet());
    var escape = module.AddFunction(new IrFunction("escape", IrType.Void, [new IrArgument(IrType.Ptr, 0, "p")]));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var direct = entry.Append(new IrCall(IrType.Void, callee, [Const(1)]));
    entry.Append(new IrCall(IrType.Void, escape, [callee]));
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.Zero);
    Assert.That(callee.Parameters, Has.Count.EqualTo(1));
    Assert.That(direct.ArgCount, Is.EqualTo(1));
  }

  [Test]
  public void CallShape_GivenAStrictMajorityLiteralShape_ThenItGetsOneSpecializedClone() {
    var module = new IrModule("t");
    var mode = new IrArgument(IrType.I16, 0, "mode");
    var value = new IrArgument(IrType.I16, 1, "value");
    var callee = module.AddFunction(new IrFunction("pick", IrType.I16, [mode, value]));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    var isText = body.Append(new IrCmp(IrCmpPred.Eq, mode, Const(1)));
    var selected = body.Append(new IrSelect(isText, value, Const(-1)));
    body.Append(new IrRet(selected));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var calls = new[] {
      entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(10)])),
      entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(20)])),
      entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(30)])),
      entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(40)])),
      entry.Append(new IrCall(IrType.I16, callee, [Const(2), Const(50)])),
    };
    entry.Append(new IrRet());

    Assert.That(DeadParameterElimination.Run(module), Is.GreaterThan(0));

    var clone = module.Functions.Single(function => function.Name.Contains("$o0069$shape", StringComparison.Ordinal));
    Assert.That(clone.Parameters, Has.Count.EqualTo(1));
    Assert.That(clone.Parameters[0].Name, Is.EqualTo("value"));
    Assert.That(clone.Parameters[0].Index, Is.Zero);
    foreach (var call in calls[..4]) {
      Assert.That(call.Callee, Is.SameAs(clone));
      Assert.That(call.ArgCount, Is.EqualTo(1));
    }
    Assert.That(calls[4].Callee, Is.SameAs(callee));
    Assert.That(calls[4].ArgCount, Is.EqualTo(2));

    var clonedComparison = clone.AllInstructions.OfType<IrCmp>().Single();
    Assert.That(clonedComparison.Lhs, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)clonedComparison.Lhs).Value, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(clone), Is.Empty);

    DeadParameterElimination.Run(module);
    Assert.That(module.Functions.Count(function => function.Name.Contains("$o0069$shape", StringComparison.Ordinal)), Is.EqualTo(1),
      "a second pass must not keep cloning the minority shapes");
  }

  [Test]
  public void CallShape_GivenNoStrictMajority_ThenNoCloneIsCreated() {
    var module = new IrModule("t");
    var mode = new IrArgument(IrType.I16, 0, "mode");
    var value = new IrArgument(IrType.I16, 1, "value");
    var callee = module.AddFunction(new IrFunction("pick", IrType.I16, [mode, value]));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    var sum = body.Append(new IrBinary(IrBinaryOp.Add, mode, value));
    body.Append(new IrRet(sum));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(10)]));
    entry.Append(new IrCall(IrType.I16, callee, [Const(1), Const(20)]));
    entry.Append(new IrCall(IrType.I16, callee, [Const(2), Const(30)]));
    entry.Append(new IrCall(IrType.I16, callee, [Const(2), Const(40)]));
    entry.Append(new IrRet());

    DeadParameterElimination.Run(module);

    Assert.That(module.Functions.Any(function => function.Name.Contains("$o0069$shape", StringComparison.Ordinal)), Is.False);
  }

  [Test]
  public void Ipcp_GivenItMakesAParameterDead_ThenO0069ShrinksTheOwnedAbi() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var callee = module.AddFunction(new IrFunction("twice", IrType.I16, [parameter]));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    body.Append(new IrRet(body.Append(new IrBinary(IrBinaryOp.Add, parameter, parameter))));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var first = entry.Append(new IrCall(IrType.I16, callee, [Const(7)]));
    var second = entry.Append(new IrCall(IrType.I16, callee, [Const(7)]));
    entry.Append(new IrRet());

    IpConstantProp.Run(module);

    Assert.That(callee.Parameters, Is.Empty);
    Assert.That(first.ArgCount, Is.Zero);
    Assert.That(second.ArgCount, Is.Zero);
  }
}
