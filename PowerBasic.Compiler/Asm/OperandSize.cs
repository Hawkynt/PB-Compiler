namespace PowerBasic.Compiler.Asm;

/// <summary>Size of an operand in bytes; <see cref="None"/> means "not specified".</summary>
public enum OperandSize : byte {
  None = 0,
  Byte = 1,
  Word = 2,
  Dword = 4,
  Qword = 8,
  Tbyte = 10,
}
