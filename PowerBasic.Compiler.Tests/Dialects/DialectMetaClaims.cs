using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// D9 - the metastatements, and whether they actually change the executable.
///
/// A metastatement that parses and is then ignored is worse than one that is rejected: the source
/// says "target an 80386" or "check for overflow" and the program does neither, silently. So each
/// claim compiles the SAME program twice, differing only in the directive, and requires the two
/// images to differ. What the difference is does not matter here - that there is one does.
///
/// The pairs are chosen so the difference is a consequence rather than a coincidence. `$CPU 80386`
/// against `$CPU 8086` gives the optimizer a wider instruction set for the same arithmetic;
/// `$ERROR OVERFLOW ON` adds a check around it; `$STACK` and `$STRING` size areas the image header
/// declares. A program with no arithmetic and no strings would compile identically under all of them
/// and prove nothing, which is why each body exercises what its directive governs.
/// </summary>
internal static class DialectMetaClaims {

  /// <summary>
  /// What the claim asserts about the two compilations.
  /// </summary>
  internal enum Kind {
    /// <summary>Both compile, and the images must differ - the directive changed the code.</summary>
    ImagesDiffer,

    /// <summary>The body compiles under <c>Against</c> and must be REFUSED under <c>Directive</c>.</summary>
    RefusedUnderDirective,
  }

  /// <param name="Id">Stable name for the claim.</param>
  /// <param name="Directive">The metastatement under test, as it appears at the head of the program.</param>
  /// <param name="Against">The directive it is compared against - the other setting, not its absence.</param>
  /// <param name="Body">Source that exercises what the directive governs.</param>
  /// <param name="Why">What the directive is supposed to change.</param>
  internal sealed record Claim(string Id, string Directive, string Against, string Body, string Why,
    Kind Kind = Kind.ImagesDiffer, Func<Dialect, bool>? Applies = null);

  private const string _arithmetic = """
    DIM a AS LONG
    DIM b AS LONG
    DIM c AS LONG
    a = 100000
    b = 7
    c = a * b + a \ b
    PRINT c
    END
    """;

  private const string _strings = """
    DIM s AS STRING
    DIM t AS STRING
    s = "hello"
    t = s + s + s
    PRINT t
    END
    """;

  private const string _pushImmediate = """
    DIM n AS INTEGER
    ! PUSH 5
    ! POP AX
    ! MOV n, AX
    PRINT n
    END
    """;

  private const string _shiftImmediate = """
    DIM n AS INTEGER
    n = 3
    ! MOV AX, n
    ! SHL AX, 3
    ! MOV n, AX
    PRINT n
    END
    """;

  internal static readonly Claim[] All = [
    // 8086 against 80286, not 80386: the 386 tier is gated to the later PowerBASIC dialects, so a
    // claim written against it measures the gate rather than the directive in the four oldest.
    new("cpu.tier", "$CPU 80286", "$CPU 8086", _arithmetic,
      "a wider instruction set for the same arithmetic - the 286's shift-by-immediate and PUSH imm"),
    new("optimize.speed", "$OPTIMIZE SPEED", "$OPTIMIZE SIZE", _arithmetic,
      "the optimizer's objective, which changes which transformations it takes"),
    new("error.overflow", "$ERROR OVERFLOW ON", "$ERROR OVERFLOW OFF", _arithmetic,
      "a trap around arithmetic that can overflow"),
    new("stack.size", "$STACK 4096", "$STACK 2048", _arithmetic,
      "the stack the image header reserves"),
    // $STRING takes a GRANULARITY - 1, 2, 4, 8, 16 - not a byte count. `$STRING 8192` simply does not
    // compile, which the probe reported as the directive failing rather than the claim being wrong.
    new("string.granularity", "$STRING 16", "$STRING 1", _strings,
      "the string heap's allocation granularity, and with it the maximum string length"),

    // Inline assembly is where the CPU tier stops being an optimizer hint and becomes a correctness
    // question. PUSH with an immediate operand does not exist on the 8086 - it arrived with the 286 -
    // so a program that writes one and targets an 8086 asks for an instruction the target cannot
    // execute. Accepting it produces an image that faults on the machine it named.
    new("asm.cpu.push-imm", "$CPU 8086", "$CPU 80286",
      _pushImmediate,
      "PUSH with an immediate is 80286 and later; on an 8086 target it must be refused",
      Kind.RefusedUnderDirective, d => d >= Dialect.Pb30 && d.Family() == DialectFamily.Borland),

    // The same for a shift by an immediate count other than 1, which the 8086 spells only through CL.
    new("asm.cpu.shift-imm", "$CPU 8086", "$CPU 80286",
      _shiftImmediate,
      "a shift by an immediate count above 1 is 80286 and later; the 8086 has only SHL r, CL",
      Kind.RefusedUnderDirective, d => d >= Dialect.Pb30 && d.Family() == DialectFamily.Borland),
  ];

  /// <summary>
  /// Whether the dialect has compiler metastatements at all.
  ///
  /// The Microsoft lineage does not: QuickBASIC's <c>REM $STATIC</c> and <c>REM $DYNAMIC</c> are
  /// array-storage directives in comment form, not compiler settings, and there is no <c>$CPU</c> or
  /// <c>$ERROR</c> to test. Reporting that as a gap would be inventing one.
  /// </summary>
  internal static bool Applies(Dialect dialect) => dialect.Family() == DialectFamily.Borland;
}
