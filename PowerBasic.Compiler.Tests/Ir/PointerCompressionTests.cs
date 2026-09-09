using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class PointerCompressionTests {

  [Test]
  public void NarrowRegionIndex_IsZeroExtendedBeforeEncoding() {
    var index = new IrArgument(IrType.U8, 0, "index");
    var fn = new IrFunction("f", IrType.Ptr, [index]);
    var entry = fn.CreateBlock("entry");
    var region = entry.Append(new IrAlloca(IrType.I16) { Count = 256, Name = "nodes" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 8, Name = "next" });
    var slot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 3), IrType.Ptr));
    var target = entry.Append(new IrGep(region, index, IrType.I16));
    entry.Append(new IrStore(target, slot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(PointerCompression.Run(fn, pointerBits: 32), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCast>().Any(c => c.Op == IrCastOp.ZExt
      && ReferenceEquals(c.Value, index) && c.Type == IrType.U16), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrStore>().Where(s => s.Pointer is IrGep { BasePtr: IrAlloca a }
      && a.Name == "next.compressed").All(s => s.Value.Type.SameStorage(IrType.U16)), Is.True);
  }

  [Test]
  public void NullPointer_UsesZeroEncodingSoZeroInitializedStorageRemainsNull() {
    var fn = new IrFunction("f", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var region = entry.Append(new IrAlloca(IrType.I16) { Count = 32, Name = "nodes" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 8, Name = "next" });
    var liveSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 0), IrType.Ptr));
    var nullSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 1), IrType.Ptr));
    var target = entry.Append(new IrGep(region, new IrConstantInt(IrType.U16, 7), IrType.I16));
    entry.Append(new IrStore(target, liveSlot));
    entry.Append(new IrStore(new IrNullPtr(), nullSlot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, nullSlot));
    entry.Append(new IrRet(loaded));

    Assert.That(PointerCompression.Run(fn, pointerBits: 32), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrStore>().Any(s => s.Value is IrConstantInt { Type: var type, ZeroExtended: 0 }
      && type == IrType.U16), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrCmp>().Any(c => c.Pred == IrCmpPred.Eq
      && c.Rhs is IrConstantInt { Type: var type, ZeroExtended: 0 } && type == IrType.U16), Is.True);
  }

  [Test]
  public void LargestEncodableRegionIndex_IsAcceptedButNextIndexIsRejected() {
    Assert.That(RunForIndex(ushort.MaxValue - 1), Is.EqualTo(1));
    Assert.That(RunForIndex(ushort.MaxValue), Is.Zero);
  }

  [Test]
  public void PointersIntoDifferentRegions_AreNotCompressed() {
    var fn = new IrFunction("f", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var firstRegion = entry.Append(new IrAlloca(IrType.I16) { Count = 16, Name = "first" });
    var secondRegion = entry.Append(new IrAlloca(IrType.I16) { Count = 16, Name = "second" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 8, Name = "next" });
    var firstSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 0), IrType.Ptr));
    var secondSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 1), IrType.Ptr));
    entry.Append(new IrStore(entry.Append(new IrGep(firstRegion, new IrConstantInt(IrType.U16, 1), IrType.I16)), firstSlot));
    entry.Append(new IrStore(entry.Append(new IrGep(secondRegion, new IrConstantInt(IrType.U16, 2), IrType.I16)), secondSlot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, firstSlot));
    entry.Append(new IrRet(loaded));

    Assert.That(PointerCompression.Run(fn, pointerBits: 32), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "next.compressed"), Is.False);
  }

  [Test]
  public void DirectRootAccessAliasingElementZero_MakesCompressionDecline() {
    var fn = new IrFunction("f", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var region = entry.Append(new IrAlloca(IrType.I16) { Count = 16, Name = "nodes" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 8, Name = "next" });
    var firstTarget = entry.Append(new IrGep(region, new IrConstantInt(IrType.U16, 1), IrType.I16));
    var secondTarget = entry.Append(new IrGep(region, new IrConstantInt(IrType.U16, 2), IrType.I16));
    entry.Append(new IrStore(firstTarget, pointers));
    var firstSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 0), IrType.Ptr));
    var secondSlot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 1), IrType.Ptr));
    entry.Append(new IrStore(secondTarget, secondSlot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, firstSlot));
    entry.Append(new IrRet(loaded));

    Assert.That(PointerCompression.Run(fn, pointerBits: 32), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "next.compressed"), Is.False);
  }

  private static int RunForIndex(long index) {
    var fn = new IrFunction("f", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var region = entry.Append(new IrAlloca(IrType.I16) { Count = ushort.MaxValue, Name = "nodes" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 2, Name = "next" });
    var slot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 0), IrType.Ptr));
    var target = entry.Append(new IrGep(region, new IrConstantInt(IrType.U16, index), IrType.I16));
    entry.Append(new IrStore(target, slot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, slot));
    entry.Append(new IrRet(loaded));
    return PointerCompression.Run(fn, pointerBits: 32);
  }
}
