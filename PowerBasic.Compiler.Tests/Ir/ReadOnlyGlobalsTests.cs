using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0165 — read-only global propagation. As with the other interprocedural passes, the cases that
/// matter are the ones it must decline: "nothing ever writes this" is only worth anything when the
/// module can see every write.
/// </summary>
[TestFixture]
public sealed class ReadOnlyGlobalsTests {

  private static (IrModule Module, IrGlobalVariable Global, IrFunction Main, IrBasicBlock Entry) Program() {
    var module = new IrModule("t");
    var global = module.AddGlobal(new IrGlobalVariable("g.flag", IrType.I16));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    return (module, global, main, main.AddBlock(new IrBasicBlock("entry")));
  }

  [Test]
  public void Global_GivenNothingWritesIt_ThenEveryReadFoldsToZero() {
    var (module, global, _, entry) = Program();
    var read = entry.Append(new IrLoad(IrType.I16, global));
    var use = entry.Append(new IrBinary(IrBinaryOp.Add, read, new IrConstantInt(IrType.I16, 1)));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.EqualTo(1));
    Assert.That(use.Lhs, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)use.Lhs).Value, Is.Zero, "PB zero-initializes, so an unwritten global reads zero");
  }

  [Test]
  public void Global_GivenAnyStore_ThenItIsLeftAlone() {
    var (module, global, _, entry) = Program();
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), global));
    var read = entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero);
    Assert.That(read.Parent, Is.Not.Null);
  }

  [Test]
  public void Global_GivenItsAddressReachesACall_ThenItIsLeftAlone() {
    var (module, global, _, entry) = Program();
    var sink = module.AddFunction(new IrFunction("sink", IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    entry.Append(new IrCall(IrType.Void, sink, [global]));    // the callee may write through it
    var read = entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero);
    Assert.That(read.Parent, Is.Not.Null);
  }

  [Test]
  public void Global_GivenAnInitializedBlob_ThenItIsLeftAlone() {
    var module = new IrModule("t");
    var literal = module.AddStringConstant("hi"u8.ToArray());
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var read = entry.Append(new IrLoad(IrType.I8, literal));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero, "its bytes are not zero, and they are not one value either");
    Assert.That(read.Parent, Is.Not.Null);
  }

  [Test]
  public void Global_GivenAnArrayGlobal_ThenItIsLeftAlone() {
    var module = new IrModule("t");
    var array = module.AddGlobal(new IrGlobalVariable("g.table", IrType.I16) { Count = 8 });
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrLoad(IrType.I16, array));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero, "element zero is not the whole array");
  }

  [Test]
  public void Global_GivenAReaderWithAnArmedErrorHandler_ThenItIsLeftAlone() {
    var (module, global, main, entry) = Program();
    main.HasErrorHandler = true;
    entry.Append(new IrLoad(IrType.I16, global));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero,
      "a fault enters where the CFG shows no edge, so a store on that path would be missed");
  }

  /// <summary>
  /// A RUNTIME cell is written by hand-written assembly the IR cannot see, so "no stores" says nothing
  /// about it. <c>rt_col</c> is the print column and <c>rt_err</c> the last error code: the IR only
  /// ever reads them, which makes them look like constants and they are the opposite - they are the
  /// part of the program state that changes without the IR touching it.
  ///
  /// This is a regression test for a measured miscompile. Folding <c>rt_col</c> to zero made
  /// <c>POS(0)</c> answer 1 forever, which DIFF111 caught the moment it started routing.
  /// </summary>
  [Test]
  public void Global_GivenARuntimeCell_ThenItIsNeverFoldedHoweverFewStoresAreVisible() {
    var module = new IrModule("t");
    var column = module.AddGlobal(new IrGlobalVariable("rt_col", IrType.I16));
    var main = module.AddFunction(new IrFunction("main", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    var read = entry.Append(new IrLoad(IrType.I16, column));
    entry.Append(new IrRet());

    Assert.That(ReadOnlyGlobals.Run(module), Is.Zero);
    Assert.That(read.Parent, Is.Not.Null, "the runtime writes it; this pass simply cannot see that");
  }

  [Test]
  public void Global_GivenARuntimeCell_ThenLocalizationLeavesItAloneToo() {
    var module = new IrModule("t");
    var column = module.AddGlobal(new IrGlobalVariable("rt_col", IrType.I16));
    var main = module.AddFunction(new IrFunction("Work", IrType.Void));
    var entry = main.AddBlock(new IrBasicBlock("entry"));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 0), column));
    entry.Append(new IrLoad(IrType.I16, column));
    entry.Append(new IrRet());

    Assert.That(LocalizeGlobals.Run(module), Is.Zero,
      "a cell the runtime shares cannot become one procedure's local");
    Assert.That(module.Globals, Does.Contain(column));
  }
}
