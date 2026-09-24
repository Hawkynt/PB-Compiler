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
  public void X86Encoder_GivenInvalidScaledAddress_ThenRejectsInsteadOfEncodingScaleOne() {
    var encoder = new X86InstructionEncoder(X86Mode.Bit32);

    Assert.Throws<ArgumentOutOfRangeException>(() => encoder.MoveMemory(
      X86RegisterFile.Eax, new(null, X86RegisterFile.Ecx, 3, 0, 32), load: true));
  }

  [Test]
  public void X86TargetConfiguration_GivenUnknownEnumValues_ThenRejectsThem() {
    Assert.Multiple(() => {
      Assert.That(() => new X86InstructionEncoder((X86Mode)123), Throws.ArgumentOutOfRangeException);
      Assert.That(() => new X86MachineTarget((X86Mode)123, X86Abi.I8086Cdecl),
        Throws.ArgumentOutOfRangeException);
      Assert.That(() => new X86TargetRegisterFile((X86Mode)123), Throws.ArgumentOutOfRangeException);
      Assert.That(() => new X86VectorRegisterFile((X86VectorRegisterClass)123),
        Throws.ArgumentOutOfRangeException);
      Assert.That(() => X86Abi.For((IrCallConvention)123, X86Mode.Bit64),
        Throws.ArgumentOutOfRangeException);
    });
  }

  [TestCase(IrCallConvention.Basic, "", X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee)]
  [TestCase(IrCallConvention.Pascal, "", X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee)]
  [TestCase(IrCallConvention.Cdecl, "", X86StackArgumentOrder.RightToLeft, X86StackCleanup.Caller)]
  [TestCase(IrCallConvention.Stdcall, "", X86StackArgumentOrder.RightToLeft, X86StackCleanup.Callee)]
  [TestCase(IrCallConvention.Fastcall, "ax,dx,bx", X86StackArgumentOrder.LeftToRight, X86StackCleanup.Callee)]
  [TestCase(IrCallConvention.Watcall, "ax,dx,bx,cx", X86StackArgumentOrder.RightToLeft, X86StackCleanup.Callee)]
  public void X86Abi_GivenA16BitSourceConvention_ThenUsesItsOwnRegistersOrderAndCleanup(
      IrCallConvention convention, string expectedRegisterCsv, X86StackArgumentOrder order,
      X86StackCleanup cleanup) {
    var abi = X86Abi.For(convention, X86Mode.Bit16);

    Assert.Multiple(() => {
      Assert.That(string.Join(",", abi.ArgumentRegisters.Select(register => register.Name)),
        Is.EqualTo(expectedRegisterCsv));
      Assert.That(abi.ArgumentOrder, Is.EqualTo(order));
      Assert.That(abi.StackCleanup, Is.EqualTo(cleanup));
      if (convention is IrCallConvention.Fastcall or IrCallConvention.Watcall)
        Assert.That(abi.CalleeSavedRegisters, Does.Not.Contain(X86RegisterFile.Bx));
    });
  }

  [Test]
  public void X86Abi_GivenOverflowArguments_ThenPlacesThemInConventionStackOrder() {
    var types = Enumerable.Repeat(IrType.I16, 6).ToArray();
    var fastcall = X86Abi.For(IrCallConvention.Fastcall, X86Mode.Bit16).PlaceArguments(types);
    var watcall = X86Abi.For(IrCallConvention.Watcall, X86Mode.Bit16).PlaceArguments(types);

    Assert.Multiple(() => {
      Assert.That(fastcall.Select(argument => argument.StackOffset), Is.EqualTo(new[] { -1, -1, -1, 4, 2, 0 }));
      Assert.That(watcall.Select(argument => argument.StackOffset), Is.EqualTo(new[] { -1, -1, -1, -1, 0, 2 }));
    });
  }

  [Test]
  public void X86Abi_GivenStackOnlyArguments_ThenReportsOffsetsFromTheCallerStackPointer() {
    var types = new[] { IrType.I16, IrType.I32, IrType.I16 };
    var basic = X86Abi.For(IrCallConvention.Basic, X86Mode.Bit16).PlaceArguments(types);
    var cdecl = X86Abi.For(IrCallConvention.Cdecl, X86Mode.Bit16).PlaceArguments(types);

    Assert.Multiple(() => {
      Assert.That(basic.Select(argument => argument.StackOffset), Is.EqualTo(new[] { 6, 2, 0 }));
      Assert.That(cdecl.Select(argument => argument.StackOffset), Is.EqualTo(new[] { 0, 2, 6 }));
    });
  }

  [Test]
  public void X86Abi_GivenX64Fastcall_ThenUsesWindowsRegisterAndShadowSpaceContract() {
    var abi = X86Abi.For(IrCallConvention.Fastcall, X86Mode.Bit64);

    Assert.Multiple(() => {
      Assert.That(abi.ArgumentRegisters.Select(register => register.Name), Is.EqualTo(new[] { "rcx", "rdx", "r8", "r9" }));
      Assert.That(abi.ShadowSpaceBytes, Is.EqualTo(32));
      Assert.That(abi.ArgumentOrder, Is.EqualTo(X86StackArgumentOrder.RightToLeft));
      Assert.That(abi.StackCleanup, Is.EqualTo(X86StackCleanup.Caller));
      var locations = abi.PlaceArguments(Enumerable.Repeat(IrType.I64, 5).ToArray());
      Assert.That(locations[4].StackOffset, Is.EqualTo(32), "the fifth argument follows Win64 shadow space");
    });
  }

  [Test]
  public void X86Abi_Given32BitWatcall_ThenIncludesFourthWatcomRegisterAndCalleeCleanup() {
    var abi = X86Abi.For(IrCallConvention.Watcall, X86Mode.Bit32);

    Assert.Multiple(() => {
      Assert.That(abi.ArgumentRegisters.Select(register => register.Name),
        Is.EqualTo(new[] { "eax", "edx", "ebx", "ecx" }));
      Assert.That(abi.ArgumentOrder, Is.EqualTo(X86StackArgumentOrder.RightToLeft));
      Assert.That(abi.StackCleanup, Is.EqualTo(X86StackCleanup.Callee));
    });
  }

  [Test]
  public void X86Abi_Given64BitWatcall_ThenKeepsTheWatcomRegisterOrderDistinctFromFastcall() {
    var abi = X86Abi.For(IrCallConvention.Watcall, X86Mode.Bit64);

    Assert.Multiple(() => {
      Assert.That(abi.ArgumentRegisters.Select(register => register.Name),
        Is.EqualTo(new[] { "rax", "rdx", "rbx", "rcx" }));
      Assert.That(abi.ArgumentOrder, Is.EqualTo(X86StackArgumentOrder.RightToLeft));
      Assert.That(abi.StackCleanup, Is.EqualTo(X86StackCleanup.Callee));
      Assert.That(abi.CalleeSavedRegisters, Does.Not.Contain(X86RegisterFile.Gpr64[3]));
    });
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
  public void X86MachineLoweringRejectsNonX86TargetFamilies() {
    Assert.That(
      () => new X86MachineLowering(new SelectionTarget(TargetFamily: MachineTargetFamily.Mos6502)),
      Throws.ArgumentOutOfRangeException);
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
    block.Instructions.Add(new MInstr(MOpcode.Nop, [], MInstrEffect.None));
    function.Blocks.Add(block);
    var target = new Mos6502MachineTarget();

    Assert.Throws<NotSupportedException>(() => target.Emitter.EmitFunction(function));
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
