using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0018 / O0159 — interprocedural constant propagation. The interesting cases are the ones it must
/// DECLINE: an interprocedural fact is only sound when the module can enumerate every call, and each
/// of these tests names one way that can fail.
/// </summary>
[TestFixture]
public sealed class IpConstantPropTests {

  private static IrConstantInt Const(long v) => new(IrType.I16, v);

  /// <summary>A callee taking one i16 and returning it doubled, plus a main that calls it.</summary>
  private static (IrModule Module, IrFunction Callee, IrArgument Param, IrFunction Main) Program(params long[] argumentsPassed) {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [parameter]));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    body.Append(new IrRet(body.Append(new IrBinary(IrBinaryOp.Add, parameter, parameter))));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    foreach (var value in argumentsPassed)
      entry.Append(new IrCall(IrType.I16, callee, [Const(value)]));
    entry.Append(new IrRet());
    return (module, callee, parameter, main);
  }

  [Test]
  public void Parameter_GivenEveryCallPassesTheSameConstant_ThenItBecomesThatConstant() {
    var (module, _, parameter, _) = Program(7, 7, 7);

    Assert.That(IpConstantProp.Run(module), Is.GreaterThan(0));
    Assert.That(parameter.HasNoUsers, Is.True, "every use of the parameter should have been replaced");
  }

  [Test]
  public void Parameter_GivenTheCallsDisagree_ThenItIsLeftAlone() {
    var (module, _, parameter, _) = Program(7, 8);

    IpConstantProp.Run(module);
    Assert.That(parameter.HasNoUsers, Is.False);
  }

  [Test]
  public void Parameter_GivenACallPassesANonConstant_ThenItIsLeftAlone() {
    var (module, callee, parameter, main) = Program(7);
    var other = main.AddParameter(new IrArgument(IrType.I16, 0, "outside"));
    main.Entry!.InsertAt(0, new IrCall(IrType.I16, callee, [other]));

    IpConstantProp.Run(module);
    Assert.That(parameter.HasNoUsers, Is.False);
  }

  [Test]
  public void Function_GivenItsAddressIsTaken_ThenNothingIsPropagated() {
    var (module, callee, parameter, main) = Program(7, 7);
    // the address flows into a call ARGUMENT: whoever receives it can call it with anything
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    main.Entry!.InsertAt(0, new IrCall(IrType.Void, sink, [callee]));

    IpConstantProp.Run(module);
    Assert.That(parameter.HasNoUsers, Is.False, "an escaped address means the call sites are not enumerable");
  }

  [Test]
  public void Entry_GivenMainItself_ThenItsParametersAreNeverAssumed() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "n");
    var main = module.AddFunction(new IrFunction("main", IrType.I16, [parameter]));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, parameter, Const(1)))));

    IpConstantProp.Run(module);
    Assert.That(parameter.HasNoUsers, Is.False, "the runtime calls main; this module does not see that call");
  }

  [Test]
  public void Result_GivenEveryReturnIsTheSameConstant_ThenCallSitesUseIt() {
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    body.Append(new IrRet(Const(42)));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.I16, callee, []));
    var use = entry.Append(new IrBinary(IrBinaryOp.Add, call, Const(1)));
    entry.Append(new IrRet(use));

    Assert.That(IpConstantProp.Run(module), Is.GreaterThan(0));
    Assert.That(use.Lhs, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)use.Lhs).Value, Is.EqualTo(42));
    Assert.That(call.Parent, Is.Not.Null, "the call must stay - the body may still have effects");
  }

  [Test]
  public void Result_GivenTheReturnsDisagree_ThenTheCallResultIsKept() {
    var module = new IrModule("t");
    var callee = module.AddFunction(new IrFunction("f", IrType.I16, [new IrArgument(IrType.I1, 0, "c")]));
    var entryBlock = callee.AddBlock(new IrBasicBlock("entry"));
    var yes = callee.AddBlock(new IrBasicBlock("yes"));
    var no = callee.AddBlock(new IrBasicBlock("no"));
    entryBlock.Append(new IrCondBr(callee.Parameters[0], yes, no));
    yes.Append(new IrRet(Const(1)));
    no.Append(new IrRet(Const(2)));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var mainEntry = main.AddBlock(new IrBasicBlock("entry"));
    var call = mainEntry.Append(new IrCall(IrType.I16, callee, [new IrConstantInt(IrType.I1, 1)]));
    mainEntry.Append(new IrRet(call));

    IpConstantProp.Run(module);
    Assert.That(call.HasNoUsers, Is.False);
  }

  [Test]
  public void Propagation_GivenAChainOfCalls_ThenItReachesTheFixpoint() {
    // main -> outer(5) -> inner(n) ; inner returns 9, outer returns what inner returned
    var module = new IrModule("t");
    var inner = module.AddFunction(new IrFunction("inner", IrType.I16, [new IrArgument(IrType.I16, 0, "n")]));
    inner.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(Const(9)));

    var outerParam = new IrArgument(IrType.I16, 0, "m");
    var outer = module.AddFunction(new IrFunction("outer", IrType.I16, [outerParam]));
    var outerEntry = outer.AddBlock(new IrBasicBlock("entry"));
    var forwarded = outerEntry.Append(new IrCall(IrType.I16, inner, [outerParam]));
    outerEntry.Append(new IrRet(forwarded));

    var main = module.AddFunction(new IrFunction("main", IrType.I16));
    var mainEntry = main.AddBlock(new IrBasicBlock("entry"));
    var call = mainEntry.Append(new IrCall(IrType.I16, outer, [Const(5)]));
    var use = mainEntry.Append(new IrBinary(IrBinaryOp.Add, call, Const(0)));
    mainEntry.Append(new IrRet(use));

    IpConstantProp.Run(module);
    Assert.That(use.Lhs, Is.InstanceOf<IrConstantInt>(), "inner's 9 should have travelled out through outer");
    Assert.That(((IrConstantInt)use.Lhs).Value, Is.EqualTo(9));
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var (module, callee, parameter, _) = Program(7, 7);
    callee.HasErrorHandler = true;

    IpConstantProp.Run(module);
    Assert.That(parameter.HasNoUsers, Is.False);
  }

  /// <summary>
  /// The pass makes conditions constant, and a constant condition on a SELECT has to be folded or
  /// the back end sees an immediate where it needs a register - which is how this showed up: three
  /// corpus functions stopped being selectable the moment interprocedural propagation started
  /// working. The rule lives in InstCombine; this is the interprocedural end of it.
  /// </summary>
  [Test]
  public void Select_GivenAPropagatedConstantCondition_ThenTheSelectFoldsAway() {
    var module = new IrModule("t");
    var flag = new IrArgument(IrType.I1, 0, "flag");
    var callee = module.AddFunction(new IrFunction("pick", IrType.I16, [flag]));
    var body = callee.AddBlock(new IrBasicBlock("entry"));
    var pick = body.Append(new IrSelect(flag, Const(10), Const(20)));
    body.Append(new IrRet(pick));

    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.I16, callee, [new IrConstantInt(IrType.I1, 1)]));
    entry.Append(new IrRet());

    IpConstantProp.Run(module);
    InstCombine.Run(callee);

    Assert.That(pick.Parent, Is.Null, "the select should be gone");
    Assert.That(((IrRet)callee.Entry!.Terminator!).Value, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrRet)callee.Entry!.Terminator!).Value!).Value, Is.EqualTo(10));
  }
}
