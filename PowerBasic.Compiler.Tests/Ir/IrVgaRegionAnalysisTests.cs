using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Controller configuration, VGA latch history and pixel observations are
/// distinct barriers. An unchanged A000h segment alone proves none of them.
/// </summary>
[TestFixture]
public sealed class IrVgaRegionAnalysisTests {
  [Test]
  public void ForInstruction_GivenCertifiedDeviceAccess_ThenModelsPixelsAndLatchesSeparately() {
    var pointer = new IrFarPtr(new IrConstantInt(IrType.I16, unchecked((short)0xA000)),
      new IrConstantInt(IrType.I16, 17));
    var read = new IrLoad(IrType.U8, pointer) { IsDeviceRead = true };
    var write = new IrStore(new IrConstantInt(IrType.U8, 9), pointer) {
      IsPackedMode13Write = true,
    };

    Assert.Multiple(() => {
      Assert.That(IrVgaEffects.ForInstruction(read), Is.EqualTo(
        IrVgaActivity.ReadsPixels | IrVgaActivity.ChangesLatches));
      Assert.That(IrVgaEffects.ForInstruction(write), Is.EqualTo(
        IrVgaActivity.WritesPixels | IrVgaActivity.ChangesLatches));
      Assert.That(IrVgaEffects.ForInstruction(new IrLoad(IrType.U8, pointer))
        .HasFlag(IrVgaActivity.Unknown), Is.True,
        "unmarked A000h far pointer must not be promoted to certified video memory");
      Assert.That(IrVgaEffects.ForInstruction(new IrStore(
          new IrConstantInt(IrType.U8, 9), pointer)).HasFlag(IrVgaActivity.Unknown), Is.True);
    });
  }

  [Test]
  public void ForInstruction_GivenPrivateLocalMemory_ThenItCannotMutateTheVgaController() {
    var slot = new IrAlloca(IrType.U8);
    var inner = new IrGep(slot, new IrConstantInt(IrType.I16, 0));
    var write = new IrStore(new IrConstantInt(IrType.U8, 4), inner);
    var read = new IrLoad(IrType.U8, inner);

    Assert.Multiple(() => {
      Assert.That(IrVgaEffects.ForInstruction(write), Is.EqualTo(IrVgaActivity.None));
      Assert.That(IrVgaEffects.ForInstruction(read), Is.EqualTo(IrVgaActivity.None));
      Assert.That(IrVgaEffects.ForInstruction(new IrInlineAsm("out dx, al"))
        .HasFlag(IrVgaActivity.Unknown), Is.True);
    });
  }

  [TestCase("rt_screen_mode", IrVgaActivity.ChangesModeOrPage)]
  [TestCase("rt_pset", IrVgaActivity.WritesPixels)]
  [TestCase("rt_point", IrVgaActivity.ReadsPixels)]
  [TestCase("rt_out", IrVgaActivity.Unknown)]
  [TestCase("rt_inp", IrVgaActivity.Unknown)]
  [TestCase("rt_poke", IrVgaActivity.Unknown)]
  [TestCase("rt_file_write", IrVgaActivity.Unknown)]
  public void ForInstruction_GivenRuntimeEntry_ThenClassifiesVgaStateBeforeOptimization(
      string name, IrVgaActivity required) {
    var callee = new IrFunction(name, IrType.Void);
    var call = new IrCall(IrType.Void, callee, []);
    Assert.That(IrVgaEffects.ForInstruction(call).HasFlag(required), Is.True);
  }

  [Test]
  public void ForInstruction_GivenPureMathIntrinsic_ThenItDoesNotInvalidateVgaState() {
    var sqrt = new IrFunction("llvm.sqrt.f64", IrType.F64,
      [new IrArgument(IrType.F64, 0)]);
    var call = new IrCall(IrType.F64, sqrt,
      [new IrConstantFloat(IrType.F64, 4.0)]);
    Assert.That(IrVgaEffects.ForInstruction(call), Is.EqualTo(IrVgaActivity.None));
  }

