using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0278 — global variable localization. The interesting condition is not "only one function uses
/// it", which is insufficient on its own: a global keeps its value between calls and a local does
/// not, so the pass also has to prove the incoming value is dead.
/// </summary>
[TestFixture]
public sealed class LocalizeGlobalsTests {

  private static (IrModule Module, IrGlobalVariable Global, IrFunction Fn, IrBasicBlock Entry) Program() {
    var module = new IrModule("t");
    var global = module.AddGlobal(new IrGlobalVariable("g.temp", IrType.I16));
    var fn = module.AddFunction(new IrFunction("Work", IrType.Void));
    return (module, global, fn, fn.AddBlock(new IrBasicBlock("entry")));
  }

  private static IrConstantInt Const(long v) => new(IrType.I16, v);

  [Test]
  public void Global_GivenOneFunctionThatWritesFirst_ThenItBecomesALocal() {
    var (module, global, fn, entry) = Program();
    entry.Append(new IrStore(Const(0), global));
    entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.EqualTo(1));
    Assert.That(module.Globals, Does.Not.Contain(global));
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().ToList(), Has.Count.EqualTo(1));
  }

  /// <summary>
  /// The load comes first, so the procedure can observe what the PREVIOUS call left in the global -
  /// which a local would not remember. This is the case that makes "only one user" insufficient.
  /// </summary>
  [Test]
  public void Global_GivenItIsReadBeforeItIsWritten_ThenItStaysGlobal() {
    var (module, global, _, entry) = Program();
    var read = entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrStore(new IrBinary(IrBinaryOp.Add, read, Const(1)), global));
    entry.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.Zero);
    Assert.That(module.Globals, Does.Contain(global));
  }

  [Test]
  public void Global_GivenTwoFunctionsTouchIt_ThenItStaysGlobal() {
    var (module, global, _, entry) = Program();
    entry.Append(new IrStore(Const(0), global));
    entry.Append(new IrRet());

    var other = module.AddFunction(new IrFunction("Peek", IrType.I16));
    var otherEntry = other.AddBlock(new IrBasicBlock("entry"));
    otherEntry.Append(new IrRet(otherEntry.Append(new IrLoad(IrType.I16, global))));

    Assert.That(LocalizeGlobals.Run(module), Is.Zero);
    Assert.That(module.Globals, Does.Contain(global));
  }

  [Test]
  public void Global_GivenItsAddressReachesACall_ThenItStaysGlobal() {
    var (module, global, _, entry) = Program();
    entry.Append(new IrStore(Const(0), global));
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    entry.Append(new IrCall(IrType.Void, sink, [global]));
    entry.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.Zero);
  }

  [Test]
  public void Global_GivenTheStoreIsNotInTheEntryBlock_ThenItStaysGlobal() {
    var (module, global, fn, entry) = Program();
    var later = fn.AddBlock(new IrBasicBlock("later"));
    entry.Append(new IrBr(later));
    later.Append(new IrStore(Const(0), global));
    later.Append(new IrLoad(IrType.I16, global));
    later.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.Zero,
      "a store that does not dominate every load proves nothing about the incoming value");
  }

  [Test]
  public void Global_GivenTheUserHasAnArmedErrorHandler_ThenItStaysGlobal() {
    var (module, global, fn, entry) = Program();
    fn.HasErrorHandler = true;
    entry.Append(new IrStore(Const(0), global));
    entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.Zero);
  }
}
