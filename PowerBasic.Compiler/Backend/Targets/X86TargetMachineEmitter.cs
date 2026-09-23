namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Encodes the independent hosted x86 machine representation and its relocations.</summary>
public sealed class X86TargetMachineEmitter(X86InstructionEncoder encoder) {
  public MachineCode Emit(X86TargetMachineFunction function, bool preserveFramePointer = true) {
    ArgumentNullException.ThrowIfNull(function);
    var bytes = new List<byte>();
    var relocations = new List<MachineRelocation>();
    var registers = new X86TargetRegisterFile(function.Mode);
    if (preserveFramePointer) {
      bytes.AddRange(encoder.Push(registers.FramePointer));
      bytes.AddRange(function.Mode == X86Mode.Bit64 ? [0x48, 0x89, 0xE5] : [0x89, 0xE5]);
    }
    if (function.Abi.ShadowSpaceBytes != 0)
      bytes.AddRange(encoder.AdjustStack(function.Abi.ShadowSpaceBytes, allocate: true));

    foreach (var instruction in function.Instructions) {
      var offset = bytes.Count;
      switch (instruction.Opcode) {
        case X86TargetOpcode.MoveImmediate:
          bytes.AddRange(encoder.MoveImmediate(instruction.Registers[0], unchecked((ulong)instruction.Immediate)));
          break;
        case X86TargetOpcode.MoveRegister:
          bytes.AddRange(encoder.MoveRegister(instruction.Registers[0], instruction.Registers[1]));
          break;
        case X86TargetOpcode.AddImmediate:
          bytes.AddRange(encoder.AddImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.SubImmediate:
          bytes.AddRange(encoder.SubImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.AndImmediate:
          bytes.AddRange(encoder.AndImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.OrImmediate:
          bytes.AddRange(encoder.OrImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.XorImmediate:
          bytes.AddRange(encoder.XorImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.CompareImmediate:
          bytes.AddRange(encoder.CompareImmediate(instruction.Registers[0], checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.PushRegister:
          bytes.AddRange(encoder.Push(instruction.Registers[0]));
          break;
        case X86TargetOpcode.PopRegister:
          bytes.AddRange(encoder.Pop(instruction.Registers[0]));
          break;
        case X86TargetOpcode.PushImmediate:
          bytes.AddRange(encoder.PushImmediate(checked((int)instruction.Immediate)));
          break;
        case X86TargetOpcode.CallRelative:
          if (string.IsNullOrWhiteSpace(instruction.Symbol))
            throw new InvalidOperationException("relative calls require a symbol");
          var call = X86RelocationEncoder.CallRelative32(instruction.Symbol);
          relocations.AddRange(call.Relocations.Select(r => r with { Offset = r.Offset + offset }));
          bytes.AddRange(call.Bytes);
          break;
        case X86TargetOpcode.Return:
          bytes.AddRange(encoder.Ret());
          break;
        default:
          throw new NotSupportedException($"x86 target opcode '{instruction.Opcode}' is not encodable");
      }
    }

    if (function.Abi.ShadowSpaceBytes != 0)
      bytes.AddRange(encoder.AdjustStack(function.Abi.ShadowSpaceBytes, allocate: false));
    if (preserveFramePointer)
      bytes.AddRange(encoder.Pop(registers.FramePointer));
    bytes.AddRange(encoder.Ret());
    return new(bytes.ToArray(), relocations);
  }
}
