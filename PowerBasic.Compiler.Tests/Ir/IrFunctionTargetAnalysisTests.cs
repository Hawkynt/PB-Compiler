using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrFunctionTargetAnalysisTests {

  private static IrFunction Unary(IrModule module, string name) {
    var value = new IrArgument(IrType.I16, 0, "value");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [value]));
    function.CreateBlock("entry").Append(new IrRet(value));
    return function;
  }

  private static IrBasicBlock Main(IrModule module) {
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    return main.CreateBlock("entry");
  }

  [Test]
  public void VisibleFormal_GivenEveryCallerPassesSameFunction_ThenTargetSetIsCompleteSingleton() {
    var module = new IrModule("targets");
    var target = Unary(module, "Target");
    var callback = new IrArgument(IrType.Ptr, 0, "callback");
    var invoke = module.AddFunction(new IrFunction("Invoke", IrType.Void, [callback]));
    new IrBuilder(invoke.CreateBlock("entry")).Ret();

    var main = Main(module);
    main.Append(new IrCall(IrType.Void, invoke, [target]));
    main.Append(new IrCall(IrType.Void, invoke, [target]));
    main.Append(new IrRet());

    var analyses = new IrModuleAnalysisManager(module);
    var targets = analyses.Get(IrModuleAnalyses.FunctionTargets);
    var set = targets.Resolve(callback);

    Assert.Multiple(() => {
      Assert.That(set.IsComplete, Is.True);
      Assert.That(set.HasNull, Is.False);
      Assert.That(set.Functions, Is.EquivalentTo(new[] { target }));
      Assert.That(set.UniqueNonNullTarget, Is.SameAs(target));
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.True);
    });
  }

  [Test]
  public void VisibleFormal_GivenFunctionOrNull_ThenTargetSetRetainsNullAlternative() {
    var module = new IrModule("targets");
    var target = Unary(module, "Target");
    var callback = new IrArgument(IrType.Ptr, 0, "callback");
    var invoke = module.AddFunction(new IrFunction("Invoke", IrType.Void, [callback]));
    new IrBuilder(invoke.CreateBlock("entry")).Ret();

    var main = Main(module);
    main.Append(new IrCall(IrType.Void, invoke, [target]));
    main.Append(new IrCall(IrType.Void, invoke, [new IrNullPtr()]));
    main.Append(new IrRet());

    var set = new IrModuleAnalysisManager(module)
      .Get(IrModuleAnalyses.FunctionTargets)
      .Resolve(callback);

    Assert.Multiple(() => {
      Assert.That(set.IsComplete, Is.True);
      Assert.That(set.HasNull, Is.True);
      Assert.That(set.Functions, Is.EquivalentTo(new[] { target }));
      Assert.That(set.UniqueNonNullTarget, Is.Null,
        "a singleton function plus null still requires a guard");
    });
  }

  [Test]
  public void ClosedLocalCell_GivenDominatingStore_ThenStoredFunctionIsResolved() {
    var module = new IrModule("targets");
    var target = Unary(module, "Target");
    var main = Main(module);
    var storage = main.Append(new IrAlloca(IrType.Ptr));
    main.Append(new IrStore(target, storage));
    var loaded = main.Append(new IrLoad(IrType.Ptr, storage));
    main.Append(new IrRet());

    var set = new IrModuleAnalysisManager(module)
      .Get(IrModuleAnalyses.FunctionTargets)
      .Resolve(loaded);

    Assert.Multiple(() => {
      Assert.That(set.IsComplete, Is.True);
      Assert.That(set.UniqueNonNullTarget, Is.SameAs(target));
    });
  }

  [Test]
  public void ClosedLocalCell_GivenAddressEscape_ThenTargetSetIsIncomplete() {
    var module = new IrModule("targets");
    var target = Unary(module, "Target");
    var sink = module.AddFunction(new IrFunction(
      "sink", IrType.Void, [new IrArgument(IrType.Ptr, 0, "p")]));
    var main = Main(module);
    var storage = main.Append(new IrAlloca(IrType.Ptr));
    main.Append(new IrStore(target, storage));
    main.Append(new IrCall(IrType.Void, sink, [storage]));
    var loaded = main.Append(new IrLoad(IrType.Ptr, storage));
    main.Append(new IrRet());

    var set = new IrModuleAnalysisManager(module)
      .Get(IrModuleAnalyses.FunctionTargets)
      .Resolve(loaded);

    Assert.Multiple(() => {
      Assert.That(set.IsComplete, Is.False);
      Assert.That(set.UniqueNonNullTarget, Is.Null);
    });
  }

  [Test]
  public void CachedFunctionTargets_GivenCallGraphInvalidation_ThenDependencyInvalidatesTransitively() {
    var module = new IrModule("targets");
    var target = Unary(module, "Target");
    var main = Main(module);
    main.Append(new IrCall(IrType.I16, target, [new IrConstantInt(IrType.I16, 1)]));
    main.Append(new IrRet());

    var analyses = new IrModuleAnalysisManager(module);
    _ = analyses.Get(IrModuleAnalyses.FunctionTargets);

    Assert.Multiple(() => {
      Assert.That(analyses.IsCached(IrModuleAnalyses.FunctionTargets), Is.True);
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.True);
    });

    analyses.Invalidate(IrModulePreservedAnalyses.Preserve(IrModuleAnalyses.FunctionTargets));

    Assert.Multiple(() => {
      Assert.That(analyses.IsCached(IrModuleAnalyses.CallGraph), Is.False);
      Assert.That(analyses.IsCached(IrModuleAnalyses.FunctionTargets), Is.False,
        "preserving a derived target set cannot outlive its invalidated call-graph prerequisite");
    });
  }
}
