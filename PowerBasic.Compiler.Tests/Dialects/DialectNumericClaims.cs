using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// D6 - the numeric typing each dialect actually has, as a table of claims.
///
/// Typing is where the dialects quietly differ and where a wrong answer is invisible: a program that
/// stores a WORD where the original stored an INTEGER compiles, runs, and gives a different number
/// only at the boundary. So each claim names a spelling and the type it must bind to, gated by the
/// dialects that have it - and a dialect that does not have the spelling at all must REJECT it rather
/// than bind it to something plausible.
///
/// The interpreters are the sharpest case. BASICA and GW-BASIC store SINGLE in Microsoft Binary
/// Format, so <c>x!</c> there is not the same type as <c>x!</c> in QuickBASIC 4 even though the source
/// is identical.
/// </summary>
internal static class DialectNumericClaims {

  /// <param name="Id">Stable name, used in the failure message and the README note.</param>
  /// <param name="Declaration">How the variable is introduced; empty for a bare suffixed name.</param>
  /// <param name="Expression">The expression whose bound type is checked.</param>
  /// <param name="Expected">A predicate on the bound type, so a claim can be about a family of types.</param>
  /// <param name="Describe">What <paramref name="Expected"/> means, for the failure message.</param>
  /// <param name="Applies">Which dialects make this claim; the rest are expected to reject it.</param>
  internal sealed record Claim(
    string Id,
    string Declaration,
    string Expression,
    Func<PbType, bool> Expected,
    string Describe,
    Func<Dialect, bool> Applies);

  private static bool Scalar(PbType t, ScalarKind kind) => t is ScalarType s && s.Kind == kind;

  private static bool Everywhere(Dialect d) => true;

  private static bool BorlandOnly(Dialect d) => d.Family() == DialectFamily.Borland;

  /// <summary>
  /// The unsigned types and QUAD arrived in PowerBASIC 3.0, not with Turbo Basic. Claiming them for
  /// the whole Borland lineage made the probe report four failures against dialects that are right -
  /// the battery's first finding was about the battery.
  /// </summary>
  private static bool BorlandFromPb30(Dialect d) => d.Family() == DialectFamily.Borland && d >= Dialect.Pb30;

  internal static readonly Claim[] All = [
    // The default. Every BASIC in both lineages types a bare, unsuffixed name SINGLE - which is why
    // float demotion (O0012) exists at all: DOS-era counters are floats by accident.
    new("default.bare", "", "bareName",
      t => Scalar(t, ScalarKind.Single) || t is MbfType, "SINGLE (MBF where the dialect stores it that way)", Everywhere),

    // The four classic suffixes, shared by both lineages since the beginning.
    new("suffix.integer", "", "n%", t => Scalar(t, ScalarKind.Integer), "INTEGER (2 bytes, signed)", Everywhere),
    new("suffix.long", "", "n&", t => Scalar(t, ScalarKind.Long), "LONG (4 bytes, signed)", Everywhere),
    new("suffix.single", "", "n!",
      t => Scalar(t, ScalarKind.Single) || t is MbfType, "SINGLE", Everywhere),
    new("suffix.double", "", "n#", t => Scalar(t, ScalarKind.Double) || t is MbfType, "DOUBLE", Everywhere),

    // Bob Zale's additions. The Microsoft lineage never had them, so those dialects must refuse the
    // spelling rather than bind it to something close.
    new("suffix.byte", "", "n?", t => Scalar(t, ScalarKind.Byte), "BYTE (1 byte, unsigned)", BorlandFromPb30),
    new("suffix.word", "", "n??", t => Scalar(t, ScalarKind.Word), "WORD (2 bytes, unsigned)", BorlandFromPb30),
    new("suffix.dword", "", "n???", t => Scalar(t, ScalarKind.Dword), "DWORD (4 bytes, unsigned)", BorlandFromPb30),
    new("suffix.quad", "", "n&&", t => Scalar(t, ScalarKind.Quad), "QUAD (8 bytes, signed)", BorlandFromPb30),
    new("suffix.ext", "", "n##", t => Scalar(t, ScalarKind.Ext), "EXT (10 bytes)", BorlandOnly),

    // AS-declared names, which must agree with the suffix spelling of the same type.
    new("as.integer", "DIM v AS INTEGER", "v", t => Scalar(t, ScalarKind.Integer), "INTEGER", Everywhere),
    new("as.long", "DIM v AS LONG", "v", t => Scalar(t, ScalarKind.Long), "LONG", Everywhere),
    new("as.single", "DIM v AS SINGLE", "v",
      t => Scalar(t, ScalarKind.Single) || t is MbfType, "SINGLE", Everywhere),
    new("as.double", "DIM v AS DOUBLE", "v", t => Scalar(t, ScalarKind.Double) || t is MbfType, "DOUBLE", Everywhere),

    // Division. `/` is floating in every BASIC even between two integers - the classic surprise - and
    // `\` is the integer one. Getting these the wrong way round is a whole class of wrong answers.
    new("divide.float", "", "3 / 2", t => t is MbfType || t is ScalarType { IsFloat: true },
      "a floating type, even between two integer literals", Everywhere),
    new("divide.integer", "", "3 \\ 2", t => t is ScalarType { IsFloat: false }, "an integral type", Everywhere),

    // Literal typing: a bare integer literal small enough for INTEGER takes it; one that is not
    // widens rather than wrapping.
    new("literal.small", "", "42", t => t is ScalarType { IsFloat: false, Size: <= 2 }, "INTEGER-width", Everywhere),
    new("literal.wide", "", "100000", t => t is ScalarType { IsFloat: false, Size: >= 4 } or ScalarType { IsFloat: true } or MbfType,
      "wider than INTEGER - it must not wrap", Everywhere),
  ];

  /// <summary>Whether a dialect stores SINGLE in Microsoft Binary Format rather than IEEE.</summary>
  internal static bool StoresMbf(Dialect dialect) => dialect.IsGwBasica();
}
