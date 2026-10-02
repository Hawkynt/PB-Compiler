namespace PowerBasic.Compiler.Backend.X86Native;

/// <summary>The machine a native x86 program runs on.</summary>
public enum X86Machine { I386, Amd64 }

/// <summary>
/// The eight legacy general-purpose registers, numbered as the encoding numbers them. The native
/// back end uses no others: REX then only ever means 64-bit width, and a byte operand is always
/// AL, CL, DL or BL.
/// </summary>
public enum X86Reg { Ax = 0, Cx = 1, Dx = 2, Bx = 3, Sp = 4, Bp = 5, Si = 6, Di = 7 }

/// <summary>An operand width in bytes.</summary>
public enum X86Width { Byte = 1, Word = 2, Dword = 4, Qword = 8 }

/// <summary>The condition codes, numbered as <c>Jcc</c>/<c>SETcc</c> encode them.</summary>
public enum X86Cond {
  Overflow = 0, NoOverflow = 1, Below = 2, AboveOrEqual = 3, Equal = 4, NotEqual = 5, BelowOrEqual = 6, Above = 7,
  Sign = 8, NoSign = 9, Parity = 10, NoParity = 11, Less = 12, GreaterOrEqual = 13, LessOrEqual = 14, Greater = 15,
}

/// <summary>The two-operand ALU instructions that share the <c>00</c>-<c>3F</c> encoding block, by their <c>/digit</c>.</summary>
public enum X86Alu { Add = 0, Or = 1, Adc = 2, Sbb = 3, And = 4, Sub = 5, Xor = 6, Cmp = 7 }

/// <summary>The <c>F6</c>/<c>F7</c> group, by <c>/digit</c>.</summary>
public enum X86Unary { Not = 2, Neg = 3, Mul = 4, Imul = 5, Div = 6, Idiv = 7 }

/// <summary>The shift group, by <c>/digit</c>.</summary>
public enum X86Shift { Rcl = 2, Rcr = 3, Shl = 4, Shr = 5, Sar = 7 }

/// <summary>The x87 binary operations that pop: <c>ST(1) = ST(1) op ST(0)</c>, by their second opcode byte.</summary>
public enum X87Op { AddPop = 0xC1, MulPop = 0xC9, SubPop = 0xE9, DivPop = 0xF9 }

/// <summary>The register-only x87 instructions the native back end emits, by their two opcode bytes.</summary>
public enum X87Code {
  Fsqrt = 0xD9FA, Fsin = 0xD9FE, Fcos = 0xD9FF, Fptan = 0xD9F2, Fpatan = 0xD9F3,
  Fld1 = 0xD9E8, Fldz = 0xD9EE, Fldln2 = 0xD9ED, Fldlg2 = 0xD9EC, Fldl2e = 0xD9EA, Fldl2t = 0xD9E9,
  Fxch = 0xD9C9, Fyl2x = 0xD9F1, F2xm1 = 0xD9F0, Frndint = 0xD9FC, Fscale = 0xD9FD,
  Fchs = 0xD9E0, Fabs = 0xD9E1,
  /// <summary><c>fld st(0)</c>: duplicates the top.</summary>
  Duplicate = 0xD9C0,
  /// <summary><c>fsub st(1), st(0)</c>: ST(1) = ST(1) - ST(0), no pop.</summary>
  SubtractFromSt1 = 0xDCE9,
  /// <summary><c>fstp st(1)</c>: drops ST(1), keeping the top.</summary>
  DropSt1 = 0xDDD9,
}

/// <summary>The section a label lives in.</summary>
public enum X86Section { Text, Data, Bss }

public static class X86Facts {
  public static X86Cond Invert(this X86Cond condition) => (X86Cond)((int)condition ^ 1);

  /// <summary>The width of an address and of the stack's slots.</summary>
  public static X86Width Word(this X86Machine machine) => machine == X86Machine.Amd64 ? X86Width.Qword : X86Width.Dword;

  public static int Bytes(this X86Width width) => (int)width;
}
