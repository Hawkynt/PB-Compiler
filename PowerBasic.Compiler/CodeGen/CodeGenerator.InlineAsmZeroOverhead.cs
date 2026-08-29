using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {
  /// <summary>
  /// Returns true for inline-assembly instructions that are architecturally indistinguishable from
  /// doing nothing: no register/memory result changes, no flags change, and no memory operand can
  /// fault. This is intentionally much narrower than ordinary algebraic simplification. In
  /// particular, arithmetic identities such as ADD r,0 are not listed because they define EFLAGS.
  /// </summary>
  private static bool IsZeroOverheadInlineAsmIdentity(InlineInstruction instruction) {
    if (instruction.RepPrefix is not null)
      return false;

    var operands = SplitInlineOperands(instruction.Operands);
    switch (instruction.Mnemonic) {
      case "MOV":
      case "XCHG":
      case "MOVQ":
      case "MOVDQA":
      case "MOVDQU":
        return operands.Length == 2 && SameRegister(operands[0], operands[1]);

      // A zero-count scalar shift/rotate is architecturally a no-op, including EFLAGS. Restrict the
      // destination to a register so elision cannot remove a faulting memory access.
      case "SHL":
      case "SAL":
      case "SHR":
      case "SAR":
      case "ROL":
      case "ROR":
      case "RCL":
      case "RCR":
        return operands.Length == 2 && IsRegister(operands[0]) && IsZeroImmediate(operands[1]);

      // PBLENDW never changes flags. With the same register as both inputs it is an identity for any
      // mask. With a zero mask it is an identity as long as the ignored source is also a register;
      // a memory source is deliberately retained because the architectural read can fault.
      case "PBLENDW":
        return operands.Length == 3
          && IsRegister(operands[0])
          && IsRegister(operands[1])
          && (SameRegister(operands[0], operands[1]) || IsZeroImmediate(operands[2]));

      // PALIGNR d,d,0 selects the unshifted low half of d:d, i.e. d itself, and does not set flags.
      case "PALIGNR":
        return operands.Length == 3
          && SameRegister(operands[0], operands[1])
          && IsZeroImmediate(operands[2]);

      default:
        return false;
    }
  }

  private static string[] SplitInlineOperands(string text)
    => text.Length == 0
      ? []
      : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

  private static bool SameRegister(string left, string right)
    => Enum.TryParse<Reg>(left, true, out var a)
       && Enum.TryParse<Reg>(right, true, out var b)
       && a == b;

  private static bool IsRegister(string text) => Enum.TryParse<Reg>(text, true, out _);

  private static bool IsZeroImmediate(string text) {
    text = text.Trim();
    if (text.StartsWith('+'))
      text = text[1..];
    return text.Length > 0 && text.All(c => c == '0');
  }
}
