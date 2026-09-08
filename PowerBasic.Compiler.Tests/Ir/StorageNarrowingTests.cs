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
}
