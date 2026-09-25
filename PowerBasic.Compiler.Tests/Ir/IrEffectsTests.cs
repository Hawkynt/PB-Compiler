using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrEffectsTests {

  [TestCase("llvm.sqrt.f32")]
  [TestCase("llvm.sin.f64")]
  [TestCase("llvm.pow.f80")]
  public void ForExternalCall_GivenCheckedMathIntrinsic_ThenItIsEffectFreeDeterministicAndSpeculatable(string name) {
    var effects = IrEffects.ForExternalCall(name);

    Assert.Multiple(() => {
      Assert.That(effects.Effects, Is.EqualTo(IrEffectKind.None));
      Assert.That(effects.Deterministic, Is.True);
      Assert.That(effects.CanCse, Is.True);
      Assert.That(effects.CanSpeculate, Is.True);
      Assert.That(FunctionSummaries.IsPureExternal(name), Is.True);
      Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.True);
    });
  }

  [TestCase("rt_print_nl")]
  [TestCase("rt_file_open")]
  [TestCase("llvm.unknown.p0")]
  public void ForExternalCall_GivenUnmodeledRuntimeEntry_ThenItRemainsConservative(string name) {
    var effects = IrEffects.ForExternalCall(name);

    Assert.Multiple(() => {
      Assert.That(effects.Effects, Is.Not.EqualTo(IrEffectKind.None));
      Assert.That(effects.Effects.HasFlag(IrEffectKind.ReadsMemory), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.WritesMemory), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayAllocate), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayRelease), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayTrap), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MaySynchronize), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.PerformsIo), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayThrow), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.MayBlock), Is.True);
      Assert.That(effects.Effects.HasFlag(IrEffectKind.Atomic), Is.True);
      Assert.That(effects.Deterministic, Is.False);
      Assert.That(effects.CanCse, Is.False);
      Assert.That(effects.CanSpeculate, Is.False);
      Assert.That(FunctionSummaries.IsPureExternal(name), Is.False);
      Assert.That(FunctionSummaries.IsSpeculatableExternal(name), Is.False);
    });
  }


  [Test]
  public void ForExternalCall_GivenModeledOwnershipAndMemoryEntries_ThenReportsTheirExactClass() {
    var borrow = IrEffects.ForExternalCall("rt_str_len_borrow");
    var consume = IrEffects.ForExternalCall("rt_str_len");
    var duplicate = IrEffects.ForExternalCall("rt_str_dup");
    var release = IrEffects.ForExternalCall("rt_str_free");
    var compare = IrEffects.ForExternalCall("rt_mem_compare");
    var allocate = IrEffects.ForExternalCall("rt_arr_alloc");
    var reallocate = IrEffects.ForExternalCall("rt_arr_realloc");
    var error = IrEffects.ForExternalCall("rt_error");

    Assert.Multiple(() => {
      Assert.That(borrow.Effects, Is.EqualTo(IrEffectKind.ReadsMemory));
      Assert.That(borrow.Deterministic, Is.True);
      Assert.That(borrow.CanDiscard, Is.True);
      Assert.That(borrow.DefinesMemory, Is.False);

      Assert.That(consume.Effects, Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.MayRelease));
      Assert.That(consume.Deterministic, Is.True);
      Assert.That(consume.CanDiscard, Is.False);
      Assert.That(consume.DefinesMemory, Is.True);

      Assert.That(duplicate.Effects,
        Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(duplicate.Deterministic, Is.False);
      Assert.That(duplicate.CanDiscard, Is.False);

      Assert.That(release.Effects, Is.EqualTo(IrEffectKind.MayRelease));
      Assert.That(release.DefinesMemory, Is.True);

      Assert.That(compare.Effects, Is.EqualTo(IrEffectKind.ReadsMemory));
      Assert.That(compare.Deterministic, Is.True);
      Assert.That(compare.CanDiscard, Is.True);

      Assert.That(allocate.Effects,
        Is.EqualTo(IrEffectKind.WritesMemory | IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(allocate.CanDiscard, Is.False);

      Assert.That(reallocate.Effects, Is.EqualTo(
        IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory | IrEffectKind.MayAllocate
        | IrEffectKind.MayRelease | IrEffectKind.MayTrap));
      Assert.That(reallocate.CanDiscard, Is.False);

      Assert.That(error.Effects,
        Is.EqualTo(IrEffectKind.WritesMemory | IrEffectKind.MayTrap | IrEffectKind.MayThrow));
      Assert.That(error.CanDiscard, Is.False);
    });
  }



  [Test]
  public void ForExternalCall_GivenCoreOwnedStringOperations_ThenOwnershipAndAllocationAreExplicit() {
    var literal = IrEffects.ForExternalCall("rt_str_const");
    var concat = IrEffects.ForExternalCall("rt_str_concat");
    var compare = IrEffects.ForExternalCall("rt_str_compare");
    var slice = IrEffects.ForExternalCall("rt_str_mid");
    var chr = IrEffects.ForExternalCall("rt_str_chr");

    Assert.Multiple(() => {
      Assert.That(literal.Effects,
        Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(literal.CanDiscard, Is.False);

      Assert.That(concat.Effects, Is.EqualTo(
        IrEffectKind.ReadsMemory | IrEffectKind.MayAllocate | IrEffectKind.MayRelease | IrEffectKind.MayTrap));
      Assert.That(concat.DefinesMemory, Is.True);
      Assert.That(concat.CanDiscard, Is.False);

      Assert.That(compare.Effects,
        Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.MayRelease));
      Assert.That(compare.Deterministic, Is.True);
      Assert.That(compare.CanDiscard, Is.False,
        "the comparison result may be unused, but the DOS ABI still consumes both owned handles");

      Assert.That(slice.Effects, Is.EqualTo(
        IrEffectKind.ReadsMemory | IrEffectKind.MayAllocate | IrEffectKind.MayRelease | IrEffectKind.MayTrap));
      Assert.That(slice.CanDiscard, Is.False);

      Assert.That(chr.Effects,
        Is.EqualTo(IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(chr.Deterministic, Is.False,
        "equal character codes still produce distinct owned handles");
    });
  }

  [Test]
  public void Dce_GivenUnusedConsumingStringComparison_ThenItKeepsTheOwnershipEffect() {
    var compare = new IrFunction("rt_str_compare", IrType.I32, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1),
    ]);
    var function = new IrFunction("f", IrType.Void);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var call = builder.Call(IrType.I32, compare, new IrNullPtr(), new IrNullPtr());
    builder.Ret();

    Assert.That(Dce.Run(function), Is.Zero);
    Assert.That(call.Parent, Is.Not.Null);
  }

  [Test]
  public void ForExternalCall_GivenPagedArrayRuntimeEntries_ThenReportsAllocationMappingAndQueryEffects() {
    var hugeAlloc = IrEffects.ForExternalCall("rt_huge_alloc");
    var hugeFree = IrEffects.ForExternalCall("rt_huge_free");
    var hugeZero = IrEffects.ForExternalCall("rt_huge_zero");
    var emsAlloc = IrEffects.ForExternalCall("rt_ems_alloc");
    var emsFree = IrEffects.ForExternalCall("rt_ems_free");
    var emsFrame = IrEffects.ForExternalCall("rt_ems_frame");
    var emsFreeBytes = IrEffects.ForExternalCall("rt_ems_fre");
    var emsMap = IrEffects.ForExternalCall("rt_ems_map2");
    var emsZero = IrEffects.ForExternalCall("rt_ems_zero");

    Assert.Multiple(() => {
      Assert.That(hugeAlloc.Effects,
        Is.EqualTo(IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(hugeAlloc.DefinesMemory, Is.True);
      Assert.That(hugeFree.Effects, Is.EqualTo(IrEffectKind.MayRelease));
      Assert.That(hugeZero.Effects, Is.EqualTo(IrEffectKind.WritesMemory));

      Assert.That(emsAlloc.Effects,
        Is.EqualTo(IrEffectKind.MayAllocate | IrEffectKind.MayTrap));
      Assert.That(emsFree.Effects, Is.EqualTo(IrEffectKind.MayRelease));
      Assert.That(emsFrame.Effects,
        Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory | IrEffectKind.MayTrap));
      Assert.That(emsFrame.CanDiscard, Is.False);

      Assert.That(emsFreeBytes.Effects, Is.EqualTo(IrEffectKind.ReadsMemory));
      Assert.That(emsFreeBytes.Deterministic, Is.False,
        "free EMS capacity may change after allocation/release");
      Assert.That(emsFreeBytes.CanDiscard, Is.True,
        "an unused capacity query has no language-visible side effect");
      Assert.That(emsFreeBytes.CanCse, Is.False);

      Assert.That(emsMap.Effects,
        Is.EqualTo(IrEffectKind.WritesMemory | IrEffectKind.MayTrap));
      Assert.That(emsZero.Effects,
        Is.EqualTo(IrEffectKind.WritesMemory | IrEffectKind.MayTrap));
    });
  }

  [Test]
  public void ForCall_GivenMemoryIntrinsicVolatilityFlag_ThenRefinesTheDeclarationContract() {
    var memcpy = new IrFunction("llvm.memcpy.p0.p0.i32", IrType.Void, [
      new IrArgument(IrType.Ptr, 0),
      new IrArgument(IrType.Ptr, 1),
      new IrArgument(IrType.I32, 2),
      new IrArgument(IrType.I1, 3),
    ]);
    var dst = new IrAlloca(IrType.I8) { Count = 4 };
    var src = new IrAlloca(IrType.I8) { Count = 4 };
    var nonVolatile = new IrCall(IrType.Void, memcpy,
      [dst, src, new IrConstantInt(IrType.I32, 4), IrBuilder.ConstBool(false)]);
    var volatileCopy = new IrCall(IrType.Void, memcpy,
      [dst, src, new IrConstantInt(IrType.I32, 4), IrBuilder.ConstBool(true)]);

    Assert.Multiple(() => {
      Assert.That(IrEffects.ForExternalCall(memcpy.Name).Effects.HasFlag(IrEffectKind.Volatile), Is.True,
        "a declaration-only query cannot assume the operand is false");
      Assert.That(IrEffects.ForCall(nonVolatile).Effects,
        Is.EqualTo(IrEffectKind.ReadsMemory | IrEffectKind.WritesMemory));
      Assert.That(IrEffects.ForCall(volatileCopy).Effects.HasFlag(IrEffectKind.Volatile), Is.True);
    });
  }

  [Test]
  public void ForCall_GivenDefinedFunctionWithIntrinsicName_ThenItDoesNotUseExternalNameContract() {
    var parameter = new IrArgument(IrType.F64, 0);
    var function = new IrFunction("llvm.sin.f64", IrType.F64, [parameter]);
    new IrBuilder(function.CreateBlock("entry")).Ret(parameter);

    var effects = IrEffects.ForCall(function);

    Assert.That(effects, Is.EqualTo(IrEffectSummary.UnknownExternal));
  }
  private sealed class UnclassifiedInstruction : IrInstruction {
    public UnclassifiedInstruction() : base(IrType.Void) { }
  }

  [Test]
  public void ForInstruction_GivenCoreOperations_ThenEffectsAreExplicit() {
    var slot = new IrAlloca(IrType.I16);
    var add = new IrBinary(IrBinaryOp.Add,
      new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2));
    var div = new IrBinary(IrBinaryOp.SDiv,
      new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2));
    var load = new IrLoad(IrType.I16, slot);
    var store = new IrStore(new IrConstantInt(IrType.I16, 3), slot);

    Assert.Multiple(() => {
      Assert.That(IrEffects.ForInstruction(add).CanCse, Is.True);
      Assert.That(IrEffects.ForInstruction(div).Effects.HasFlag(IrEffectKind.MayTrap), Is.True);
      Assert.That(IrEffects.ForInstruction(div).CanDiscard, Is.False);
      Assert.That(IrEffects.ForInstruction(load).MayReadMemory, Is.True);
      Assert.That(IrEffects.ForInstruction(load).CanDiscard, Is.True);
      Assert.That(IrEffects.ForInstruction(load).CanCse, Is.False);
      Assert.That(IrEffects.ForInstruction(store).DefinesMemory, Is.True);
      Assert.That(IrEffects.ForInstruction(store).CanDiscard, Is.False);
      Assert.That(IrEffects.ForInstruction(slot).CanDiscard, Is.True);
      Assert.That(IrEffects.ForInstruction(slot).CanCse, Is.False);
    });
  }

  [Test]
  public void ForInstruction_GivenATypeWithNoEffectContract_ThenItFailsClosed() {
    Assert.That(
      () => IrEffects.ForInstruction(new UnclassifiedInstruction()),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Dce_GivenUnusedPotentiallyTrappingDivision_ThenItIsKept() {
    var divisor = new IrArgument(IrType.I16, 0, "divisor");
    var function = new IrFunction("f", IrType.Void, [divisor]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var division = builder.SDiv(new IrConstantInt(IrType.I16, 10), divisor);
    builder.Ret();

    Assert.That(Dce.Run(function), Is.Zero);
    Assert.That(division.Parent, Is.Not.Null);
  }

  [Test]
  public void Dce_GivenUnusedEffectFreeExternalCall_ThenItIsRemoved() {
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64, [new IrArgument(IrType.F64, 0)]);
    var function = new IrFunction("f", IrType.Void);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var call = builder.Call(IrType.F64, sqrt, new IrConstantFloat(IrType.F64, 4));
    builder.Ret();

    Assert.That(Dce.Run(function), Is.EqualTo(1));
    Assert.That(call.Parent, Is.Null);
  }

}
