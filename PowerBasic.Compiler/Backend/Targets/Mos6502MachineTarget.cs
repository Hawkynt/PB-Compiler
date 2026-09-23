using PowerBasic.Compiler.Backend;

namespace PowerBasic.Compiler.Backend.Targets;

public sealed class Mos6502MachineTarget : IMachineTarget {
  public Mos6502MachineTarget() {
    Description = new("MOS 6502", 16, 8);
    Abi = new Mos6502Abi();
    Encoder = new Mos6502InstructionEncoder();
    Emitter = new Mos6502MachineEmitter(Encoder);
  }

  public MachineTargetDescription Description { get; }
  public IMachineAbi Abi { get; }
  public IMachineInstructionEncoder Encoder { get; }
  public IMachineEmitter Emitter { get; }

  public IMachineFunctionLowerer CreateLowerer(SelectionTarget selectionTarget)
    => throw new NotSupportedException(
      "the MOS 6502 target has an ABI and emitter shell, but no Low IR instruction selector yet");
}
