using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0331 — Boolean-domain proof and whole-array preservation for bitset substitution.</summary>
[TestFixture]
public sealed class BitsetSubstitutionTests {

  [Test]
  public void BooleanGlobalArray_GivenRangeProvenComputedBooleanStore_ThenItPacksToBits() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var input = new IrArgument(IrType.I16, 0, "input");
    var index = new IrArgument(IrType.I16, 1, "index");
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, [input, index]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var compare = entry.Append(new IrCmp(IrCmpPred.Sgt, input, new IrConstantInt(IrType.I16, 0)));
    var widened = entry.Append(new IrCast(IrCastOp.ZExt, compare, IrType.I16));
    var boolean = entry.Append(new IrBinary(IrBinaryOp.Sub, new IrConstantInt(IrType.I16, 0), widened));
    var at = entry.Append(new IrGep(flags, index, IrType.I16));
    entry.Append(new IrStore(boolean, at));
    entry.Append(new IrRet());

    Assert.That(BitsetSubstitution.Run(module), Is.EqualTo(1));
    var packed = module.FindGlobal("flags");
    Assert.That(packed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(packed!.ValueType.SameStorage(IrType.I8), Is.True);
      Assert.That(packed.Count, Is.EqualTo(4));
      Assert.That(fn.AllInstructions.OfType<IrCast>().Any(cast =>
        cast.Op == IrCastOp.Trunc && ReferenceEquals(cast.Value, boolean) && cast.Type.SameStorage(IrType.I8)), Is.True,
        "the proven 0/-1 value should feed the selected packed bit rather than require a branch");
      Assert.That(fn.AllInstructions.OfType<IrStore>().All(store => store.Value.Type.SameStorage(IrType.I8)), Is.True);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void BooleanGlobalArray_GivenAnUnprovenComputedStore_ThenItStaysUnpacked() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var value = new IrArgument(IrType.I16, 0, "value");
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, [value]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(flags, new IrConstantInt(IrType.I16, 7), IrType.I16));
    entry.Append(new IrStore(value, at));
    entry.Append(new IrRet());

    Assert.That(BitsetSubstitution.Run(module), Is.Zero);
    Assert.That(module.FindGlobal("flags")!.ValueType.SameStorage(IrType.I16), Is.True);
  }

  [Test]
  public void BooleanGlobalArray_GivenWholeArrayZeroing_ThenMemsetTargetsPackedStorage() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var memset = AddMemset(module);
    var fn = module.AddFunction(new IrFunction("f", IrType.I16));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var zero = entry.Append(new IrCall(IrType.Void, memset, [
      flags,
      new IrConstantInt(IrType.I8, 0),
      new IrConstantInt(IrType.I32, 64),
      new IrConstantInt(IrType.I1, 0),
    ]));
    var at = entry.Append(new IrGep(flags, new IrConstantInt(IrType.I16, 9), IrType.I16));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, at))));

    Assert.That(BitsetSubstitution.Run(module), Is.EqualTo(1));
    var packed = module.FindGlobal("flags");
    Assert.That(packed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(zero.Args.ElementAt(0), Is.SameAs(packed));
      Assert.That(((IrConstantInt)zero.Args.ElementAt(2)).ZeroExtended, Is.EqualTo(4));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void BooleanGlobalArray_GivenPartialZeroing_ThenItsOriginalLayoutRemainsObservable() {
    var module = new IrModule("test");
    var flags = module.AddGlobal(new IrGlobalVariable("flags", IrType.I16) {
      Count = 32,
      IsZeroInitialized = true,
    });
    var memset = AddMemset(module);
    var fn = module.AddFunction(new IrFunction("f", IrType.I16));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var zero = entry.Append(new IrCall(IrType.Void, memset, [
      flags,
      new IrConstantInt(IrType.I8, 0),
      new IrConstantInt(IrType.I32, 62),
      new IrConstantInt(IrType.I1, 0),
    ]));
    var at = entry.Append(new IrGep(flags, new IrConstantInt(IrType.I16, 9), IrType.I16));
    entry.Append(new IrRet(entry.Append(new IrLoad(IrType.I16, at))));

    Assert.That(BitsetSubstitution.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("flags"), Is.SameAs(flags));
      Assert.That(zero.Args.ElementAt(0), Is.SameAs(flags));
      Assert.That(((IrConstantInt)zero.Args.ElementAt(2)).ZeroExtended, Is.EqualTo(62));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  private static IrFunction AddMemset(IrModule module)
    => module.AddFunction(new IrFunction("llvm.memset.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0, "destination"),
      new IrArgument(IrType.I8, 1, "value"),
      new IrArgument(IrType.I32, 2, "bytes"),
      new IrArgument(IrType.I1, 3, "volatile"),
    ]));
}
