using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class StorageNarrowingTests {

  [Test]
  public void O0057_BoundedUnsignedLong_UsesByteStorageAndKeepsLongArithmetic() {
    var input = new IrArgument(IrType.I32, 0, "input");
    var fn = new IrFunction("f", IrType.I32, [input]);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.I32) { IsSourceVariable = true, Name = "value" });
    var bounded = entry.Append(new IrBinary(IrBinaryOp.And, input, new IrConstantInt(IrType.I32, 255)));
    entry.Append(new IrStore(bounded, slot));
    var loaded = entry.Append(new IrLoad(IrType.I32, slot));
    var plusOne = entry.Append(new IrBinary(IrBinaryOp.Add, loaded, new IrConstantInt(IrType.I32, 1)));
    entry.Append(new IrRet(plusOne));

    Assert.That(StorageNarrowing.Run(fn, minimumIntegerBits: 8), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var narrowed = entry.Instructions.OfType<IrAlloca>().Single();
    Assert.That(narrowed.Allocated, Is.EqualTo(IrType.U8));
    var truncation = entry.Instructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.Trunc);
    var extension = entry.Instructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.ZExt);
    Assert.That(truncation.Type, Is.EqualTo(IrType.U8));
    Assert.That(extension.Type, Is.EqualTo(IrType.I32));
    Assert.That(plusOne.Lhs, Is.SameAs(extension), "arithmetic must still execute at the source width");

    Assert.That(Mem2Reg.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>(), Is.Empty);
    Assert.That(extension.Value, Is.SameAs(truncation), "promotion must preserve the narrow SSA representation");
  }

  [Test]
  public void O0057_X8616Policy_StopsAtWordStorage() {
    var input = new IrArgument(IrType.I32, 0, "input");
    var fn = new IrFunction("f", IrType.I32, [input]);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.I32) { Name = "value" });
    var bounded = entry.Append(new IrBinary(IrBinaryOp.And, input, new IrConstantInt(IrType.I32, 255)));
    entry.Append(new IrStore(bounded, slot));
    var loaded = entry.Append(new IrLoad(IrType.I32, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(StorageNarrowing.Run(fn, minimumIntegerBits: 16), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.U16));
    Assert.That(entry.Instructions.OfType<IrCast>().Any(c => c.Op == IrCastOp.ZExt && c.Type == IrType.I32), Is.True);
  }

  [Test]
  public void O0057_NegativeRange_UsesSignedStorageAndSignExtension() {
    var fn = new IrFunction("f", IrType.I32);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.I32) { Name = "value" });
    entry.Append(new IrStore(new IrConstantInt(IrType.I32, -5), slot));
    var loaded = entry.Append(new IrLoad(IrType.I32, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(StorageNarrowing.Run(fn, minimumIntegerBits: 8), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.I8));
    Assert.That(entry.Instructions.OfType<IrCast>().Any(c => c.Op == IrCastOp.SExt && c.Type == IrType.I32), Is.True);
  }

  [Test]
  public void O0057_PromotedMergeValue_UsesNarrowPhiAndExtendsOnceAtTheBoundary() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.I32, [condition]);
    var entry = fn.CreateBlock("entry");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");
    var merge = fn.CreateBlock("merge");
    entry.Append(new IrCondBr(condition, yes, no));
    yes.Append(new IrBr(merge));
    no.Append(new IrBr(merge));
    var value = merge.AppendPhi(new IrPhi(IrType.I32) { Name = "value" });
    value.AddIncoming(new IrConstantInt(IrType.I32, 12), yes);
    value.AddIncoming(new IrConstantInt(IrType.I32, 200), no);
    var plusOne = merge.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.I32, 1)));
    merge.Append(new IrRet(plusOne));

    Assert.That(StorageNarrowing.Run(fn, minimumIntegerBits: 8), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var narrowPhi = merge.Phis.Single();
    Assert.That(narrowPhi.Type, Is.EqualTo(IrType.U8));
    Assert.That(yes.Instructions.OfType<IrCast>().Single().Op, Is.EqualTo(IrCastOp.Trunc));
    Assert.That(no.Instructions.OfType<IrCast>().Single().Op, Is.EqualTo(IrCastOp.Trunc));
    var extension = merge.Instructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.ZExt);
    Assert.That(extension.Value, Is.SameAs(narrowPhi));
    Assert.That(extension.Type, Is.EqualTo(IrType.I32));
    Assert.That(plusOne.Lhs, Is.SameAs(extension), "users must keep the original arithmetic width");
  }

  [Test]
  public void O0057_AddressObservableStorage_IsNotNarrowed() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.I32) { Name = "value" });
    entry.Append(new IrStore(new IrConstantInt(IrType.I32, 7), slot));
    _ = entry.Append(new IrGep(slot, new IrConstantInt(IrType.I32, 0), IrType.I32));
    entry.Append(new IrRet());

    Assert.That(StorageNarrowing.Run(fn, minimumIntegerBits: 8), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.I32));
  }

  [Test]
  public void O0057_SingleBackedDoubleSlot_UsesF32StorageAndKeepsDoubleArithmetic() {
    var input = new IrArgument(IrType.F32, 0, "input");
    var fn = new IrFunction("f", IrType.F64, [input]);
    var entry = fn.CreateBlock("entry");
    var widenedInput = entry.Append(new IrCast(IrCastOp.FPExt, input, IrType.F64));
    var slot = entry.Append(new IrAlloca(IrType.F64) { Name = "value" });
    entry.Append(new IrStore(widenedInput, slot));
    var loaded = entry.Append(new IrLoad(IrType.F64, slot));
    var plusOne = entry.Append(new IrBinary(IrBinaryOp.FAdd, loaded, new IrConstantFloat(IrType.F64, 1.0)));
    entry.Append(new IrRet(plusOne));

    Assert.That(StorageNarrowing.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.F32));
    Assert.That(entry.Instructions.OfType<IrCast>().Any(c => c.Op == IrCastOp.FPTrunc && c.Type == IrType.F32), Is.True);
    var extension = entry.Instructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.FPExt && c.Value is IrLoad);
    Assert.That(extension.Type, Is.EqualTo(IrType.F64));
    Assert.That(plusOne.Lhs, Is.SameAs(extension), "floating arithmetic must remain at the declared width");
  }

  [Test]
  public void O0057_IntegerBackedDoubleWithinSingleExactRange_NarrowsStorage() {
    var input = new IrArgument(IrType.I16, 0, "input");
    var fn = new IrFunction("f", IrType.F64, [input]);
    var entry = fn.CreateBlock("entry");
    var converted = entry.Append(new IrCast(IrCastOp.SIToFP, input, IrType.F64));
    var slot = entry.Append(new IrAlloca(IrType.F64) { Name = "value" });
    entry.Append(new IrStore(converted, slot));
    var loaded = entry.Append(new IrLoad(IrType.F64, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(StorageNarrowing.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.F32));
  }

  [Test]
  public void O0057_NonRepresentableDoubleConstant_IsNotNarrowed() {
    var fn = new IrFunction("f", IrType.F64);
    var entry = fn.CreateBlock("entry");
    var slot = entry.Append(new IrAlloca(IrType.F64) { Name = "value" });
    entry.Append(new IrStore(new IrConstantFloat(IrType.F64, 16_777_217.0), slot));
    var loaded = entry.Append(new IrLoad(IrType.F64, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(StorageNarrowing.Run(fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Single().Allocated, Is.EqualTo(IrType.F64));
  }

  [Test]
  public void O0057_WideFloatArithmeticResult_IsNotNarrowedFromRangeAlone() {
    var left = new IrArgument(IrType.F32, 0, "left");
    var right = new IrArgument(IrType.F32, 1, "right");
    var fn = new IrFunction("f", IrType.F64, [left, right]);
    var entry = fn.CreateBlock("entry");
    var wideLeft = entry.Append(new IrCast(IrCastOp.FPExt, left, IrType.F64));
    var wideRight = entry.Append(new IrCast(IrCastOp.FPExt, right, IrType.F64));
    var sum = entry.Append(new IrBinary(IrBinaryOp.FAdd, wideLeft, wideRight));
    var slot = entry.Append(new IrAlloca(IrType.F64) { Name = "value" });
    entry.Append(new IrStore(sum, slot));
    var loaded = entry.Append(new IrLoad(IrType.F64, slot));
    entry.Append(new IrRet(loaded));

    Assert.That(StorageNarrowing.Run(fn), Is.Zero,
      "a small floating range does not prove that a wide arithmetic result has only SINGLE precision");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void O0057_PromotedFloatMerge_UsesF32PhiAndExtendsAtBoundary() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.F64, [condition]);
    var entry = fn.CreateBlock("entry");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");
    var merge = fn.CreateBlock("merge");
    entry.Append(new IrCondBr(condition, yes, no));
    yes.Append(new IrBr(merge));
    no.Append(new IrBr(merge));
    var value = merge.AppendPhi(new IrPhi(IrType.F64) { Name = "value" });
    value.AddIncoming(new IrConstantFloat(IrType.F64, 1.5), yes);
    value.AddIncoming(new IrConstantFloat(IrType.F64, 200.25), no);
    var doubled = merge.Append(new IrBinary(IrBinaryOp.FMul, value, new IrConstantFloat(IrType.F64, 2.0)));
    merge.Append(new IrRet(doubled));

    Assert.That(StorageNarrowing.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var narrowPhi = merge.Phis.Single();
    Assert.That(narrowPhi.Type, Is.EqualTo(IrType.F32));
    Assert.That(yes.Instructions.OfType<IrCast>().Single().Op, Is.EqualTo(IrCastOp.FPTrunc));
    Assert.That(no.Instructions.OfType<IrCast>().Single().Op, Is.EqualTo(IrCastOp.FPTrunc));
    var extension = merge.Instructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.FPExt);
    Assert.That(extension.Value, Is.SameAs(narrowPhi));
    Assert.That(extension.Type, Is.EqualTo(IrType.F64));
    Assert.That(doubled.Lhs, Is.SameAs(extension));
  }
}
