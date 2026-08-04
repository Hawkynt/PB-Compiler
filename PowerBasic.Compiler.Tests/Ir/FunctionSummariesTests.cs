using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0161 — per-procedure mod/ref summaries. Two bits, deliberately: a coarse fact computed correctly
/// beats a precise one computed optimistically, and "does calling this write memory" is what the
/// consumers actually ask.
/// </summary>
[TestFixture]
public sealed class FunctionSummariesTests {

  private static IrFunction Add(IrModule module, string name, Action<IrBasicBlock, IrFunction> body) {
    var fn = module.AddFunction(new IrFunction(name, IrType.I16));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    body(entry, fn);
    entry.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));
    return fn;
  }

  [Test]
  public void Summary_GivenAFunctionThatOnlyComputes_ThenItIsPure() {
    var module = new IrModule("t");
    var fn = Add(module, "Calc", (entry, _) =>
      entry.Append(new IrBinary(IrBinaryOp.Add, new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2))));

    Assert.That(FunctionSummaries.Compute(module).For(fn).IsPure, Is.True);
  }

  [Test]
  public void Summary_GivenAFunctionThatStores_ThenItIsNotPure() {
    var module = new IrModule("t");
    var global = module.AddGlobal(new IrGlobalVariable("g.x", IrType.I16));
    var fn = Add(module, "Poke", (entry, _) => entry.Append(new IrStore(new IrConstantInt(IrType.I16, 1), global)));

    Assert.That(FunctionSummaries.Compute(module).For(fn).IsPure, Is.False);
  }

  /// <summary>Impurity travels UP the call graph: a caller of something impure is impure.</summary>
  [Test]
  public void Summary_GivenItCallsSomethingImpure_ThenItIsImpureToo() {
    var module = new IrModule("t");
    var global = module.AddGlobal(new IrGlobalVariable("g.x", IrType.I16));
    var inner = Add(module, "Poke", (entry, _) => entry.Append(new IrStore(new IrConstantInt(IrType.I16, 1), global)));
    var outer = Add(module, "Wrapper", (entry, _) => entry.Append(new IrCall(IrType.I16, inner, [])));

    Assert.That(FunctionSummaries.Compute(module).For(outer).IsPure, Is.False);
  }

  [Test]
  public void Summary_GivenAnExternalDeclaration_ThenItIsAssumedToDoAnything() {
    var module = new IrModule("t");
    var external = module.AddFunction(new IrFunction("rt_print_nl", IrType.Void));
    var caller = Add(module, "Shout", (entry, _) => entry.Append(new IrCall(IrType.Void, external, [])));

    var summaries = FunctionSummaries.Compute(module);
    Assert.That(summaries.For(external).IsPure, Is.False, "nothing here can see through it");
    Assert.That(summaries.For(caller).IsPure, Is.False);
  }

  /// <summary>
  /// A recursive pure function stays pure. The fixpoint starts from "pure" and only ever adds
  /// impurity, so a cycle settles at the union of what is reachable round it - starting from "impure"
  /// would need a proof about the cycle before entering it.
  /// </summary>
  [Test]
  public void Summary_GivenARecursivePureFunction_ThenItStaysPure() {
    var module = new IrModule("t");
    var fn = module.AddFunction(new IrFunction("Down", IrType.I16, [new IrArgument(IrType.I16, 0)]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrCall(IrType.I16, fn, [new IrConstantInt(IrType.I16, 1)]));
    entry.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.That(FunctionSummaries.Compute(module).For(fn).IsPure, Is.True);
  }

  [Test]
  public void Summary_GivenInlineAsm_ThenItIsAssumedToDoAnything() {
    var module = new IrModule("t");
    var fn = Add(module, "Raw", (entry, owner) => {
      entry.Append(new IrInlineAsm("STI"));
      owner.HasInlineAsm = true;
    });

    Assert.That(FunctionSummaries.Compute(module).For(fn).IsPure, Is.False);
  }

  [Test]
  public void DeadPureCall_GivenNothingUsesTheResult_ThenTheCallGoes() {
    var module = new IrModule("t");
    var calc = Add(module, "Calc", (entry, _) =>
      entry.Append(new IrBinary(IrBinaryOp.Add, new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2))));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry2 = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry2.Append(new IrCall(IrType.I16, calc, []));
    entry2.Append(new IrRet());

    Assert.That(FunctionSummaries.RemoveDeadPureCalls(module), Is.EqualTo(1));
    Assert.That(call.Parent, Is.Null);
  }

  [Test]
  public void DeadPureCall_GivenTheCalleePrints_ThenTheCallStays() {
    var module = new IrModule("t");
    var external = module.AddFunction(new IrFunction("rt_print_nl", IrType.Void));
    var shout = Add(module, "Shout", (entry, _) => entry.Append(new IrCall(IrType.Void, external, [])));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry2 = main.AddBlock(new IrBasicBlock("entry"));
    var call = entry2.Append(new IrCall(IrType.I16, shout, []));
    entry2.Append(new IrRet());

    Assert.That(FunctionSummaries.RemoveDeadPureCalls(module), Is.Zero);
    Assert.That(call.Parent, Is.Not.Null);
  }
}
