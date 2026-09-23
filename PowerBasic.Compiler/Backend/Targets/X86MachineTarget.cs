using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Backend.Targets;

public sealed class X86MachineTarget : IMachineTarget {
  private readonly X86Mode _mode;

  public X86MachineTarget(X86Mode mode, X86Abi abi) {
    if ((mode == X86Mode.Bit32) != (abi.PointerBits == 32))
      throw new ArgumentException("ABI and machine mode disagree.", nameof(abi));
    this._mode = mode;
    this.Description = new(mode == X86Mode.Bit64 ? "x86-64" : "x86-32", abi.PointerBits, abi.PointerBits);
    this.Abi = abi;
    this.Encoder = new X86InstructionEncoder(mode);
    this.Emitter = new X86MachineEmitter(this.Encoder, abi);
  }

  public MachineTargetDescription Description { get; }
  public IMachineAbi Abi { get; }
  public IMachineInstructionEncoder Encoder { get; }
  public IMachineEmitter Emitter { get; }

  public X86TargetMachineEmitter HostedEmitter => new((X86InstructionEncoder)this.Encoder);

  public IMachineFunctionLowerer CreateLowerer(SelectionTarget selectionTarget)
    => new X86MachineLowering(selectionTarget with {
      TargetFamily = this._mode == X86Mode.Bit64
        ? MachineTargetFamily.X86_64
        : MachineTargetFamily.X86_32,
      CpuLevel = this._mode == X86Mode.Bit64
        ? Math.Max(selectionTarget.CpuLevel, 686)
        : Math.Max(selectionTarget.CpuLevel, 386)
    });
}
