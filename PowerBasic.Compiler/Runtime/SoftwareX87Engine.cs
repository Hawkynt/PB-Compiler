using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Integer-only x87 implementation for 8086-class targets. Each logical stack entry keeps the full
/// x87 information in canonical form: a normalized 64-bit explicit significand, signed unbiased
/// exponent and sign/class word. No host floating-point type participates in generated semantics.
/// </summary>
public sealed partial class SoftwareX87Engine : IX87InstructionSink {
  internal const int SlotBytes = 12;
  internal const int SlotCount = 8;
  internal const int Sig0 = 0;
  internal const int Sig1 = 2;
  internal const int Sig2 = 4;
  internal const int Sig3 = 6;
  internal const int Exponent = 8;
  internal const int Meta = 10;

  internal const ushort SignMask = 0x0001;
  internal const ushort ClassMask = 0x000E;
  internal const ushort ClassFinite = 0x0000;
  internal const ushort ClassZero = 0x0002;
  internal const ushort ClassInfinity = 0x0004;
  internal const ushort ClassNaN = 0x0006;
  /// <summary>Canonical NaNs retain whether the source was signaling even though the payload is quieted.</summary>
  internal const ushort SignalingNaNMask = 0x0010;

  internal const int ScratchA = 0;
  internal const int ScratchB = 12;
  internal const int ScratchC = 24;
  internal const int ScratchD = 36;
  internal const int ScratchGuardA = 48; // five words, low guard then four significand words
  internal const int ScratchGuardB = 58;
  internal const int ScratchWide = 68;   // eight words for 64x64 product / long division
  internal const int ScratchMisc = 84;
  internal const int Math0 = 112;
  internal const int Math1 = 124;
  internal const int Math2 = 136;
  internal const int Math3 = 148;
  internal const int Math4 = 160;
  internal const int Math5 = 172;
  private const int ScratchBytes = 224;

  private readonly Assembler _asm;
  private readonly TextAssembler _textAssembler;
  private readonly Label _slots;
  private readonly Label _valid;
  private readonly Label _control;
  private readonly Label _status;
  private readonly Label _scratch;
  private bool _stateEmitted;

  public SoftwareX87Engine(Assembler assembler) {
    this._asm = assembler ?? throw new ArgumentNullException(nameof(assembler));
    this._textAssembler = new TextAssembler(assembler);
    this._slots = assembler.DefineLabel();
    this._valid = assembler.DefineLabel();
    this._control = assembler.DefineLabel();
    this._status = assembler.DefineLabel();
    this._scratch = assembler.DefineLabel();
  }

  public bool TryEmitInline(string mnemonic, string operandsText, IAsmSymbolResolver? resolver, out string? error) {
    this.EnsureState();
    error = null;
    if (!this._textAssembler.TryParseOperands(operandsText, resolver, out var operands, out error))
      return false;
    mnemonic = mnemonic.ToUpperInvariant();

    switch (mnemonic) {
      case "FLD": return this.EmitInlineFld(operands, out error);
      case "FST": return this.EmitInlineFst(operands, pop: false, out error);
      case "FSTP": return this.EmitInlineFst(operands, pop: true, out error);
      case "FILD": return this.EmitInlineFild(operands, out error);
      case "FIST": return this.EmitInlineFist(operands, pop: false, out error);
      case "FISTP": return this.EmitInlineFist(operands, pop: true, out error);
      case "FXCH": return this.EmitInlineFxch(operands, out error);
      case "FFREE": return this.EmitInlineFfree(operands, out error);
      case "FADD" or "FMUL" or "FSUB" or "FSUBR" or "FDIV" or "FDIVR":
        return this.EmitInlineArithmetic(mnemonic, operands, pop: false, integer: false, out error);
      case "FADDP" or "FMULP" or "FSUBP" or "FSUBRP" or "FDIVP" or "FDIVRP":
        return this.EmitInlineArithmetic(mnemonic[..^1], operands, pop: true, integer: false, out error);
      case "FIADD" or "FIMUL" or "FISUB" or "FISUBR" or "FIDIV" or "FIDIVR":
        return this.EmitInlineArithmetic(mnemonic[1..], operands, pop: false, integer: true, out error);
      case "FCOM" or "FCOMP" or "FUCOM" or "FUCOMP" or "FICOM" or "FICOMP":
        return this.EmitInlineCompare(mnemonic, operands, out error);
      case "FCOMPP" or "FUCOMPP": return this.EmitInlineComparePopPop(mnemonic, operands, out error);
      case "FTST": return this.EmitInlineFtst(operands, out error);
      case "FCHS": return this.EmitInlineSign(operands, absolute: false, out error);
      case "FABS": return this.EmitInlineSign(operands, absolute: true, out error);
      case "FSQRT" or "FRNDINT" or "FSCALE" or "FPREM" or "FPREM1" or "FPTAN" or "FPATAN" or
           "F2XM1" or "FYL2X" or "FYL2XP1" or "FSIN" or "FCOS" or "FSINCOS":
        return this.EmitInlineTranscendental(mnemonic, operands, out error);
      case "FLDZ" or "FLD1" or "FLDPI" or "FLDL2E" or "FLDL2T" or "FLDLG2" or "FLDLN2":
        return this.EmitInlineConstant(mnemonic, operands, out error);
      case "FINIT" or "FNINIT": return this.EmitInlineInit(operands, out error);
      case "FCLEX" or "FNCLEX": return this.EmitInlineClear(operands, out error);
      case "FINCSTP": return this.EmitInlineRotateStack(operands, +1, out error);
      case "FDECSTP": return this.EmitInlineRotateStack(operands, -1, out error);
      case "FWAIT" or "WAIT": return this.EmitInlineWait(operands, out error);
      case "FSTSW" or "FNSTSW": return this.EmitInlineStatus(operands, out error);
      case "FSTCW" or "FNSTCW": return this.EmitInlineStoreControl(operands, out error);
      case "FLDCW": return this.EmitInlineLoadControl(operands, out error);
      default: return false;
    }
  }

