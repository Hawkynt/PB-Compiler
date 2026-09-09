using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0279 — whole-program devirtualization. The pass may turn an indirect call into a direct one only
/// when the IR proves the COMPLETE target set is one non-null procedure; these tests pin both sides
/// of that boundary.
/// </summary>
[TestFixture]
public sealed class WholeProgramDevirtualizationTests {

  private static IrConstantInt Const(long value) => new(IrType.I16, value);

  private static IrFunction Unary(IrModule module, string name) {
    var value = new IrArgument(IrType.I16, 0, "value");
    var function = module.AddFunction(new IrFunction(name, IrType.I16, [value]));
    function.CreateBlock("entry").Append(new IrRet(value));
    return function;
  }

  private static (IrFunction Function, IrCall Indirect) Invoker(IrModule module) {
    var callback = new IrArgument(IrType.Ptr, 0, "callback");
    var value = new IrArgument(IrType.I16, 1, "value");
    var function = module.AddFunction(new IrFunction("Invoke", IrType.I16, [callback, value]));
    var entry = function.CreateBlock("entry");
    var indirect = entry.Append(new IrCall(IrType.I16, callback, [value]));
    entry.Append(new IrRet(indirect));
    return (function, indirect);
  }

  private static IrBasicBlock Main(IrModule module) {
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    return main.CreateBlock("entry");
  }

  [Test]
  public void Call_GivenEveryVisibleCallerPassesSameTarget_WhenDevirtualized_ThenItBecomesDirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Double");
    var (invoke, indirect) = Invoker(module);
    var entry = Main(module);
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(21)]));
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(7)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.EqualTo(1));
    Assert.That(indirect.Callee, Is.SameAs(target));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Call_GivenVisibleCallersPassDifferentTargets_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var first = Unary(module, "First");
    var second = Unary(module, "Second");
    var (invoke, indirect) = Invoker(module);
    var original = indirect.Callee;
    var entry = Main(module);
    entry.Append(new IrCall(IrType.I16, invoke, [first, Const(1)]));
    entry.Append(new IrCall(IrType.I16, invoke, [second, Const(2)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(original));
  }

  [Test]
  public void Call_GivenOneVisibleCallerCanPassNull_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var (invoke, indirect) = Invoker(module);
    var original = indirect.Callee;
    var entry = Main(module);
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(1)]));
    entry.Append(new IrCall(IrType.I16, invoke, [new IrNullPtr(), Const(2)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(original));
  }

  [Test]
  public void Call_GivenCallbackOwnerAddressEscapes_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var (invoke, indirect) = Invoker(module);
    var original = indirect.Callee;
    var escaped = module.AddGlobal(new IrGlobalVariable("escaped", IrType.Ptr));
    var entry = Main(module);
    entry.Append(new IrStore(invoke, escaped));
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(1)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(original));
  }

  [Test]
  public void Call_GivenClosedLocalCellHasOneStoredTarget_WhenDevirtualized_ThenItBecomesDirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var entry = Main(module);
    var storage = entry.Append(new IrAlloca(IrType.Ptr));
    entry.Append(new IrStore(target, storage));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, storage));
    var indirect = entry.Append(new IrCall(IrType.I16, loaded, [Const(4)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.EqualTo(1));
    Assert.That(indirect.Callee, Is.SameAs(target));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Call_GivenLocalCellMayBeReadBeforeStored_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var entry = Main(module);
    var storage = entry.Append(new IrAlloca(IrType.Ptr));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, storage));
    var indirect = entry.Append(new IrCall(IrType.I16, loaded, [Const(4)]));
    entry.Append(new IrStore(target, storage));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(loaded));
  }

  [Test]
  public void Call_GivenLocalCellAddressEscapes_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    var entry = Main(module);
    var storage = entry.Append(new IrAlloca(IrType.Ptr));
    entry.Append(new IrStore(target, storage));
    entry.Append(new IrCall(IrType.Void, sink, [storage]));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, storage));
    var indirect = entry.Append(new IrCall(IrType.I16, loaded, [Const(4)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(loaded));
  }

  [Test]
  public void Call_GivenUniqueTargetHasIncompatibleSignature_WhenDevirtualized_ThenItStaysIndirect() {
    var module = new IrModule("t");
    var wrong = module.AddFunction(new IrFunction("Wrong", IrType.Void));
    wrong.CreateBlock("entry").Append(new IrRet());
    var (invoke, indirect) = Invoker(module);
    var original = indirect.Callee;
    var entry = Main(module);
    entry.Append(new IrCall(IrType.I16, invoke, [wrong, Const(1)]));
    entry.Append(new IrRet());

    Assert.That(WholeProgramDevirtualization.Run(module), Is.Zero);
    Assert.That(indirect.Callee, Is.SameAs(original));
  }

  [Test]
  public void Pipeline_GivenSingletonCallback_WhenStandardRuns_ThenO0279RunsBeforeIpcp() {
    var module = new IrModule("t");
    var target = Unary(module, "Target");
    var (invoke, indirect) = Invoker(module);
    var entry = Main(module);
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(1)]));
    entry.Append(new IrCall(IrType.I16, invoke, [target, Const(2)]));
    entry.Append(new IrRet());

    IrPassManager.Standard().RunOnModule(module);

    Assert.That(indirect.Callee, Is.SameAs(target));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }
}
