using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0331/O0332/O0333 — compact Boolean storage and lookup-table tradeoffs.</summary>
[TestFixture]
public sealed class DataRepresentationOptimizationTests {

  [Test]
  public void BooleanGlobalArray_GivenOnlyZeroAndMinusOneStores_ThenItPacksToBits() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var index = new IrArgument(IrType.I16, 0, "index");
    var fn = module.AddFunction(new IrFunction("f", IrType.I16, [index]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(flags, index, IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, -1), at));
    var value = entry.Append(new IrLoad(IrType.I16, at));
    entry.Append(new IrRet(value));

    Assert.That(BitsetSubstitution.Run(module), Is.EqualTo(1));
    var packed = module.FindGlobal("flags");
    Assert.That(packed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(packed!.ValueType.SameStorage(IrType.I8), Is.True);
      Assert.That(packed.Count, Is.EqualTo(4));
      Assert.That(fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.Shl), Is.True);
      Assert.That(fn.AllInstructions.OfType<IrLoad>().Any(load => load.Type.Bits == 16), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void BooleanGlobalArray_GivenAThirdStoredValue_ThenItsRepresentationStaysObservable() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var fn = module.AddFunction(new IrFunction("f", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(flags, new IrConstantInt(IrType.I16, 3), IrType.I16));
    entry.Append(new IrStore(new IrConstantInt(IrType.I16, 1), at));
    entry.Append(new IrRet());

    Assert.That(BitsetSubstitution.Run(module), Is.Zero);
    Assert.That(module.FindGlobal("flags")!.ValueType.SameStorage(IrType.I16), Is.True);
  }

  [Test]
  public void PureByteFunction_GivenRepeatedDynamicCalls_ThenACompleteTableReplacesTheCalls() {
    var module = new IrModule("test");
    var x = new IrArgument(IrType.U8, 0, "x");
    var transform = module.AddFunction(new IrFunction("transform", IrType.U8, [x]));
    var body = transform.AddBlock(new IrBasicBlock("entry"));
    IrValue value = body.Append(new IrBinary(IrBinaryOp.Mul, x, new IrConstantInt(IrType.U8, 3)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U8, 5)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0x33)));
    value = body.Append(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.U8, 7)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U8, 9)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0x55)));
    body.Append(new IrRet(value));

    var input = new IrArgument(IrType.U8, 0, "input");
    var caller = module.AddFunction(new IrFunction("caller", IrType.U8, [input]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var first = entry.Append(new IrCall(IrType.U8, transform, [input]));
    var second = entry.Append(new IrCall(IrType.U8, transform, [input]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Xor, first, second))));

    Assert.That(LookupTableGeneration.Run(module), Is.EqualTo(1));
    var table = module.FindGlobal(".lut.transform");
    Assert.That(table, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(table!.Bytes, Has.Length.EqualTo(256));
      Assert.That(caller.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(caller.AllInstructions.OfType<IrLoad>().Count(), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  [Test]
  public void IdentityByteTable_GivenAByteBoundedIndex_ThenTheLoadAndTableDisappear() {
    var module = new IrModule("test");
    var table = module.AddGlobal(new IrGlobalVariable("identity", IrType.U8) {
      Bytes = [.. Enumerable.Range(0, 256).Select(index => (byte)index)],
      Count = 256,
      IsZeroInitialized = false,
    });
    var index = new IrArgument(IrType.U8, 0, "index");
    var fn = module.AddFunction(new IrFunction("read", IrType.U8, [index]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var widened = entry.Append(new IrCast(IrCastOp.ZExt, index, IrType.I16));
    var at = entry.Append(new IrGep(table, widened, IrType.U8));
    var load = entry.Append(new IrLoad(IrType.U8, at));
    var ret = entry.Append(new IrRet(load));

    Assert.That(LookupTableElimination.Run(module), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("identity"), Is.Null);
      Assert.That(ret.Value, Is.SameAs(index));
      Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void GeneratedLookupTable_GivenAnArithmeticFormula_ThenTheReversePassDoesNotUndoGeneration() {
    var module = new IrModule("test");
    module.AddGlobal(new IrGlobalVariable(".lut.keep", IrType.U8) {
      Bytes = [.. Enumerable.Range(0, 256).Select(index => (byte)(index ^ 0x5a))],
      Count = 256,
    });

    Assert.That(LookupTableElimination.Run(module), Is.Zero);
    Assert.That(module.FindGlobal(".lut.keep"), Is.Not.Null);
  }
}
