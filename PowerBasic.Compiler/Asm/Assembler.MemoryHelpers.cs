namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {
  /// <summary>
  /// Compiler-internal convenience for byte/word cell copies. x86 has no memory-to-memory MOV, so
  /// this expands through a saved AX/AL temporary and therefore preserves the architectural register
  /// state and flags. Wider copies must be expressed explicitly by the caller.
  /// </summary>
  public void Mov(Mem destination, Mem source) {
    var size = destination.Size != OperandSize.None ? destination.Size : source.Size;
    if (size == OperandSize.Byte) {
      this.Push(Reg.AX);
      this.Mov(Reg.AL, source.WithSize(OperandSize.Byte));
      this.Mov(destination.WithSize(OperandSize.Byte), Reg.AL);
      this.Pop(Reg.AX);
      return;
    }
    if (size is OperandSize.None or OperandSize.Word) {
      this.Push(Reg.AX);
      this.Mov(Reg.AX, source.WithSize(OperandSize.Word));
      this.Mov(destination.WithSize(OperandSize.Word), Reg.AX);
      this.Pop(Reg.AX);
      return;
    }
    throw new ArgumentException("memory-to-memory helper supports only byte/word cells");
  }
}
