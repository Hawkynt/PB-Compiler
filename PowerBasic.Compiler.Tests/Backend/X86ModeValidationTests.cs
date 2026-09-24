using PowerBasic.Compiler.Backend.Targets;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class X86ModeValidationTests {
  [Test]
  public void HostedEmitterRejectsUnknownMachineModes() {
    var mode = (X86Mode)99;
    var abi = new X86TargetAbi(mode, "invalid", 1, 0, [],
      new MachineRegister("rax", 0, 64));
    var function = new X86TargetMachineFunction(mode, abi, []);

    Assert.That(() => new X86TargetMachineEmitter(new X86InstructionEncoder(X86Mode.Bit64))
      .Emit(function), Throws.ArgumentOutOfRangeException);
  }
}
