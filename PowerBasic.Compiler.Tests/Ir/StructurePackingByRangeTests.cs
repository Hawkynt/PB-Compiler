using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class StructurePackingByRangeTests {

  [Test]
  public void SubByteRanges_ShareOneStorageByte() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 64 * 4, Name = "tile" });
    var kindPtr = RecordFieldPointer(entry, records, i, 4, 0);
    var flagsPtr = RecordFieldPointer(entry, records, i, 4, 2);
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 7), kindPtr));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 3), flagsPtr));
    var kind = entry.Append(new IrLoad(IrType.I16, kindPtr));
    var flags = entry.Append(new IrLoad(IrType.I16, flagsPtr));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Add, kind, flags))));

    Assert.That(StructurePackingByRange.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "tile.packed" && a.Count == 64), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(b => b.Op == IrBinaryOp.Or), Is.True,
      "sub-byte stores must merge their bits with the neighbouring field");
    Assert.That(StructurePackingByRange.Run(fn), Is.Zero, "the packed representation must be a fixpoint");
  }

  [Test]
  public void SignedSubByteRange_IsSignExtendedOnLoad() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 32 * 4, Name = "sample" });
    var signedPtr = RecordFieldPointer(entry, records, i, 4, 0);
    var flagPtr = RecordFieldPointer(entry, records, i, 4, 2);
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, -4), signedPtr));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 1), flagPtr));
    var value = entry.Append(new IrLoad(IrType.I16, signedPtr));
    entry.Append(new IrRet(value));

    Assert.That(StructurePackingByRange.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "sample.packed" && a.Count == 32), Is.True);
    Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(b => b.Op == IrBinaryOp.AShr), Is.True,
      "signed sub-byte fields must restore their sign bit before widening");
    Assert.That(fn.AllInstructions.OfType<IrCast>().Any(c => c.Op == IrCastOp.SExt && c.Type.SameStorage(IrType.I16)), Is.True);
  }

  [Test]
  public void SubByteField_DoesNotCrossStorageByteBoundary() {
    var i = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [i]);
    var entry = fn.CreateBlock("entry");
    var records = entry.Append(new IrAlloca(IrType.I8) { Count = 16 * 4, Name = "pair" });
    var firstPtr = RecordFieldPointer(entry, records, i, 4, 0);
    var secondPtr = RecordFieldPointer(entry, records, i, 4, 2);
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 31), firstPtr));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 31), secondPtr));
    var value = entry.Append(new IrLoad(IrType.I16, secondPtr));
    entry.Append(new IrRet(value));

    Assert.That(StructurePackingByRange.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "pair.packed" && a.Count == 16 * 2), Is.True,
      "two five-bit fields need two byte-addressable storage units rather than straddling a byte");
  }

  private static IrValue RecordFieldPointer(IrBasicBlock block, IrAlloca root, IrValue index, int stride, int fieldOffset) {
    IrValue offset = block.Append(new IrBinary(IrBinaryOp.Mul, index, new IrConstantInt(index.Type, stride)));
    if (fieldOffset != 0)
      offset = block.Append(new IrBinary(IrBinaryOp.Add, offset, new IrConstantInt(index.Type, fieldOffset)));
    return block.Append(new IrGep(root, offset));
  }
}
