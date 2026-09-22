namespace PowerBasic.Compiler.Backend.Targets;

/// <summary>Emits a complete x86 function shell around already-selected machine instructions.</summary>
public sealed class X86MachineEmitter(IMachineInstructionEncoder encoder, IMachineAbi abi) : IMachineEmitter {
  public MachineCode EmitFunction(ReadOnlySpan<byte> body, bool preserveFramePointer = true) {
    var bytes = new List<byte>();
    if (preserveFramePointer) {
      bytes.AddRange(this.encoder.Push(this.abi.PointerBits == 64 ? X86RegisterFile.Gpr64[5] : X86RegisterFile.Gpr32[5]));
      bytes.AddRange(this.abi.PointerBits == 64 ? [0x48, 0x89, 0xE5] : [0x89, 0xE5]);
    }
    bytes.AddRange(body.ToArray());
    if (preserveFramePointer)
      bytes.AddRange(this.encoder.Pop(this.abi.PointerBits == 64 ? X86RegisterFile.Gpr64[5] : X86RegisterFile.Gpr32[5]));
    bytes.AddRange(this.encoder.Ret());
    return new(bytes.ToArray(), []);
  }
}