  [Test]
  public void Region_GivenInterleavedPlanarReadsAndWrites_ThenOnlyConfigurationCanBeReused() {
    var function = new IrFunction("f", IrType.Void);
    var block = function.CreateBlock("entry");
    var slot = block.Append(new IrAlloca(IrType.U8));
    var before = block.Append(new IrStore(new IrConstantInt(IrType.U8, 1), slot));
    var ptr = block.Append(new IrFarPtr(new IrConstantInt(IrType.I16, unchecked((short)0xA000)),
      new IrConstantInt(IrType.I16, 42)));
    var store = block.Append(new IrStore(new IrConstantInt(IrType.U8, 6), ptr) {
      IsPackedMode13Write = true,
    });
    var scratch = block.Append(new IrLoad(IrType.U8, slot));
    var read = block.Append(new IrLoad(IrType.U8, ptr) { IsDeviceRead = true });
    var next = block.Append(new IrStore(new IrConstantInt(IrType.U8, 7), slot));
    var state = new IrVgaRegionAnalysis(function);

    Assert.Multiple(() => {
      Assert.That(state.HasNoInterveningDeviceEffects(before, store), Is.True);
      Assert.That(state.HasUnchangedConfiguration(before, next), Is.True);
      Assert.That(state.HasUnchangedLatchState(before, store), Is.True);
      Assert.That(state.HasUnchangedLatchState(before, next), Is.False,
        "pixel reads and writes change latches even if controller configuration is stable");
      Assert.That(state.HasNoInterveningDeviceEffects(store, scratch), Is.False);
      Assert.That(state.HasNoInterveningDeviceEffects(scratch, read), Is.True);
      Assert.That(state.HasNoInterveningDeviceEffects(read, next), Is.False);
      Assert.That(state.Before(next)!.Value.LatchEpoch, Is.EqualTo(2));
      Assert.That(state.Before(next)!.Value.ObservationEpoch, Is.EqualTo(2));
    });
  }

  [Test]
  public void Region_GivenScreenModeOrOpaqueCalls_ThenConfigurationAndLatchesAreInvalidated() {
    var function = new IrFunction("f", IrType.Void);
    var block = function.CreateBlock("entry");
    var prior = block.Append(new IrAlloca(IrType.U8));
    var mode = block.Append(new IrCall(IrType.Void,
      new IrFunction("rt_screen_mode", IrType.Void), []));
    var after = block.Append(new IrAlloca(IrType.U8));
    var opaque = block.Append(new IrCall(IrType.Void,
      new IrFunction("rt_out", IrType.Void), []));
    var final = block.Append(new IrAlloca(IrType.U8));
    var state = new IrVgaRegionAnalysis(function);

    Assert.Multiple(() => {
      Assert.That(state.HasUnchangedConfiguration(prior, after), Is.False);
      Assert.That(state.HasUnchangedConfiguration(after, final), Is.False);
      Assert.That(state.HasUnchangedLatchState(prior, after), Is.False);
      Assert.That(state.HasUnchangedLatchState(after, final), Is.False);
      Assert.That(state.Before(final)!.Value.ConfigurationEpoch, Is.EqualTo(2));
      Assert.That(state.Before(final)!.Value.LatchEpoch, Is.EqualTo(2));
    });
  }

  [Test]
  public void Region_GivenReverseInstructionOrder_ThenNoForwardReuseProofIsGranted() {
    var function = new IrFunction("f", IrType.Void);
    var block = function.CreateBlock("entry");
    var early = block.Append(new IrAlloca(IrType.U8));
    var late = block.Append(new IrAlloca(IrType.U8));
    var state = new IrVgaRegionAnalysis(function);

    Assert.Multiple(() => {
      Assert.That(state.HasUnchangedConfiguration(early, late), Is.True);
      Assert.That(state.HasUnchangedLatchState(early, late), Is.True);
      Assert.That(state.HasNoInterveningDeviceEffects(early, late), Is.True);
      Assert.That(state.HasUnchangedConfiguration(late, early), Is.False);
      Assert.That(state.HasUnchangedLatchState(late, early), Is.False);
      Assert.That(state.HasNoInterveningDeviceEffects(late, early), Is.False);
    });
  }

  [Test]
  public void Region_GivenSeparateControlFlowBlocks_ThenNeverAssumesCommonDeviceState() {
    var function = new IrFunction("f", IrType.Void);
    var first = function.CreateBlock("first").Append(new IrAlloca(IrType.I16));
    var second = function.CreateBlock("second").Append(new IrAlloca(IrType.I16));
    var state = new IrVgaRegionAnalysis(function);
    Assert.That(state.HasNoInterveningDeviceEffects(first, second), Is.False);
    Assert.That(state.HasUnchangedConfiguration(first, second), Is.False);
    Assert.That(state.HasUnchangedLatchState(first, second), Is.False);
  }
}
