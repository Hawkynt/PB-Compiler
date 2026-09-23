using PowerBasic.Compiler.Backend.Targets;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class X86TargetTests {
  [Test]
  public void X86RegisterFileExposesAliasedScalarViews() {
    Assert.Multiple(() => {
      Assert.That(X86RegisterFile.Ax, Is.EqualTo(X86RegisterFile.Gpr16[0]));
      Assert.That(X86RegisterFile.Al, Is.EqualTo(new MachineRegister("al", 0, 8, 0)));
      Assert.That(X86RegisterFile.Ah, Is.EqualTo(new MachineRegister("ah", 4, 8, 0)));
      Assert.That(X86RegisterFile.Eax.AliasGroup, Is.EqualTo(X86RegisterFile.Ax.AliasGroup));
      Assert.That(X86RegisterFile.Rax.AliasGroup, Is.EqualTo(X86RegisterFile.Ax.AliasGroup));
      Assert.That(X86RegisterFile.Gpr64LowBytes[8].Name, Is.EqualTo("r8b"));
      Assert.That(X86RegisterFile.Gpr64Words[8].Name, Is.EqualTo("r8w"));
      Assert.That(X86RegisterFile.Gpr64Dwords[8].Name, Is.EqualTo("r8d"));
    });
  }

  [TestCase(X86Mode.Bit16, Reg.AL, MRegSize.Byte, "al", 0)]
  [TestCase(X86Mode.Bit16, Reg.AH, MRegSize.Byte, "ah", 4)]
  [TestCase(X86Mode.Bit32, Reg.EAX, MRegSize.Dword, "eax", 0)]
  [TestCase(X86Mode.Bit32, Reg.AH, MRegSize.Byte, "ah", 4)]
  [TestCase(X86Mode.Bit64, Reg.AX, MRegSize.Qword, "rax", 0)]
  public void HostedRegisterFilePreservesRegisterViewAndEncoding(X86Mode mode, Reg physical, MRegSize size, string name, int encoding) {
    var register = new X86TargetRegisterFile(mode).RegisterFor(MReg.Physical_(physical, size));
    Assert.That(register.Name, Is.EqualTo(name));
    Assert.That(register.Encoding, Is.EqualTo(encoding));
  }

  [Test]
  public void X86_64RejectsLegacyHighByteRegisters() {
    Assert.That(() => new X86TargetRegisterFile(X86Mode.Bit64)
      .RegisterFor(MReg.Physical_(Reg.AH, MRegSize.Byte)),
      Throws.InvalidOperationException.With.Message.Contains("not encodable"));
  }

  [Test]
  public void X86_64EncoderRejectsHighByteWhenRexIsRequired() {
    var encoder = new X86InstructionEncoder(X86Mode.Bit64);
    Assert.That(() => encoder.MoveRegister(
      X86RegisterFile.Ah, X86RegisterFile.Gpr64LowBytes[8]),
      Throws.ArgumentException.With.Message.Contains("REX"));
  }

  [Test]
  public void X86_32_UsesCdeclAndFrameShell() {
    var target = new X86MachineTarget(X86Mode.Bit32, X86Abi.I386Cdecl);
    var code = target.Emitter.EmitFunction([]).Bytes;
    Assert.That(code, Is.EqualTo(new byte[] { 0x55, 0x89, 0xE5, 0x5D, 0xC3 }));
  }

  [Test]
  public void X86_64_WindowsUsesShadowSpaceAndFrameShell() {
    var target = new X86MachineTarget(X86Mode.Bit64, X86Abi.Windows64);
    var code = target.Emitter.EmitFunction([]).Bytes;
    Assert.That(code, Is.EqualTo(new byte[] {
      0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20,
      0x48, 0x83, 0xC4, 0x20, 0x5D, 0xC3
    }));
    Assert.That(target.Abi.ShadowSpaceBytes, Is.EqualTo(32));
    Assert.That(target.Abi.ArgumentRegisters.Select(r => r.Name),
      Is.EqualTo(new[] { "rcx", "rdx", "r8", "r9" }));
  }

  [Test]
  public void X86_64_EncodesExtendedRegisterImmediate() {
    var encoder = new X86InstructionEncoder(X86Mode.Bit64);
    Assert.That(encoder.MoveImmediate(X86RegisterFile.Gpr64[8], 0x1122334455667788UL),
      Is.EqualTo(new byte[] { 0x49, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 }));
  }

  [Test]
  public void X86_64_EncodesStackAdjustment() {
    var encoder = new X86InstructionEncoder(X86Mode.Bit64);
    Assert.That(encoder.AdjustStack(32, allocate: true), Is.EqualTo(new byte[] { 0x48, 0x83, 0xEC, 0x20 }));
    Assert.That(encoder.AdjustStack(32, allocate: false), Is.EqualTo(new byte[] { 0x48, 0x83, 0xC4, 0x20 }));
  }

  [Test]
  public void X86_64_EncodesRegisterMoveAndArithmeticImmediate() {
    var encoder = new X86InstructionEncoder(X86Mode.Bit64);
    Assert.That(encoder.MoveRegister(X86RegisterFile.Gpr64[8], X86RegisterFile.Gpr64[0]),
      Is.EqualTo(new byte[] { 0x49, 0x89, 0xC0 }));
    Assert.That(encoder.AddImmediate(X86RegisterFile.Gpr64[0], 7),
      Is.EqualTo(new byte[] { 0x48, 0x83, 0xC0, 0x07 }));
    Assert.That(encoder.SubImmediate(X86RegisterFile.Gpr64[8], 300),
      Is.EqualTo(new byte[] { 0x49, 0x81, 0xE8, 0x2C, 0x01, 0x00, 0x00 }));
    Assert.That(encoder.XorImmediate(X86RegisterFile.Gpr64[0], 0),
      Is.EqualTo(new byte[] { 0x48, 0x83, 0xF0, 0x00 }));
    Assert.That(encoder.PushImmediate(127), Is.EqualTo(new byte[] { 0x6A, 0x7F }));
  }

  [Test]
  public void HostedX86Emitter_EmitsIndependentX86_16MachineFunction() {
    var target = new X86MachineTarget(X86Mode.Bit16, X86Abi.I8086Cdecl);
    var registers = new X86TargetRegisterFile(X86Mode.Bit16);
    var abi = new X86TargetAbi(X86Mode.Bit16, "i8086-cdecl", 2, 0,
      [registers.ReturnValue], registers.ReturnValue, new HashSet<MachineRegister>());
    var function = new X86TargetMachineFunction(X86Mode.Bit16, abi, [
      new(X86TargetOpcode.MoveImmediate, [registers.ReturnValue], 42),
    ]);

    var code = target.HostedEmitter.Emit(function);

    Assert.That(code.Bytes, Is.EqualTo(new byte[] { 0x55, 0x89, 0xE5, 0xB8, 0x2A, 0x00, 0x5D, 0xC3 }));
  }

  [Test]
  public void HostedX86Emitter_EmitsIndependentX86_32MachineFunction() {
    var target = new X86MachineTarget(X86Mode.Bit32, X86Abi.I386Cdecl);
    var registers = new X86TargetRegisterFile(X86Mode.Bit32);
    var abi = new X86TargetAbi(X86Mode.Bit32, "i386-cdecl", 4, 0,
      [registers.ReturnValue], registers.ReturnValue, new HashSet<MachineRegister>());
    var function = new X86TargetMachineFunction(X86Mode.Bit32, abi, [
      new(X86TargetOpcode.MoveImmediate, [registers.ReturnValue], 42),
      new(X86TargetOpcode.AddImmediate, [registers.ReturnValue], 1),
    ]);

    var code = target.HostedEmitter.Emit(function);

    Assert.That(code.Bytes, Is.EqualTo(new byte[] { 0x55, 0x89, 0xE5, 0xB8, 0x2A, 0x00, 0x00, 0x00, 0x83, 0xC0, 0x01, 0x5D, 0xC3 }));
  }

  [Test]
  public void HostedX86Emitter_EmitsIndependentX86_64MachineFunction() {
    var target = new X86MachineTarget(X86Mode.Bit64, X86Abi.SysV64);
    var registers = new X86TargetRegisterFile(X86Mode.Bit64);
    var abi = new X86TargetAbi(X86Mode.Bit64, "x86-64-sysv", 16, 0,
      [registers.ReturnValue], registers.ReturnValue, new HashSet<MachineRegister>());
    var function = new X86TargetMachineFunction(X86Mode.Bit64, abi, [
      new(X86TargetOpcode.MoveImmediate, [registers.ReturnValue], 42),
      new(X86TargetOpcode.Return, []),
    ]);

    var code = target.HostedEmitter.Emit(function);

    Assert.That(code.Bytes, Is.EqualTo(new byte[] {
      0x55, 0x48, 0x89, 0xE5, 0x48, 0xB8, 0x2A, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0xC3, 0x5D, 0xC3
    }));
  }

  [Test]
  public void MismatchedAbiAndModeIsRejected() {
    Assert.That(() => new X86MachineTarget(X86Mode.Bit32, X86Abi.Windows64), Throws.ArgumentException);
  }

  [Test]
  public void RelativeCallProducesRelocationInsteadOfGuessingAnAddress() {
    var code = X86RelocationEncoder.CallRelative32("callee");
    Assert.That(code.Bytes, Is.EqualTo(new byte[] { 0xE8, 0, 0, 0, 0 }));
    Assert.That(code.Relocations, Has.One.Items);
    Assert.That(code.Relocations[0], Is.EqualTo(
      new MachineRelocation(1, MachineRelocationKind.Relative32, "callee", -4)));
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
  public void MachinePipelineAdvancesLowIrThroughSelectionAndAllocationBoundaries() {
    var module = new IrModule("empty");
    Assert.That(module.TryAdvanceRepresentationStage(IrRepresentationStage.OptimizedSsa, out _), Is.True);
    Assert.That(IrLowIrLegalization.TryLegalize(module, out _), Is.True);

    Assert.That(IrMachinePipeline.TryLower(module, SelectionTarget.Baseline,
      out var machine, out var errors), Is.True);
    Assert.That(errors, Is.Empty);
    Assert.That(machine, Is.Not.Null);
    Assert.That(machine!.Functions, Is.Empty);
    Assert.That(module.RepresentationStage, Is.EqualTo(IrRepresentationStage.MachineIr));
  }

  [Test]
  public void Mos6502MachineTargetHasExplicitAbiAndFunctionShell() {
    var target = new Mos6502MachineTarget();
    Assert.That(target.Description.PointerBits, Is.EqualTo(16));
    Assert.That(target.Abi.ReturnRegister, Is.EqualTo(Mos6502RegisterFile.Accumulator));
    Assert.That(target.Emitter.EmitFunction([0xA9, 0x00]).Bytes, Is.EqualTo(new byte[] { 0xA9, 0x00, 0x60 }));
    Assert.That(IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.Mos6502), Is.TypeOf<Mos6502MachineTarget>());
    Assert.That(IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.X86_32), Is.TypeOf<X86MachineTarget>());
    Assert.That(IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.X86_64), Is.TypeOf<X86MachineTarget>());
  }

  [Test]
  public void MachineTargetOwnsTheLoweringContract() {
    var target = (X86MachineTarget)IrBackendTargetContract.CreateMachineTarget(IrBackendTarget.X86_64)!;
    var lowerer = (X86MachineLowering)target.CreateLowerer(SelectionTarget.Baseline);
    Assert.That(lowerer, Is.Not.Null);
    Assert.That(lowerer.Target, Is.EqualTo(new MachineTargetDescription("x86-64", 64, 64)));
    Assert.That(new X86MachineSelector(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachineAllocator(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachineScheduler(SelectionTarget.Baseline), Is.Not.Null);
    Assert.That(new X86MachinePostAllocation(), Is.Not.Null);
  }

  [Test]
  public void Mos6502OwnsAnInstructionSelector() {
    var target = new Mos6502MachineTarget();
    Assert.That(target.CreateLowerer(SelectionTarget.Baseline), Is.TypeOf<Mos6502MachineLowering>());
  }

  [Test]
  public void MachineFunctionClonePreservesTargetFamily() {
    var function = new X86MachineFunction("target") { TargetFamily = MachineTargetFamily.X86_64 };
    Assert.That(function.Clone().TargetFamily, Is.EqualTo(MachineTargetFamily.X86_64));
  }

  [Test]
  public void MachineTargetValidationRejectsCrossTargetProducts() {
    var function = new X86MachineFunction("target") { TargetFamily = MachineTargetFamily.X86_16 };
    Assert.That(X86MachineTargetValidation.TryValidate(function,
      new MachineTargetDescription("x86-64", 64, 64), out var error), Is.False);
    Assert.That(error, Does.Contain("X86_64"));
  }
}
