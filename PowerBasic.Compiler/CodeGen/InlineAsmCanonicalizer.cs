using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// Removes baseline register-only inline-assembly instructions whose architectural result is provably
/// identical to doing nothing. This pre-policy pass deliberately excludes target-dependent instructions:
/// optimization must never hide an ISA-policy or target-legality diagnostic.
/// </summary>
public static class InlineAsmCanonicalizer {
  /// <summary>True when <paramref name="line"/> is a baseline side-effect-free register identity.</summary>
  public static bool IsRedundant(string line) {
    var comment = line.IndexOf(';');
    if (comment >= 0)
      line = line[..comment];
    line = line.Trim();
    if (line.Length == 0 || line.Contains(':') || line.Contains('\n') || line.Contains('\r'))
      return false;

    var space = line.IndexOfAny([' ', '\t']);
    if (space < 0 || !line[..space].Equals("MOV", StringComparison.OrdinalIgnoreCase))
      return false;

    var operands = line[(space + 1)..].Split(',').Select(o => o.Trim()).ToArray();
    return operands.Length == 2 && SameBaselineGp(operands[0], operands[1]);
  }

  private static bool SameBaselineGp(string first, string second) =>
    Enum.TryParse<Reg>(first, true, out var a) && Enum.TryParse<Reg>(second, true, out var b) && a == b
    && (a.IsByte() || a.IsWord());
}
