namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// Synthetic definitions for the handful of <em>C-startup-provided</em> symbols a CRT
/// formatting routine references but never executes on the integer code path
/// (docs/LINKER.md "C runtime"). A genuine C program runs <c>crt0</c> at entry, which
/// initialises these and then calls <c>main</c>; our program's entry is the BASIC main,
/// so <c>crt0</c> never runs. For a CRT function we link <em>without</em> the startup
/// (e.g. <c>sprintf</c> formatting an integer into a buffer), the formatter still carries
/// a <em>relocation</em> to such a startup symbol even though the path that would use it is
/// unreachable. Linking fails on the unresolved relocation unless the symbol is defined, so
/// we contribute a safe, never-taken stub for the exact symbols this scoped path needs - and
/// nothing else (an unknown CRT external still surfaces as an honest <c>unresolved symbol</c>
/// diagnostic). The provider is consulted lazily by the <see cref="Linker"/>, only after the
/// real units and libraries have had their chance, so it never shadows a genuine definition.
///
/// Currently provided (Borland / Turbo C small-model <c>sprintf</c> integer path):
/// <list type="bullet">
/// <item><c>__RealCvtVector</c> - the float-conversion jump vector. <c>_REALCVT</c> is a
/// <c>jmp word ptr [__RealCvtVector]</c> trampoline reached only for <c>%f/%e/%g</c>; the
/// real vector is patched in by <c>crt0</c> to the float formatter. Integer formats never
/// jump through it, so we point it at a bare <c>ret</c>: the relocation resolves and, were it
/// ever reached with no FP in play, it returns cleanly rather than wild-jumping.</item>
/// </list>
/// </summary>
public static class CrtSupport {

  /// <summary>Startup-provided symbols this scoped CRT path references but never executes.</summary>
  private static readonly HashSet<string> _provided = new(StringComparer.Ordinal) { "__RealCvtVector" };

  /// <summary>True when <see cref="Build"/> can satisfy <paramref name="symbol"/>.</summary>
  public static bool Provides(string symbol) => _provided.Contains(symbol);

  /// <summary>
  /// Builds the synthetic stub unit (a foreign unit so its symbols stay case-sensitive). It
  /// holds one <c>ret</c> (<c>cvt_stub</c>) followed by the <c>__RealCvtVector</c> word - placed
  /// in the code segment so CS=DS reads it the same - initialised to <c>cvt_stub</c>'s offset
  /// via a near-code fixup. The vector is exported as a code-relative symbol so the linker's
  /// offset resolution applies unchanged; <c>_REALCVT</c>'s indirect jump lands on the
  /// <c>ret</c>.
  /// </summary>
  public static PbuFile Build() {
    // byte 0: cvt_stub = RET (0xC3); bytes 1..2: the __RealCvtVector word (cvt_stub's offset).
    var code = new byte[] { 0xC3, 0x00, 0x00 };
    var unit = new PbuFile { Name = "crtsupport", Code = code, Foreign = true };
    // export the vector word at code offset 1 (the trampoline reads the word *at* this address)
    unit.Exports.Add(new("__RealCvtVector", PbuExportKind.Sub, 0u, 1u));
    // patch that word to cvt_stub's final near offset (it currently holds 0 = stub's intra-unit
    // offset; NearCode adds the unit's code base, yielding the final offset).
    unit.Fixups.Add(new(1u, PbuFixupKind.NearCode, 0));
    return unit;
  }
}
