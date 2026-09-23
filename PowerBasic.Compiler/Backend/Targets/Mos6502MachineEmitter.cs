namespace PowerBasic.Compiler.Backend.Targets;

public sealed class Mos6502MachineEmitter(IMachineInstructionEncoder encoder) : IMachineEmitter {
  public MachineCode EmitFunction(X86MachineFunction function) {
    ArgumentNullException.ThrowIfNull(function);
    var bytes = new List<byte>();
    foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions))
      if (instruction.Opcode == MOpcode.Ret)
        bytes.AddRange(encoder.Ret());
    if (bytes.Count == 0)
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
