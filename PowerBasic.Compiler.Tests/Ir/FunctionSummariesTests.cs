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

  /// <summary>
  /// The checked exception to "a declaration is a wall": the float math intrinsics take their
  /// arguments by value, so there is no memory they could reach - and a caller of one is no less
  /// pure than it was.
  /// </summary>
  [Test]
  public void Summary_GivenAMathIntrinsicDeclaration_ThenItIsPure() {
    var module = new IrModule("t");
    var sqrt = module.AddFunction(new IrFunction("llvm.sqrt.f80", IrType.F80, [new IrArgument(IrType.F80, 0)]));
    var caller = Add(module, "Root", (entry, _) =>
      entry.Append(new IrCall(IrType.F80, sqrt, [new IrConstantFloat(IrType.F80, 2)])));

    var summaries = FunctionSummaries.Compute(module);
    Assert.That(summaries.For(sqrt).IsPure, Is.True);
    Assert.That(summaries.For(caller).IsPure, Is.True);
  }

  /// <summary>
  /// The rows that are NOT on the list, and the reason the list is worth having: a string entry
  /// consumes or allocates a handle, so it can neither be merged with another nor moved.
  /// </summary>
  [TestCase("rt_str_len")]
  [TestCase("rt_str_dup")]
  [TestCase("rt_print_nl")]
  [TestCase("llvm.memcpy.p0.p0.i32")]
  public void Summary_GivenARuntimeEntryThatIsNotPure_ThenItIsNotOnTheList(string name) {
    Assert.That(FunctionSummaries.IsPureExternal(name), Is.False);
    Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.False);
  }

  [TestCase("llvm.sqrt.f64")]
  [TestCase("llvm.sin.f32")]
  [TestCase("llvm.pow.f80")]
  public void Summary_GivenAMathIntrinsic_ThenItIsPureAtEveryWidth(string name) {
    Assert.That(FunctionSummaries.IsPureExternal(name), Is.True);
    Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.True);
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
