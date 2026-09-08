using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0271 — profile-guided indirect call promotion.</summary>
[TestFixture]
public sealed class IndirectCallPromotionTests {

  private static IrConstantInt Const(long value) => new(IrType.I16, value);

  private static (IrModule Module, IrFunction Target, IrFunction Caller, IrArgument Handler, IrCall Call, IrBinary Use)
      Program() {
    var module = new IrModule("t");
    var targetParameter = new IrArgument(IrType.I16, 0, "x");
    var target = module.AddFunction(new IrFunction("hot", IrType.I16, [targetParameter]));
    var targetEntry = target.AddBlock(new IrBasicBlock("entry"));
    targetEntry.Append(new IrRet(targetEntry.Append(new IrBinary(IrBinaryOp.Add, targetParameter, Const(1)))));

    var handler = new IrArgument(IrType.Ptr, 0, "handler");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I16, [handler]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var call = entry.Append(new IrCall(IrType.I16, handler, [Const(7)]));
    var use = entry.Append(new IrBinary(IrBinaryOp.Add, call, Const(10)));
    entry.Append(new IrRet(use));
    return (module, target, caller, handler, call, use);
  }

  [Test]
  public void Run_GivenAHotIndirectTarget_ThenBuildsGuardedDirectAndFallbackCalls() {
    var (module, target, caller, handler, call, use) = Program();
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 80)));

    Assert.That(IndirectCallPromotion.Run(module), Is.EqualTo(1));

    var calls = caller.AllInstructions.OfType<IrCall>().ToArray();
    Assert.That(calls, Has.Length.EqualTo(2));
    Assert.That(calls.Count(item => ReferenceEquals(item.Callee, target)), Is.EqualTo(1), "the hot arm must be direct");
    Assert.That(calls.Count(item => ReferenceEquals(item.Callee, handler)), Is.EqualTo(1), "the miss arm must stay indirect");
    Assert.That(call.GetIndirectTargetProfile(), Is.Null, "the fallback must not be promoted again on the next sweep");

    var branch = caller.Entry!.Terminator as IrCondBr;
    Assert.That(branch, Is.Not.Null);
    Assert.That(branch!.Condition, Is.InstanceOf<IrCmp>());
    var comparison = (IrCmp)branch.Condition;
    Assert.Multiple(() => {
      Assert.That(comparison.Pred, Is.EqualTo(IrCmpPred.Eq));
      Assert.That(comparison.Lhs, Is.SameAs(handler));
      Assert.That(comparison.Rhs, Is.SameAs(target));
      Assert.That(use.Lhs, Is.InstanceOf<IrPhi>(), "both call results must merge before the old continuation");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase(29UL, false)]
  [TestCase(30UL, true)]
  public void Run_GivenTheFirstTargetProfitabilityBoundary_ThenUsesThirtyPercent(ulong count, bool expected) {
    var (module, target, _, _, call, _) = Program();
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, count)));

    Assert.That(IndirectCallPromotion.Run(module) > 0, Is.EqualTo(expected));
  }

  [Test]
  public void Run_GivenTwoEquallyHotTargets_ThenDeclinesInsteadOfDependingOnProfileOrder() {
    var (module, target, _, _, call, _) = Program();
    var otherParameter = new IrArgument(IrType.I16, 0, "x");
    var other = module.AddFunction(new IrFunction("alsoHot", IrType.I16, [otherParameter]));
    other.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(otherParameter));
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100,
      new IrIndirectCallTarget(target, 50), new IrIndirectCallTarget(other, 50)));

    Assert.That(IndirectCallPromotion.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenAStaleTargetSignature_ThenDeclines() {
    var (module, _, _, _, call, _) = Program();
    var staleParameter = new IrArgument(IrType.I32, 0, "x");
    var stale = module.AddFunction(new IrFunction("stale", IrType.I32, [staleParameter]));
    stale.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(staleParameter));
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(stale, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenAProfileTargetFromAnotherModule_ThenDeclines() {
    var (module, _, _, _, call, _) = Program();
    var foreign = new IrFunction("foreign", IrType.I16, [new IrArgument(IrType.I16, 0, "x")]);
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(foreign, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenTheCallPrecedesASuccessorPhi_ThenRepairsItsIncomingBlockAndValue() {
    var module = new IrModule("t");
    var parameter = new IrArgument(IrType.I16, 0, "x");
    var target = module.AddFunction(new IrFunction("hot", IrType.I16, [parameter]));
    target.AddBlock(new IrBasicBlock("entry")).Append(new IrRet(parameter));

    var handler = new IrArgument(IrType.Ptr, 0, "handler");
    var caller = module.AddFunction(new IrFunction("caller", IrType.I16, [handler]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var merge = caller.AddBlock(new IrBasicBlock("merge"));
    var call = entry.Append(new IrCall(IrType.I16, handler, [Const(7)]));
    entry.Append(new IrBr(merge));
    var merged = merge.AppendPhi(new IrPhi(IrType.I16));
    merged.AddIncoming(call, entry);
    merge.Append(new IrRet(merged));
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.EqualTo(1));

    Assert.Multiple(() => {
      Assert.That(merged.IncomingBlocks.Single(), Is.Not.SameAs(entry));
      Assert.That(merged.IncomingBlocks.Single().Label, Does.Contain(".icp.cont"));
      Assert.That(merged.GetOperand(0), Is.InstanceOf<IrPhi>(), "the successor must consume the merged call result");
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [TestCase(true, false)]
  [TestCase(false, true)]
  public void Run_GivenAnOpaqueCaller_ThenLeavesItUntouched(bool hasErrorHandler, bool hasInlineAsm) {
    var (module, target, caller, _, call, _) = Program();
    caller.HasErrorHandler = hasErrorHandler;
    caller.HasInlineAsm = hasInlineAsm;
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.Zero);
  }

  [Test]
  public void Run_GivenAPromotedFallback_ThenASecondSweepIsIdempotent() {
    var (module, target, caller, _, call, _) = Program();
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.EqualTo(1));
    Assert.That(IndirectCallPromotion.Run(module), Is.Zero);
    Assert.That(caller.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(2));
  }

  [Test]
  public void Inliner_GivenAPromotedTarget_ThenCanInlineTheDirectArmWithoutRemovingTheFallback() {
    var (module, target, caller, handler, call, _) = Program();
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 90)));

    Assert.That(IndirectCallPromotion.Run(module), Is.EqualTo(1));
    Assert.That(Inliner.Run(module), Is.GreaterThan(0));

    Assert.Multiple(() => {
      Assert.That(caller.AllInstructions.OfType<IrCall>().Any(item => ReferenceEquals(item.Callee, target)), Is.False);
      Assert.That(caller.AllInstructions.OfType<IrCall>().Count(item => ReferenceEquals(item.Callee, handler)), Is.EqualTo(1));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void StandardPipeline_GivenProfiledIndirectCall_ThenRunsPromotionAsAModulePass() {
    var (module, target, caller, handler, call, _) = Program();
    call.SetIndirectTargetProfile(new IrIndirectCallProfile(100, new IrIndirectCallTarget(target, 90)));

    IrPassManager.Standard().RunOnModule(module);

    Assert.Multiple(() => {
      Assert.That(caller.AllInstructions.OfType<IrCall>().Any(item => ReferenceEquals(item.Callee, target)), Is.True);
      Assert.That(caller.AllInstructions.OfType<IrCall>().Any(item => ReferenceEquals(item.Callee, handler)), Is.True);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void Profile_GivenCountsExceedTheSiteTotal_ThenRejectsTheMalformedData() {
    var (_, target, _, _, _, _) = Program();

    Assert.That(() => new IrIndirectCallProfile(10, new IrIndirectCallTarget(target, 11)),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }
}
