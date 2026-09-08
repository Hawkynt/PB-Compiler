using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class ProfileGuidedInliningTests {

  [Test]
  public void Run_GivenHotCallAboveStaticBudget_ThenProfileEarnsTheInline() {
    var (module, _, call) = BuildSingleCallModule(bodyInstructions: 80);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module), Is.Zero, "the ordinary 64-instruction budget must still reject it");
    Assert.That(Inliner.Run(module, callEdgeCount: c => ReferenceEquals(c, call) ? 10_000UL : null), Is.EqualTo(1));
    Assert.That(main.AllInstructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenColdCallBelowStaticBudget_ThenProfileLeavesItOutOfLine() {
    var (module, _, call) = BuildSingleCallModule(bodyInstructions: 8);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module, callEdgeCount: c => ReferenceEquals(c, call) ? 1UL : null), Is.Zero);
    Assert.That(main.AllInstructions.OfType<IrCall>().Single(), Is.SameAs(call));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenHotAndColdCallsToSameCallee_ThenBudgetIsPerCallSite() {
    var module = BuildCallee(bodyInstructions: 80, out var callee);
    var x = new IrArgument(IrType.I32, 0, "x");
    var main = new IrFunction("main", IrType.I32, [x]);
    var builder = new IrBuilder(main.CreateBlock("entry"));
    var hot = builder.Call(IrType.I32, callee, x);
    var cold = builder.Call(IrType.I32, callee, x);
    builder.Ret(builder.Add(hot, cold));
    module.AddFunction(main);

    var inlined = Inliner.Run(module, callEdgeCount: call => ReferenceEquals(call, hot) ? 10_000UL : 1UL);

    Assert.That(inlined, Is.EqualTo(1));
    Assert.That(main.AllInstructions.OfType<IrCall>().Single(), Is.SameAs(cold));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenMissingProfileForCallSite_ThenStaticBudgetIsPreserved() {
    var (module, _, _) = BuildSingleCallModule(bodyInstructions: 80);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module, callEdgeCount: _ => null), Is.Zero);
    Assert.That(main.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(1));
  }

  [Test]
  public void Run_GivenExtremelyHotHugeCallee_ThenProfileHardCapPreventsRunawayGrowth() {
    var (module, _, call) = BuildSingleCallModule(bodyInstructions: 600);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module, callEdgeCount: c => ReferenceEquals(c, call) ? ulong.MaxValue : null), Is.Zero);
    Assert.That(main.AllInstructions.OfType<IrCall>().Single(), Is.SameAs(call));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Run_GivenHotNoInlineCallee_ThenProfileCannotOverrideLegality() {
    var (module, _, call) = BuildSingleCallModule(bodyInstructions: 8, noInline: true);
    var main = module.FindFunction("main")!;

    Assert.That(Inliner.Run(module, callEdgeCount: c => ReferenceEquals(c, call) ? ulong.MaxValue : null), Is.Zero);
    Assert.That(main.AllInstructions.OfType<IrCall>().Single(), Is.SameAs(call));
  }

  private static (IrModule Module, IrFunction Callee, IrCall Call) BuildSingleCallModule(int bodyInstructions,
      bool noInline = false) {
    var module = BuildCallee(bodyInstructions, out var callee, noInline);
    var x = new IrArgument(IrType.I32, 0, "x");
    var main = new IrFunction("main", IrType.I32, [x]);
    var builder = new IrBuilder(main.CreateBlock("entry"));
    var call = builder.Call(IrType.I32, callee, x);
    builder.Ret(call);
    module.AddFunction(main);
    return (module, callee, call);
  }

  private static IrModule BuildCallee(int bodyInstructions, out IrFunction callee, bool noInline = false) {
    var module = new IrModule("T");
    var n = new IrArgument(IrType.I32, 0, "n");
    callee = new IrFunction("abstraction", IrType.I32, [n]) { NoInline = noInline };
    var builder = new IrBuilder(callee.CreateBlock("entry"));
    IrValue value = n;
    for (var i = 0; i < bodyInstructions; ++i)
      value = builder.Add(value, new IrConstantInt(IrType.I32, i + 1));
    builder.Ret(value);
    module.AddFunction(callee);
    return module;
  }
}
