using PowerBasic.Compiler.Backend.Targets;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class MachineTargetTests {
  private sealed class UnclassifiedLowIrInstruction : IrInstruction {
    public UnclassifiedLowIrInstruction() : base(IrType.Void) { }
  }

  [Test]
  public void ProductionTargetsConsumeLowIr() {
    Assert.That(IrBackendTargetContract.RequiredInputStage(IrBackendTarget.C),
      Is.EqualTo(IrRepresentationStage.LowIr));
    Assert.That(IrBackendTargetContract.RequiredInputStage(IrBackendTarget.PowerBasic35),
      Is.EqualTo(IrRepresentationStage.LowIr));
    Assert.That(IrBackendTargetContract.RequiredInputStage(IrBackendTarget.X86_16),
      Is.EqualTo(IrRepresentationStage.LowIr));
  }

  [Test]
  public void LowIrBoundaryRequiresIndependentVerification() {
    var module = new IrModule("test");
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out var errors), Is.True);
    Assert.That(errors, Is.Empty);
    Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
  }

  [Test]
  public void RepresentationBoundariesAreMonotonicAndAdjacent() {
    var module = new IrModule("test");
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.LowIr, out var skipped), Is.False);
    Assert.That(skipped, Does.Contain("skip"));
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.LowIr, out _), Is.True);
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out var backwards), Is.False);
    Assert.That(backwards, Does.Contain("backwards"));
  }

  [Test]
  public void MachinePipelineProducesMachineIrWithoutRelabelingItsLowIrSource() {
    var module = new IrModule("empty");
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out _), Is.True);

    Assert.That(IrMachinePipeline.TryLower(module, SelectionTarget.Baseline,
      out var machine, out var errors), Is.True);
    Assert.That(errors, Is.Empty);
    Assert.That(machine, Is.Not.Null);
    Assert.That(machine!.Functions, Is.Empty);
    Assert.Multiple(() => {
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr),
        "selection/allocation produce a distinct machine product; the source remains target-neutral");
      Assert.That(machine.RepresentationStage, Is.EqualTo(IrRepresentationStage.MachineIr));
    });
  }

  [Test]
  public void OptimizedSsaBoundaryRejectsMalformedSsaInsteadOfMerelyRelabelingIt() {
    var module = new IrModule("malformed");
    var function = module.AddFunction(new IrFunction("f", IrType.Void));
    function.CreateBlock("entry").Append(new IrBinary(
      IrBinaryOp.Add,
      new IrConstantInt(IrType.I16, 1),
      new IrConstantInt(IrType.I16, 2)));

    Assert.That(module.TryAdvanceRepresentationStage(
      IrRepresentationStage.OptimizedSsa, out var error), Is.False);

    Assert.Multiple(() => {
      Assert.That(error, Does.Contain("does not end in a terminator"));
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.Lowered));
    });
  }

  [Test]
  public void LowIrBoundaryRejectsAnOperationWithoutAnExplicitSemanticContract() {
    var module = new IrModule("unclassified");
    var function = module.AddFunction(new IrFunction("f", IrType.Void));
    var block = function.CreateBlock("entry");
    block.Append(new UnclassifiedLowIrInstruction());
    block.Append(new IrRet());

    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out var ssaError),
      Is.True, ssaError);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out var errors), Is.False);

    Assert.Multiple(() => {
      Assert.That(errors, Has.Some.Contains("UnclassifiedLowIrInstruction"));
      Assert.That(errors, Has.Some.Contains("semantic contract"));
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.OptimizedSsa));
    });
  }

  [Test]
  public void SourceModuleCannotBeRelabeledAsMachineSsa() {
    var module = new IrModule("source");
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out _), Is.True);

    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.MachineSsa, out var error), Is.False);
    Assert.Multiple(() => {
      Assert.That(error, Does.Contain("machine-product stage"));
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    });
  }

  [Test]
  public void MachinePipeline_CollectsEveryFunctionLoweringDecline() {
    var module = new IrModule("unsupported");
    foreach (var name in new[] { "first", "second" }) {
      var function = new IrFunction(name, IrType.Void);
      var builder = new IrBuilder(function.CreateBlock("entry"));
      builder.InlineAsm(new IrInlineAsm("DAA"));
      builder.Ret();
      module.AddFunction(function);
    }
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out _), Is.True);

    Assert.That(IrMachinePipeline.TryLower(module, SelectionTarget.Baseline,
      out var machine, out var errors), Is.False);

    Assert.Multiple(() => {
      Assert.That(machine, Is.Null);
      Assert.That(errors, Has.Count.EqualTo(2));
      Assert.That(errors[0], Does.Contain("first"));
      Assert.That(errors[1], Does.Contain("second"));
      Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.LowIr));
    });
  }

  [Test]
  public void Mos6502MachineTargetHasExplicitAbiAndFunctionShell() {
    var target = new Mos6502MachineTarget();
    Assert.That(target.Description.PointerBits, Is.EqualTo(16));
    Assert.That(target.Abi.ReturnRegister, Is.EqualTo(Mos6502RegisterFile.Accumulator));
    Assert.That(target.Emitter.EmitFunction([0xA9, 0x00]).Bytes, Is.EqualTo(new byte[] { 0xA9, 0x00, 0x60 }));
    Assert.That(IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.Mos6502), Is.TypeOf<Mos6502MachineTarget>());
    Assert.That(IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.X86_16), Is.Null,
      "x86-16 is lowered by X86MachineLowering directly");
  }

  [Test]
  public void MachineTargetOwnsTheLoweringContract() {
    var lowerer = new X86MachineLowering(SelectionTarget.Baseline);
    Assert.That(lowerer.Target, Is.EqualTo(new MachineTargetDescription(MachineTargetFamily.X86_16)));
    Assert.That(new X86MachineSelector(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachineAllocator(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachineScheduler(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachinePostAllocation(), Is.Not.Null);
  }

  [Test]
  public void X86MachineLowering_GivenMalformedInputIr_ThenReturnsVerifierDiagnostics() {
    var function = new IrFunction("malformed", IrType.Void);
    var block = function.CreateBlock("entry");
    block.Append(new IrBinary(IrBinaryOp.Add,
      new IrConstantInt(IrType.I16, 1), new IrConstantInt(IrType.I16, 2)));
    var lowerer = new X86MachineLowering(SelectionTarget.Baseline);

    Assert.That(lowerer.TrySelect(function, out var selected, out var error), Is.False);
    Assert.Multiple(() => {
      Assert.That(selected, Is.Null);
      Assert.That(error, Does.Contain("input IR failed verification"));
      Assert.That(error, Does.Contain("entry"));
    });
  }

  [Test]
  public void X86MachineLoweringRejectsNonX86TargetFamilies() {
    Assert.That(
      () => new X86MachineLowering(new SelectionTarget(TargetFamily: MachineTargetFamily.Mos6502)),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Test]
  public void Mos6502OwnsAnInstructionSelector() {
    var target = new Mos6502MachineTarget();
    Assert.That(target.CreateLowerer(SelectionTarget.Baseline), Is.TypeOf<Mos6502MachineLowering>());
  }

  [Test]
  public void Mos6502Lowering_GivenUnsupportedIr_ThenDeclinesWithConstructAndBlock() {
    var function = new IrFunction("f", IrType.Void);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    builder.InlineAsm(new IrInlineAsm("NOP"));
    builder.Ret();
    var lowerer = new Mos6502MachineLowering();

    Assert.That(lowerer.TrySelect(function, out var selected, out var error), Is.False);
    Assert.Multiple(() => {
      Assert.That(selected, Is.Null);
      Assert.That(error, Does.Contain("IrInlineAsm"));
      Assert.That(error, Does.Contain("entry"));
    });
  }

  [Test]
  public void Mos6502Emitter_GivenUnselectedOpcode_ThenThrowsInsteadOfDroppingIt() {
    var function = new X86MachineFunction("unsupported");
    var block = new MBlock("entry");
    block.Instructions.Add(new MInstr(MOpcode.InlineAsm, [], MInstrEffect.None));
    function.Blocks.Add(block);
    var target = new Mos6502MachineTarget();

    Assert.Throws<NotSupportedException>(() => ((Mos6502MachineEmitter)target.Emitter).EmitFunction(function));
  }

  [Test]
  public void MachineFunctionClonePreservesTargetFamily() {
    var function = new X86MachineFunction("target") { TargetFamily = MachineTargetFamily.Mos6502 };
    Assert.That(function.Clone().TargetFamily, Is.EqualTo(MachineTargetFamily.Mos6502));
  }

  [Test]
  public void MachineTargetValidationRejectsCrossTargetProducts() {
    var function = new X86MachineFunction("target") { TargetFamily = MachineTargetFamily.Mos6502 };
    var lowerer = new X86MachineLowering(SelectionTarget.Baseline);

    Assert.That(lowerer.TryAllocate(new IrFunction("target", IrType.Void), function, out var machine, out var error), Is.False);
    Assert.That(machine, Is.Null);
    Assert.That(error, Does.Contain("6502"));
  }

}
