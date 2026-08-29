using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Returns true for inline-assembly instructions that are architecturally indistinguishable from
  /// doing nothing: no register/memory result changes, no flags change, and no memory operand can
  /// fault. Operand parsing happens before the decision so SPEED can never turn malformed assembly
  /// into an accepted program merely because its tokens resemble an identity.
  /// </summary>
  private bool IsZeroOverheadInlineAsmIdentity(InlineInstruction instruction, InlineAsmResolver resolver) {
    if (instruction.RepPrefix is not null)
      return false;

    this._textAssembler ??= new(this._asm);
    if (!this._textAssembler.TryParseOperands(instruction.Operands, resolver, out var operands, out _))
      return false;

    static bool SameGp(TextAssembler.ParsedAsmOperand left, TextAssembler.ParsedAsmOperand right)
      => left is TextAssembler.ParsedAsmRegister a
         && right is TextAssembler.ParsedAsmRegister b
         && IsGp(a.Register) && a.Register == b.Register;

    static bool SameVector(TextAssembler.ParsedAsmOperand left, TextAssembler.ParsedAsmOperand right, bool xmmOnly)
      => left is TextAssembler.ParsedAsmRegister a
         && right is TextAssembler.ParsedAsmRegister b
         && a.Register == b.Register
         && (xmmOnly ? a.Register.IsXmm() : a.Register.IsMmx() || a.Register.IsXmm());

    static bool IsImmediateZero(TextAssembler.ParsedAsmOperand operand)
      => operand is TextAssembler.ParsedAsmImmediate { Value: 0 };

    switch (instruction.Mnemonic) {
      case "MOV":
      case "XCHG":
        return operands.Count == 2 && SameGp(operands[0], operands[1]);

      case "MOVQ":
        return operands.Count == 2 && SameVector(operands[0], operands[1], xmmOnly: false);
      case "MOVDQA":
      case "MOVDQU":
        return operands.Count == 2 && SameVector(operands[0], operands[1], xmmOnly: true);

      // A zero-count scalar shift/rotate is architecturally a no-op, including EFLAGS. Only GP
      // register forms are accepted here; memory operands are retained because the access can fault.
      case "SHL":
      case "SAL":
      case "SHR":
      case "SAR":
      case "ROL":
      case "ROR":
      case "RCL":
      case "RCR":
        return operands.Count == 2
          && operands[0] is TextAssembler.ParsedAsmRegister r && IsGp(r.Register)
          && IsImmediateZero(operands[1]);

      // PBLENDW never changes flags. With equal XMM inputs it is an identity for every valid imm8.
      // With a zero mask it is also an identity when the ignored source is a register; a memory
      // source is retained because the architectural read can fault.
      case "PBLENDW":
        return operands.Count == 3
          && operands[0] is TextAssembler.ParsedAsmRegister d && d.Register.IsXmm()
          && operands[1] is TextAssembler.ParsedAsmRegister s && s.Register.IsXmm()
          && operands[2] is TextAssembler.ParsedAsmImmediate immediate
          && (d.Register == s.Register || unchecked((byte)immediate.Value) == 0);

      // PALIGNR d,d,0 selects the unshifted low half of d:d, i.e. d itself, and does not set flags.
      case "PALIGNR":
        return operands.Count == 3
          && SameVector(operands[0], operands[1], xmmOnly: false)
          && IsImmediateZero(operands[2]);

      default:
        return false;
    }
  }

  private static bool IsGp(Reg register) => register.IsByte() || register.IsWord() || register.IsDword();
}
