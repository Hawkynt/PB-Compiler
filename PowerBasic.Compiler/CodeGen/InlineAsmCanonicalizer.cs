using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Removes register-only inline-assembly instructions whose architectural result is provably identical
/// to doing nothing, before any target policy has been resolved. Only baseline forms belong here:
/// anything target-dependent is left to the post-policy recognizer, because optimization must never
/// hide an ISA-policy or target-legality diagnostic.
/// </summary>
public static class InlineAsmCanonicalizer {
  /// <summary>True when <paramref name="line"/> is a baseline side-effect-free register identity.</summary>
  public static bool IsRedundant(string line) {
    if (!TryInstruction(line, out var mnemonic, out var operands))
      return false;
    return mnemonic == "MOV" && operands.Length == 2 && SameBaselineGp(operands[0], operands[1]);
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
}
