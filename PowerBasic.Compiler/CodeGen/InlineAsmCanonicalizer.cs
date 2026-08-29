using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Removes register-only inline-assembly instructions whose architectural result is provably identical
/// to doing nothing. The recognizer is intentionally narrow: an invalid instruction, a memory operand,
/// an instruction that writes flags, or an instruction with VEX upper-lane clearing is never hidden.
/// </summary>
public static class InlineAsmCanonicalizer {
  /// <summary>True when <paramref name="line"/> is a side-effect-free register identity.</summary>
  public static bool IsRedundant(string line) {
    var comment = line.IndexOf(';');
    if (comment >= 0)
      line = line[..comment];
    line = line.Trim();
    if (line.Length == 0 || line.Contains(':') || line.Contains('\n') || line.Contains('\r'))
      return false;

    var space = line.IndexOfAny([' ', '\t']);
    if (space < 0)
      return false;
    var mnemonic = line[..space].ToUpperInvariant();
    var operands = line[(space + 1)..].Split(',').Select(o => o.Trim()).ToArray();

    if (mnemonic == "PBLENDW")
      return operands.Length == 3 && SameXmm(operands[0], operands[1]) && IsImmediate(operands[2]);

    if (operands.Length != 2)
      return false;

    if (mnemonic == "MOV")
      return SameGp(operands[0], operands[1]);

    if (mnemonic is "MOVDQA" or "MOVDQU" or "PAND" or "POR"
        or "PMINSB" or "PMAXSB" or "PMINUW" or "PMAXUW" or "PMINUD" or "PMAXUD")
      return SameXmm(operands[0], operands[1]);

    return false;
  }

  private static bool SameGp(string first, string second) =>
    Enum.TryParse<Reg>(first, true, out var a) && Enum.TryParse<Reg>(second, true, out var b) && a == b
    && (a.IsByte() || a.IsWord() || a.IsDword());

  private static bool SameXmm(string first, string second) =>
    Enum.TryParse<Reg>(first, true, out var a) && Enum.TryParse<Reg>(second, true, out var b) && a == b && a.IsXmm();

  private static bool IsImmediate(string text) {
    if (text.Length == 0)
      return false;
    var c = text[0];
    return char.IsAsciiDigit(c) || ((c == '-' || c == '+' || c == '&') && text.Length > 1);
  }
}
