using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Removes register-only inline-assembly instructions whose architectural result is provably identical
/// to doing nothing. Target-dependent identities are deliberately separated from the pre-policy pass:
/// optimization must never hide an ISA-policy or target-legality diagnostic.
/// </summary>
public static class InlineAsmCanonicalizer {
  /// <summary>True when <paramref name="line"/> is a baseline side-effect-free register identity.</summary>
  public static bool IsRedundant(string line) {
    if (!TryInstruction(line, out var mnemonic, out var operands))
      return false;
    return mnemonic == "MOV" && operands.Length == 2 && SameBaselineGp(operands[0], operands[1]);
  }

  /// <summary>
  /// True for target-dependent register identities that may only be erased after ISA policy has been
  /// resolved. None of these instructions modify integer flags; VEX/EVEX forms are excluded because
  /// upper-lane clearing can be architecturally observable.
  /// </summary>
  public static bool IsPolicyValidatedRedundant(string line) {
    if (!TryInstruction(line, out var mnemonic, out var operands) || mnemonic.StartsWith('V'))
      return false;

    if (mnemonic is "MOVDQA" or "MOVDQU" or "PAND" or "POR"
        or "PMINSB" or "PMINSD" or "PMINUW" or "PMINUD"
        or "PMAXSB" or "PMAXSD" or "PMAXUW" or "PMAXUD")
      return operands.Length == 2 && SameVectorRegister(operands[0], operands[1]);

    if (mnemonic == "PBLENDW")
      return operands.Length == 3 && SameVectorRegister(operands[0], operands[1]);

    return false;
  }

  private static bool TryInstruction(string line, out string mnemonic, out string[] operands) {
    var comment = line.IndexOf(';');
    if (comment >= 0)
      line = line[..comment];
    line = line.Trim();
    if (line.Length == 0 || line.Contains(':') || line.Contains('\n') || line.Contains('\r')) {
      mnemonic = string.Empty;
      operands = [];
      return false;
    }

    var space = line.IndexOfAny([' ', '\t']);
    if (space < 0) {
      mnemonic = line.ToUpperInvariant();
      operands = [];
      return true;
    }

    mnemonic = line[..space].ToUpperInvariant();
    operands = line[(space + 1)..].Split(',').Select(o => o.Trim()).ToArray();
    return true;
  }

  private static bool SameBaselineGp(string first, string second) =>
    Enum.TryParse<Reg>(first, true, out var a) && Enum.TryParse<Reg>(second, true, out var b) && a == b
    && (a.IsByte() || a.IsWord());

  private static bool SameVectorRegister(string first, string second) =>
    Enum.TryParse<Reg>(first, true, out var a) && Enum.TryParse<Reg>(second, true, out var b) && a == b
    && (a.IsMmx() || a.IsXmm());
}
