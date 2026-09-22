namespace PowerBasic.Compiler.Backend.Targets;

public sealed class X86MachineTarget : IMachineTarget {
  public X86MachineTarget(X86Mode mode, X86Abi abi) {
    if ((mode == X86Mode.Bit32) != (abi.PointerBits == 32))
      throw new ArgumentException("ABI and machine mode disagree.", nameof(abi));
    this.Description = new(mode == X86Mode.Bit64 ? "x86-64" : "x86-32", abi.PointerBits, abi.PointerBits);
    this.Abi = abi;
    this.Encoder = new X86InstructionEncoder(mode);
    this.Emitter = new X86MachineEmitter(this.Encoder, abi);
  }

  public MachineTargetDescription Description { get; }
  public IMachineAbi Abi { get; }
  public IMachineInstructionEncoder Encoder { get; }
  public IMachineEmitter Emitter { get; }
}
