using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0048 local overwrite DSE plus O0065 whole-function private-frame DSE.</summary>
[TestFixture]
public sealed class DeadStoreElimTests {

  [Test]
  public void OverwrittenStore_WithNoInterveningLoad_IsRemoved() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.Void, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(SourceSlot(b, IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);      // dead: overwritten below before any read
    b.Store(y, p);
    b.Ret();

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Store_ObservedByALoad_IsKept() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var fn = new IrFunction("f", IrType.I32, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(SourceSlot(b, IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);
    var l = b.Load(IrType.I32, p);   // observes the first store
    b.Store(y, p);
    b.Ret(l);

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(2));
  }

  [Test]
  public void Store_AcrossACall_IsKept() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var callee = new IrFunction("g", IrType.Void);
    var fn = new IrFunction("f", IrType.Void, [x, y]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = b.Gep(SourceSlot(b, IrType.I32), IrBuilder.ConstI32(0));
    b.Store(x, p);
    b.Call(IrType.Void, callee);     // may read source-visible memory
    b.Store(y, p);
    b.Ret();

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
  }

  [Test]
  public void PartialOverwriteAtSameAddress_DoesNotKillWiderStore() {
    var word = new IrArgument(IrType.I16, 0, "word");
    var lowByte = new IrArgument(IrType.I8, 1, "lowByte");
    var fn = new IrFunction("f", IrType.Void, [word, lowByte]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var p = SourceSlot(b, IrType.I16);
    b.Store(word, p);
    b.Store(lowByte, p);             // same start, but one byte of the original word remains live
    b.Ret();

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PrivateFrameStore_WithNoReaderAcrossControlFlowAndCall_IsRemoved() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var condition = new IrArgument(IrType.I1, 1, "condition");
    var callee = new IrFunction("g", IrType.Void);
    var fn = new IrFunction("f", IrType.Void, [value, condition]);
    var entry = fn.CreateBlock("entry");
    var call = fn.CreateBlock("call");
    var exit = fn.CreateBlock("exit");
    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.I16);
    b.Store(value, slot);
    b.CondBr(condition, call, exit);
    b.Position(call);
    b.Call(IrType.Void, callee);     // cannot observe a private slot whose address never escaped
    b.Br(exit);
    b.Position(exit);
    b.Ret();

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrStore>(), Is.Empty);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PrivateFrameStore_WithAliasingLoadInSuccessor_IsKept() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var fn = new IrFunction("f", IrType.I16, [value]);
    var entry = fn.CreateBlock("entry");
    var read = fn.CreateBlock("read");
    var b = new IrBuilder(entry);
    var slot = b.Alloca(IrType.I16);
    b.Store(value, slot);
    b.Br(read);
    b.Position(read);
    var loaded = b.Load(IrType.I16, slot);
    b.Ret(loaded);

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PrivateFrameStore_WithOnlyDisjointLoad_IsRemoved() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var fn = new IrFunction("f", IrType.I16, [value]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var frame = b.Alloca(IrType.I32);
    var low = b.Gep(frame, IrBuilder.ConstI32(0));
    var high = b.Gep(frame, IrBuilder.ConstI32(2));
    b.Store(value, low);
    var loaded = b.Load(IrType.I16, high);
    b.Ret(loaded);

    var removed = DeadStoreElim.Run(fn);

    Assert.That(removed, Is.EqualTo(1));
    Assert.That(fn.AllInstructions.OfType<IrStore>(), Is.Empty);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PrivateFrameStore_WithUnknownLoadOffset_IsKept() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var offset = new IrArgument(IrType.I16, 1, "offset");
    var fn = new IrFunction("f", IrType.I16, [value, offset]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var frame = b.Alloca(IrType.I32);
    var low = b.Gep(frame, IrBuilder.ConstI32(0));
    var unknown = b.Gep(frame, offset);
    b.Store(value, low);
    var loaded = b.Load(IrType.I16, unknown);
    b.Ret(loaded);

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PrivateFrameStore_WhenPointerEscapesToCall_IsKept() {
    var value = new IrArgument(IrType.I16, 0, "value");
    var pointer = new IrArgument(IrType.Ptr, 0, "pointer");
    var callee = new IrFunction("sink", IrType.Void, [pointer]);
    var fn = new IrFunction("f", IrType.Void, [value]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var slot = b.Alloca(IrType.I16);
    b.Store(value, slot);
    b.Call(IrType.Void, callee, slot);   // the callee may observe the store through the escaped address
    b.Ret();

    Assert.That(DeadStoreElim.Run(fn), Is.EqualTo(0));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Pipeline_DoubleAssignedArrayElement_DropsTheDeadStore() {
    var unit = Parser.Parse(Lexer.Tokenize("DIM a%(0 TO 3)\na%(1) = 1\na%(1) = 2\nx% = a%(1)\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.LessThanOrEqualTo(1));  // the a%(1)=1 store is dead
  }

  private static IrAlloca SourceSlot(IrBuilder builder, IrType type) {
    var result = builder.Alloca(type);
    result.IsSourceVariable = true;
    return result;
  }
}
