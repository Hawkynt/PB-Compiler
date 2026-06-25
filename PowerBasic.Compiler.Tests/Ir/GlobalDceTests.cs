using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Module-level global dead-code elimination: unreferenced functions and global variables are removed
/// (entry @main and everything reachable from it are kept), cascading so a function that becomes
/// unreferenced once its only caller is deleted is also removed.
/// </summary>
[TestFixture]
public sealed class GlobalDceTests {

  [Test]
  public void Run_RemovesUnreferenced_KeepsMainAndReachable() {
    var module = new IrModule("m");
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var used = module.AddFunction(new IrFunction("used", IrType.I16, [new IrArgument(IrType.I16, 0)]));
    var dead = module.AddFunction(new IrFunction("dead", IrType.I16));
    var gUsed = module.AddGlobal(new IrGlobalVariable("g_used", IrType.I16));
    module.AddGlobal(new IrGlobalVariable("g_dead", IrType.I16));

    var entry = main.CreateBlock("entry");
    entry.Append(new IrCall(IrType.I16, used, [new IrConstantInt(IrType.I16, 5)]));   // references 'used'
    entry.Append(new IrLoad(IrType.I16, gUsed));                                       // references g_used
    entry.Append(new IrRet());
    used.CreateBlock("e").Append(new IrRet(new IrConstantInt(IrType.I16, 0)));
    dead.CreateBlock("e").Append(new IrRet(new IrConstantInt(IrType.I16, 0)));         // no callers

    var removed = GlobalDce.Run(module);

    Assert.Multiple(() => {
      Assert.That(module.FindFunction("main"), Is.Not.Null, "the entry is kept");
      Assert.That(module.FindFunction("used"), Is.Not.Null, "a called function is kept");
      Assert.That(module.FindFunction("dead"), Is.Null, "an uncalled function is removed");
      Assert.That(module.FindGlobal("g_used"), Is.Not.Null, "a loaded global is kept");
      Assert.That(module.FindGlobal("g_dead"), Is.Null, "an unreferenced global is removed");
      Assert.That(removed, Is.EqualTo(2));
    });
  }

  [Test]
  public void Run_CascadesThroughChainsOfDeadFunctions() {
    // main is empty; a -> b (a calls b) but nothing calls a. Removing a frees b, so both go.
    var module = new IrModule("m");
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var a = module.AddFunction(new IrFunction("a", IrType.Void));
    var b = module.AddFunction(new IrFunction("b", IrType.Void));
    main.CreateBlock("e").Append(new IrRet());
    a.CreateBlock("e");
    a.Entry!.Append(new IrCall(IrType.Void, b, []));   // a references b
    a.Entry.Append(new IrRet());
    b.CreateBlock("e").Append(new IrRet());

    GlobalDce.Run(module);

    Assert.That(module.FindFunction("a"), Is.Null, "the uncalled function is removed");
    Assert.That(module.FindFunction("b"), Is.Null, "its now-unreferenced callee cascades away");
    Assert.That(module.FindFunction("main"), Is.Not.Null);
  }
}
