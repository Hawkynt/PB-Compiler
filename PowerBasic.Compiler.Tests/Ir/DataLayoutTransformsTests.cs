using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DataLayoutTransformsTests {

  [Test]
  public void O0320_PrivatePackedRecordArray_BecomesFieldArrays() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.Void, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 4, Name = "p" });
    _ = LoadRecordField(entry, records, i, 4, 0, IrType.I16);
    _ = LoadRecordField(entry, records, i, 4, 2, IrType.I16);
    entry.Append(new IrRet());

    Assert.That(ArrayOfStructsToStructOfArrays.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Count(a => a.Allocated == IrType.I16 && a.Count == 64), Is.EqualTo(2));
    Assert.That(entry.Instructions.Contains(records), Is.False);
  }

  [Test]
  public void O0321_HotField_IsMovedAheadOfColdFields() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.Void, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 6, Name = "r" });
    _ = LoadRecordField(entry, records, i, 6, 0, IrType.I16);
    _ = LoadRecordField(entry, records, i, 6, 2, IrType.I16);
    for (var n = 0; n < 6; ++n)
      _ = LoadRecordField(entry, records, i, 6, 4, IrType.I16);
    entry.Append(new IrRet());

    Assert.That(FieldReordering.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(FieldReordering.Run(fn), Is.Zero, "the weighted order must be a fixpoint");
  }

  [Test]
  public void O0322_ColdField_IsSplitFromHotRecord() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.Void, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 6, Name = "entity" });
    for (var n = 0; n < 8; ++n)
      _ = LoadRecordField(entry, records, i, 6, 0, IrType.I16);
    for (var n = 0; n < 4; ++n)
      _ = LoadRecordField(entry, records, i, 6, 2, IrType.I16);
    _ = LoadRecordField(entry, records, i, 6, 4, IrType.I16);
    entry.Append(new IrRet());

    Assert.That(HotColdFieldSplitting.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "entity.hot"), Is.True);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name?.EndsWith(".cold", StringComparison.Ordinal) == true), Is.True);
  }

  [Test]
  public void O0323_BoundedIntegerField_UsesNarrowerStorage() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 2, Name = "tile" });
    var ptr = RecordFieldPointer(entry, records, i, 2, 0);
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), ptr));
    var value = entry.Append(new IrLoad(IrType.I16, ptr));
    entry.Append(new IrRet(value));

    Assert.That(StructurePackingByRange.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "tile.packed" && a.Count == 64), Is.True);
    Assert.That(entry.Instructions.OfType<IrLoad>().Any(l => l.Type.Bits == 8), Is.True);
    Assert.That(entry.Instructions.OfType<IrCast>().Any(c => c.Op is IrCastOp.ZExt or IrCastOp.SExt), Is.True);
  }

  [Test]
  public void O0324_SameRegionPointerArray_StoresIndices() {
    var fn = new IrFunction("f", IrType.Ptr);
    var entry = fn.CreateBlock("entry");
    var region = entry.Append(new IrAlloca(IrType.I16) { Count = 100, Name = "nodes" });
    var pointers = entry.Append(new IrAlloca(IrType.Ptr) { Count = 32, Name = "next" });
    var slot = entry.Append(new IrGep(pointers, new IrConstantInt(IrType.I32, 0), IrType.Ptr));
    var target = entry.Append(new IrGep(region, new IrConstantInt(IrType.I32, 5), IrType.I16));
    entry.Append(new IrStore(target, slot));
    var loaded = entry.Append(new IrLoad(IrType.Ptr, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(PointerCompression.Run(fn, pointerBits: 32), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "next.compressed" && a.Allocated == IrType.U16), Is.True);
    Assert.That(entry.Instructions.OfType<IrSelect>().Any(), Is.True);
  }

  [Test]
  public void O0325_ArrayTail_IsRoundedToVectorWidth() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [i]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 17, Name = "a" });
    var ptr = entry.Append(new IrGep(array, i, IrType.I16));
    var value = entry.Append(new IrLoad(IrType.I16, ptr));
    entry.Append(new IrRet(value));

    Assert.That(ArrayPaddingAlignment.Run(fn, vectorBytes: 8), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "a.padded" && a.Count == 20), Is.True);
  }

  [Test]
  public void O0326_PowerOfTwoRowStride_GetsOneElementPad() {
    var row = new IrArgument(IrType.I32, 0, "row");
    var col = new IrArgument(IrType.I32, 1, "col");
    var fn = new IrFunction("f", IrType.I16, [row, col]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 8 * 32, Name = "m" });
    var index = entry.Append(new IrBinary(IrBinaryOp.Add,
      entry.Append(new IrBinary(IrBinaryOp.Mul, row, new IrConstantInt(IrType.I32, 32))), col));
    var ptr = entry.Append(new IrGep(array, index, IrType.I16));
    var value = entry.Append(new IrLoad(IrType.I16, ptr));
    entry.Append(new IrRet(value));

    Assert.That(CacheConflictPadding.Run(fn, cacheSizeBytes: 64, cacheLineBytes: 16), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "m.cachepad" && a.Count == 8 * 33), Is.True);
  }

  [Test]
  public void O0327_StridedInnerLoop_TransposesPrivateArray() {
    var col = new IrArgument(IrType.I32, 0, "col");
    var fn = new IrFunction("f", IrType.Void, [col]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("loop.header");
    var body = fn.CreateBlock("loop.body");
    var latch = fn.CreateBlock("loop.latch");
    var exit = fn.CreateBlock("exit");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 8 * 32, Name = "img" });
    entry.Append(new IrBr(header));
    var counter = header.AppendPhi(new IrPhi(IrType.I32));
    counter.AddIncoming(new IrConstantInt(IrType.I32, 0), entry);
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I32, 8)));
    header.Append(new IrCondBr(test, body, exit));
    var index = body.Append(new IrBinary(IrBinaryOp.Add,
      body.Append(new IrBinary(IrBinaryOp.Mul, counter, new IrConstantInt(IrType.I32, 32))), col));
    var ptr = body.Append(new IrGep(array, index, IrType.I16));
    body.Append(new IrStore(new IrConstantInt(IrType.I16, 1), ptr));
    body.Append(new IrBr(latch));
    var next = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I32, 1)));
    latch.Append(new IrBr(header));
    counter.AddIncoming(next, latch);
    exit.Append(new IrRet());

    Assert.That(DataTransposition.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "img.transpose"), Is.True);
  }

  [Test]
  public void O0328_ProducerTemporary_IsForwardedIntoConsumerLoop() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var pHeader = fn.CreateBlock("p.header");
    var pBody = fn.CreateBlock("p.body");
    var pLatch = fn.CreateBlock("p.latch");
    var between = fn.CreateBlock("between");
    var cHeader = fn.CreateBlock("c.header");
    var cBody = fn.CreateBlock("c.body");
    var cLatch = fn.CreateBlock("c.latch");
    var exit = fn.CreateBlock("exit");
    var source = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "src" });
    var temp = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "tmp" });
    var output = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "out" });
    entry.Append(new IrBr(pHeader));

    var p = pHeader.AppendPhi(new IrPhi(IrType.I32));
    p.AddIncoming(new IrConstantInt(IrType.I32, 0), entry);
    pHeader.Append(new IrCondBr(pHeader.Append(new IrCmp(IrCmpPred.Slt, p, new IrConstantInt(IrType.I32, 8))), pBody, between));
    var srcPtr = pBody.Append(new IrGep(source, p, IrType.I16));
    var src = pBody.Append(new IrLoad(IrType.I16, srcPtr));
    var doubled = pBody.Append(new IrBinary(IrBinaryOp.Mul, src, new IrConstantInt(IrType.I16, 2)));
    var tmpOut = pBody.Append(new IrGep(temp, p, IrType.I16));
    pBody.Append(new IrStore(doubled, tmpOut));
    pBody.Append(new IrBr(pLatch));
    var pNext = pLatch.Append(new IrBinary(IrBinaryOp.Add, p, new IrConstantInt(IrType.I32, 1)));
    pLatch.Append(new IrBr(pHeader));
    p.AddIncoming(pNext, pLatch);
    between.Append(new IrBr(cHeader));

    var c = cHeader.AppendPhi(new IrPhi(IrType.I32));
    c.AddIncoming(new IrConstantInt(IrType.I32, 0), between);
    cHeader.Append(new IrCondBr(cHeader.Append(new IrCmp(IrCmpPred.Slt, c, new IrConstantInt(IrType.I32, 8))), cBody, exit));
    var tmpIn = cBody.Append(new IrGep(temp, c, IrType.I16));
    var tempValue = cBody.Append(new IrLoad(IrType.I16, tmpIn));
    var plusOne = cBody.Append(new IrBinary(IrBinaryOp.Add, tempValue, new IrConstantInt(IrType.I16, 1)));
    var outPtr = cBody.Append(new IrGep(output, c, IrType.I16));
    cBody.Append(new IrStore(plusOne, outPtr));
    cBody.Append(new IrBr(cLatch));
    var cNext = cLatch.Append(new IrBinary(IrBinaryOp.Add, c, new IrConstantInt(IrType.I32, 1)));
    cLatch.Append(new IrBr(cHeader));
    c.AddIncoming(cNext, cLatch);
    exit.Append(new IrRet());

    Assert.That(TemporaryArrayFusion.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Any(a => a.Name == "tmp"), Is.False);
    Assert.That(fn.AllInstructions.OfType<IrLoad>().Any(l => l.Pointer is IrGep { BasePtr: var b } && ReferenceEquals(b, temp)), Is.False);
  }

  [Test]
  public void O0329_OneElementWindow_BecomesLoopCarriedPhi() {
    var fn = new IrFunction("f", IrType.I16);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("loop.header");
    var body = fn.CreateBlock("loop.body");
    var latch = fn.CreateBlock("loop.latch");
    var exit = fn.CreateBlock("exit");
    var source = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "a" });
    var temp = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "t" });
    var a0 = entry.Append(new IrLoad(IrType.I16, entry.Append(new IrGep(source, new IrConstantInt(IrType.I32, 0), IrType.I16))));
    entry.Append(new IrStore(a0, entry.Append(new IrGep(temp, new IrConstantInt(IrType.I32, 0), IrType.I16))));
    entry.Append(new IrBr(header));

    var i = header.AppendPhi(new IrPhi(IrType.I32));
    i.AddIncoming(new IrConstantInt(IrType.I32, 1), entry);
    header.Append(new IrCondBr(header.Append(new IrCmp(IrCmpPred.Sle, i, new IrConstantInt(IrType.I32, 7))), body, exit));
    var prevIndex = body.Append(new IrBinary(IrBinaryOp.Sub, i, new IrConstantInt(IrType.I32, 1)));
    var previous = body.Append(new IrLoad(IrType.I16, body.Append(new IrGep(temp, prevIndex, IrType.I16))));
    var input = body.Append(new IrLoad(IrType.I16, body.Append(new IrGep(source, i, IrType.I16))));
    var sum = body.Append(new IrBinary(IrBinaryOp.Add, previous, input));
    body.Append(new IrStore(sum, body.Append(new IrGep(temp, i, IrType.I16))));
    body.Append(new IrBr(latch));
    var next = latch.Append(new IrBinary(IrBinaryOp.Add, i, new IrConstantInt(IrType.I32, 1)));
    latch.Append(new IrBr(header));
    i.AddIncoming(next, latch);
    var answer = exit.Append(new IrLoad(IrType.I16, exit.Append(new IrGep(temp, new IrConstantInt(IrType.I32, 7), IrType.I16))));
    exit.Append(new IrRet(answer));

    Assert.That(ArrayContraction.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Any(a => a.Name == "t"), Is.False);
    Assert.That(header.Phis.Count(), Is.EqualTo(2));
  }

  [Test]
  public void LayoutTransforms_DeclineEscapedStorage() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var callee = new IrFunction("opaque", IrType.Void, [new IrArgument(IrType.Ptr, 0, "p")]);
    var fn = new IrFunction("f", IrType.Void, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 4, Name = "p" });
    _ = LoadRecordField(entry, records, i, 4, 0, IrType.I16);
    _ = LoadRecordField(entry, records, i, 4, 2, IrType.I16);
    entry.Append(new IrCall(IrType.Void, callee, [records]));
    entry.Append(new IrRet());

    Assert.That(ArrayOfStructsToStructOfArrays.Run(fn), Is.Zero);
    Assert.That(FieldReordering.Run(fn), Is.Zero);
    Assert.That(HotColdFieldSplitting.Run(fn), Is.Zero);
    Assert.That(StructurePackingByRange.Run(fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private static IrLoad LoadRecordField(IrBasicBlock block, IrAlloca root, IrValue index, int stride, int fieldOffset, IrType type)
    => block.Append(new IrLoad(type, RecordFieldPointer(block, root, index, stride, fieldOffset)));

  private static IrValue RecordFieldPointer(IrBasicBlock block, IrAlloca root, IrValue index, int stride, int fieldOffset) {
    IrValue offset = block.Append(new IrBinary(IrBinaryOp.Mul, index, new IrConstantInt(index.Type, stride)));
    if (fieldOffset != 0)
      offset = block.Append(new IrBinary(IrBinaryOp.Add, offset, new IrConstantInt(index.Type, fieldOffset)));
    return block.Append(new IrGep(root, offset));
  }
}
