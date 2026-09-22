using PowerBasic.Compiler.Backend.Targets;
using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class X86TargetTests {
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
    Assert.That(code, Is.EqualTo(new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x5D, 0xC3 }));
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
  public void LegacyMachineStagesDelegateSelectionAndAllocation() {
    var stages = new X86LegacyMachineStages(
      new X86LegacySelector(SelectionTarget.Baseline),
      new X86LegacyAllocator(SelectionTarget.Baseline));
    Assert.That(stages.Selector, Is.Not.Null);
    Assert.That(stages.Allocator, Is.Not.Null);
  }
}
