namespace PowerBasic.Compiler.Asm;

/// <summary>
/// Optional replacement for native x87 encoding. An assembler configured with a sink delegates every
/// x87 memory, stack and simple opcode to it, allowing a software implementation to preserve the
/// public FPU API used throughout code generation and the DOS runtime.
/// </summary>
public interface IX87InstructionSink {
  bool TryEmitMemory(byte opcode, int regField, Mem memory);
  bool TryEmitStack(byte opcode, byte modRmBase, St register);
  bool TryEmitSimple(byte opcode, byte modRm);
  void EmitWait();
}
