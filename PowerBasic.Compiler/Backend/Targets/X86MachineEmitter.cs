namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Emits a complete x86 function shell around already-selected machine instructions.</summary>
public sealed class X86MachineEmitter(IMachineInstructionEncoder encoder, IMachineAbi abi) : IMachineEmitter {
  public MachineCode EmitFunction(ReadOnlySpan<byte> body, bool preserveFramePointer = true) {
    var bytes = new List<byte>();
    if (preserveFramePointer) {
      bytes.AddRange(encoder.Push(abi.PointerBits == 64 ? X86RegisterFile.Gpr64[5] : X86RegisterFile.Gpr32[5]));
      bytes.AddRange(abi.PointerBits == 64 ? [0x48, 0x89, 0xE5] : [0x89, 0xE5]);
    }
    bytes.AddRange(body.ToArray());
    if (preserveFramePointer)
      bytes.AddRange(encoder.Pop(abi.PointerBits == 64 ? X86RegisterFile.Gpr64[5] : X86RegisterFile.Gpr32[5]));
    bytes.AddRange(encoder.Ret());
    return new(bytes.ToArray(), []);
  }
}
