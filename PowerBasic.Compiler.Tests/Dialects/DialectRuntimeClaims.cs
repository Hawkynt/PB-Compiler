using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// D7 - which runtime implementation a dialect selects, where the dialects genuinely disagree.
///
/// Two programs can lower to the same shape and still be wrong for one of them, because the routine
/// underneath differs. The BASCOM lineage - QuickBASIC 1.0 through 3.0, and the interpreters before
/// them - rounds a real on its way into an integer HALF AWAY FROM ZERO, so <c>CINT(2.5)</c> is 3 and
/// <c>CINT(-2.5)</c> is -3. QuickBASIC 4 onward and the whole Borland lineage take the x87's own
/// round-half-to-even, so <c>CINT(2.5)</c> is 2. Both are one <c>fptosi.round</c> in the IR and only
/// the callee tells them apart.
///
/// The same is true of float STORAGE: BASICA and GW-BASIC keep SINGLE in Microsoft Binary Format, so
/// a load and a store carry an <c>MbfToFP</c> / <c>FPToMbf</c> pair that no other dialect has.
///
/// Each claim names a marker that must appear in the lowered IR, and - just as importantly - the
/// dialects where it must NOT. A marker that shows up everywhere proves nothing.
/// </summary>
internal static class DialectRuntimeClaims {

  /// <param name="Id">Stable name for the claim.</param>
  /// <param name="Body">A program body whose lowering should contain the marker.</param>
  /// <param name="Marker">Text that must appear in the printed IR exactly where <paramref name="Applies"/> says.</param>
  /// <param name="Why">What the marker means, for the failure message and the README.</param>
  /// <param name="Applies">The dialects whose runtime uses this implementation.</param>
  internal sealed record Claim(
    string Id,
    string Body,
    string Marker,
    string Why,
    Func<Dialect, bool> Applies);

  internal static readonly Claim[] All = [
    // Rounding on the way into an integer. This is the single most consequential runtime difference
    // between the two Microsoft eras and it is invisible in the source.
    new("round.half-away",
      "DIM n AS INTEGER\nDIM d AS DOUBLE\nd = 2.5\nn = d\nPRINT n\nEND",
      "rt_round_half_away",
      "the BASCOM lineage rounds half AWAY from zero; QB 4+ and Borland take the FPU's half-to-even",
      d => d.IsBascomRuntime()),

    // Microsoft Binary Format storage. Only the two interpreters have it, and the conversions are the
    // proof that the format is honoured rather than merely recorded in a type.
    new("float.mbf",
      "DIM s AS SINGLE\ns = 1.5\nPRINT s\nEND",
      "mbf",
      "BASICA and GW-BASIC store SINGLE in Microsoft Binary Format, so loads and stores convert",
      d => d.IsGwBasica()),
  ];
}
