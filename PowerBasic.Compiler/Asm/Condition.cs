namespace PowerBasic.Compiler.Asm;

/// <summary>Condition codes for Jcc; the value is the low nibble of the opcode (0x70+cc / 0F 80+cc).</summary>
public enum Condition : byte {
  Overflow = 0x0,
  NotOverflow = 0x1,
  Below = 0x2,
  AboveOrEqual = 0x3,
  Equal = 0x4,
  NotEqual = 0x5,
  BelowOrEqual = 0x6,
  Above = 0x7,
  Sign = 0x8,
  NotSign = 0x9,
  Parity = 0xA,
  NotParity = 0xB,
  Less = 0xC,
  GreaterOrEqual = 0xD,
  LessOrEqual = 0xE,
  Greater = 0xF,

  // aliases
  Carry = Below,
  NotCarry = AboveOrEqual,
  Zero = Equal,
  NotZero = NotEqual,
  ParityEven = Parity,
  ParityOdd = NotParity,
}
