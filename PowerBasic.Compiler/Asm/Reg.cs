namespace PowerBasic.Compiler.Asm;

/// <summary>
/// x86 registers usable in 16-bit real mode. The low nibble is the hardware
/// register number (ModRM encoding), the high nibble is the register class.
/// </summary>
public enum Reg : byte {
  // 8-bit general purpose (class 0)
  AL = 0x00, CL = 0x01, DL = 0x02, BL = 0x03, AH = 0x04, CH = 0x05, DH = 0x06, BH = 0x07,
  // 16-bit general purpose (class 1)
  AX = 0x10, CX = 0x11, DX = 0x12, BX = 0x13, SP = 0x14, BP = 0x15, SI = 0x16, DI = 0x17,
  // 32-bit general purpose (class 2, 386; encoded via 0x66 operand-size prefix)
  EAX = 0x20, ECX = 0x21, EDX = 0x22, EBX = 0x23, ESP = 0x24, EBP = 0x25, ESI = 0x26, EDI = 0x27,
  // segment registers (class 3)
  ES = 0x30, CS = 0x31, SS = 0x32, DS = 0x33, FS = 0x34, GS = 0x35,
}

/// <summary>Classification and encoding helpers for <see cref="Reg"/>.</summary>
public static class RegExtensions {

  /// <summary>The 3-bit hardware register number used in ModRM/opcode encodings.</summary>
  public static int Index(this Reg register) => (int)register & 0x0F;

  public static bool IsByte(this Reg register) => ((int)register & 0xF0) == 0x00;
  public static bool IsWord(this Reg register) => ((int)register & 0xF0) == 0x10;
  public static bool IsDword(this Reg register) => ((int)register & 0xF0) == 0x20;
  public static bool IsSegment(this Reg register) => ((int)register & 0xF0) == 0x30;
  public static bool IsGeneralPurpose(this Reg register) => (int)register < 0x30;

  /// <summary>Operand size of a general-purpose register; segment registers report <see cref="OperandSize.Word"/>.</summary>
  public static OperandSize Size(this Reg register) => ((int)register >> 4) switch {
    0 => OperandSize.Byte,
    1 or 3 => OperandSize.Word,
    2 => OperandSize.Dword,
    _ => OperandSize.None,
  };
}