  public bool TryEmitMemory(byte opcode, int regField, Mem memory) {
    this.EnsureState();
    return (opcode, regField) switch {
      (0xD9, 0) => this.EmitLoadReal(memory, 32),
      (0xDD, 0) => this.EmitLoadReal(memory, 64),
      (0xDB, 5) => this.EmitLoadReal(memory, 80),
      (0xD9, 2) => this.EmitStoreReal(memory, 32, pop: false),
      (0xD9, 3) => this.EmitStoreReal(memory, 32, pop: true),
      (0xDD, 2) => this.EmitStoreReal(memory, 64, pop: false),
      (0xDD, 3) => this.EmitStoreReal(memory, 64, pop: true),
      (0xDB, 7) => this.EmitStoreReal(memory, 80, pop: true),
      (0xDF, 0) => this.EmitLoadInteger(memory, 16),
      (0xDB, 0) => this.EmitLoadInteger(memory, 32),
      (0xDF, 5) => this.EmitLoadInteger(memory, 64),
      (0xDF, 2) => this.EmitStoreInteger(memory, 16, pop: false),
      (0xDF, 3) => this.EmitStoreInteger(memory, 16, pop: true),
      (0xDB, 2) => this.EmitStoreInteger(memory, 32, pop: false),
      (0xDB, 3) => this.EmitStoreInteger(memory, 32, pop: true),
      (0xDF, 7) => this.EmitStoreInteger(memory, 64, pop: true),
      (0xD9, 5) => this.EmitLoadControl(memory),
      (0xD9, 7) => this.EmitStoreControl(memory),
      (0xDD, 7) => this.EmitStoreStatus(memory),
      (0xD8, >= 0 and <= 7) => this.EmitArithmeticMemory(regField, memory, integer: false, bits: 32),
      (0xDC, >= 0 and <= 7) => this.EmitArithmeticMemory(regField, memory, integer: false, bits: 64),
      (0xDA, >= 0 and <= 7) => this.EmitArithmeticMemory(regField, memory, integer: true, bits: 32),
      (0xDE, >= 0 and <= 7) => this.EmitArithmeticMemory(regField, memory, integer: true, bits: 16),
      _ => false,
    };
  }

  public bool TryEmitStack(byte opcode, byte modRmBase, St register) {
    this.EnsureState();
    return (opcode, modRmBase) switch {
      (0xD9, 0xC0) => this.EmitLoadStack(register.Index),
      (0xD9, 0xC8) => this.EmitExchange(register.Index),
      (0xDD, 0xC0) => this.EmitFree(register.Index),
      (0xDD, 0xD0) => this.EmitStoreStack(register.Index, pop: false),
      (0xDD, 0xD8) => this.EmitStoreStack(register.Index, pop: true),
      (0xD8, >= 0xC0 and <= 0xF8) => this.EmitArithmeticStack(opcode, modRmBase, register.Index),
      (0xDC, >= 0xC0 and <= 0xF8) => this.EmitArithmeticStack(opcode, modRmBase, register.Index),
      (0xDE, 0xC0 or 0xC8 or 0xE0 or 0xE8 or 0xF0 or 0xF8) => this.EmitArithmeticPop(modRmBase, register.Index),
      (0xD8, 0xD0) => this.EmitCompareStack(register.Index, popCount: 0, unordered: false),
      (0xD8, 0xD8) => this.EmitCompareStack(register.Index, popCount: 1, unordered: false),
      (0xDD, 0xE0) => this.EmitCompareStack(register.Index, popCount: 0, unordered: true),
      (0xDD, 0xE8) => this.EmitCompareStack(register.Index, popCount: 1, unordered: true),
      _ => false,
    };
  }

