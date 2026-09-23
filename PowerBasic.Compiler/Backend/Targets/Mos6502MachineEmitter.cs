namespace PowerBasic.Compiler.Backend.Targets;

public sealed class Mos6502MachineEmitter(IMachineInstructionEncoder encoder) : IMachineEmitter {
  public MachineCode EmitFunction(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var bytes = new List<byte>();
    var mos = (Mos6502InstructionEncoder)encoder;
    var frame = function.TargetAllocation is Mos6502Allocation allocation ? allocation.Frame : null;
    if (frame is not null)
      foreach (var saved in frame.SavedRegisters)
        bytes.AddRange(mos.LoadZeroPage((byte)(4 + saved)));
    if (frame is not null)
      foreach (var saved in frame.SavedRegisters)
        bytes.AddRange(mos.PushAccumulator());
    foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions)) {
      if (instruction.Opcode == MOpcode.Ret) { bytes.AddRange(encoder.Ret()); continue; }
      if (instruction.Opcode == MOpcode.Mov && instruction.Operands is [MOperand.Register, MOperand.Immediate immediate])
        bytes.AddRange(mos.MoveImmediate(Mos6502RegisterFile.Accumulator, unchecked((ulong)immediate.Value)));
      else if (instruction.Opcode == MOpcode.Add && instruction.Operands is [MOperand.Immediate add])
        bytes.AddRange(mos.AddImmediate((byte)add.Value));
      else if (instruction.Opcode == MOpcode.Sub && instruction.Operands is [MOperand.Immediate sub])
        bytes.AddRange(mos.SubImmediate((byte)sub.Value));
      else if (instruction.Opcode == MOpcode.And && instruction.Operands is [MOperand.Immediate and])
        bytes.AddRange(mos.AndImmediate((byte)and.Value));
      else if (instruction.Opcode == MOpcode.Or && instruction.Operands is [MOperand.Immediate orValue])
        bytes.AddRange(mos.OrImmediate((byte)orValue.Value));
      else if (instruction.Opcode == MOpcode.Xor && instruction.Operands is [MOperand.Immediate xor])
        bytes.AddRange(mos.XorImmediate((byte)xor.Value));
    }
    if (frame is not null) {
      // Preserve the ABI return register while restoring callee-saved pseudo-registers.
      bytes.AddRange(mos.PushAccumulator());
      foreach (var saved in frame.SavedRegisters.Reverse()) {
        bytes.AddRange(mos.PopAccumulator());
        bytes.AddRange(mos.StoreZeroPage((byte)(4 + saved)));
      }
      bytes.AddRange(mos.PopAccumulator());
    }
    bytes.AddRange(encoder.Ret());
    return new(bytes.ToArray(), []);
  }

  public MachineCode EmitFunction(ReadOnlySpan<byte> body, bool preserveFramePointer = true) {
    var bytes = new List<byte>(body.Length + 1);
    bytes.AddRange(body.ToArray());
    bytes.AddRange(encoder.Ret());
    return new(bytes.ToArray(), []);
  }
}