  public bool TryEmitSimple(byte opcode, byte modRm) {
    this.EnsureState();
    return (opcode, modRm) switch {
      (0xD9, 0xE0) => this.EmitChangeSign(absolute: false),
      (0xD9, 0xE1) => this.EmitChangeSign(absolute: true),
      (0xD9, 0xE4) => this.EmitTestZero(),
      (0xD9, 0xE8) => this.EmitConstant("FLD1"),
      (0xD9, 0xE9) => this.EmitConstant("FLDL2T"),
      (0xD9, 0xEA) => this.EmitConstant("FLDL2E"),
      (0xD9, 0xEB) => this.EmitConstant("FLDPI"),
      (0xD9, 0xEC) => this.EmitConstant("FLDLG2"),
      (0xD9, 0xED) => this.EmitConstant("FLDLN2"),
      (0xD9, 0xEE) => this.EmitConstant("FLDZ"),
      (0xD9, 0xF0) => this.EmitUnaryMath("F2XM1"),
      (0xD9, 0xF1) => this.EmitUnaryMath("FYL2X"),
      (0xD9, 0xF2) => this.EmitUnaryMath("FPTAN"),
      (0xD9, 0xF3) => this.EmitUnaryMath("FPATAN"),
      (0xD9, 0xF5) => this.EmitUnaryMath("FPREM1"),
      (0xD9, 0xF6) => this.EmitRotateLogicalStack(-1),
      (0xD9, 0xF7) => this.EmitRotateLogicalStack(+1),
      (0xD9, 0xF8) => this.EmitUnaryMath("FPREM"),
      (0xD9, 0xF9) => this.EmitUnaryMath("FYL2XP1"),
      (0xD9, 0xFA) => this.EmitUnaryMath("FSQRT"),
      (0xD9, 0xFB) => this.EmitUnaryMath("FSINCOS"),
      (0xD9, 0xFC) => this.EmitUnaryMath("FRNDINT"),
      (0xD9, 0xFD) => this.EmitUnaryMath("FSCALE"),
      (0xD9, 0xFE) => this.EmitUnaryMath("FSIN"),
      (0xD9, 0xFF) => this.EmitUnaryMath("FCOS"),
      (0xDB, 0xE2) => this.EmitClearExceptions(),
      (0xDB, 0xE3) => this.EmitInit(),
      (0xDF, 0xE0) => this.EmitStatusToAx(),
      (0xDE, 0xD9) => this.EmitCompareStack(1, popCount: 2, unordered: false),
      (0xDA, 0xE9) => this.EmitCompareStack(1, popCount: 2, unordered: true),
      _ => false,
    };
  }

  public void EmitWait() { /* all software operations are synchronous */ }

  internal Mem Slot(int index, int offset = 0, OperandSize size = OperandSize.None) =>
    Mem.At(this._slots, index * SlotBytes + offset).WithSize(size).Cs();

  internal Mem Scratch(int offset, OperandSize size = OperandSize.None) =>
    Mem.At(this._scratch, offset).WithSize(size).Cs();

  internal Mem Control => Mem.Word(this._control).Cs();
  internal Mem Status => Mem.Word(this._status).Cs();
  internal Mem Valid => Mem.Byte(this._valid).Cs();
  internal Assembler Asm => this._asm;

  private void EnsureState() {
    if (this._stateEmitted)
      return;
    this._stateEmitted = true;
    var over = this._asm.DefineLabel();
    this._asm.Jmp(over);
    this._asm.Align(2);
    this._asm.MarkLabel(this._slots);
    this._asm.Db(new byte[SlotBytes * SlotCount]);
    this._asm.MarkLabel(this._valid);
    this._asm.Db(0);
    this._asm.Align(2);
    this._asm.MarkLabel(this._control);
    this._asm.Dw(0x037F);
    this._asm.MarkLabel(this._status);
    this._asm.Dw(0);
    this._asm.MarkLabel(this._scratch);
    this._asm.Db(new byte[ScratchBytes]);
    this._asm.MarkLabel(over);
  }

  internal static bool RequireNoOperands(IReadOnlyList<TextAssembler.ParsedAsmOperand> operands, out string? error) {
    if (operands.Count == 0) { error = null; return true; }
    error = "x87 instruction takes no operands";
    return false;
  }
}
