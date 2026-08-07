using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// The per-dialect conformance battery, as data.
///
/// A dialect is not conformant because a pile of programs happens to compile under it. It is
/// conformant when a specific, enumerable set of claims holds — and the point of naming those claims
/// here, rather than leaving them implied by whichever tests happen to exist, is that the ones NOT yet
/// held are then visible too. Every dimension below is either measured by a probe or explicitly marked
/// as having none, and <c>tests/dialects/&lt;name&gt;/README.md</c> is generated from that measurement
/// rather than maintained by hand, so the checklist cannot drift away from the code.
/// </summary>
internal static class DialectBattery {

  /// <summary>How far along a dimension is for one dialect.</summary>
  internal enum State {
    /// <summary>A probe runs and every case in it passes.</summary>
    Held,

    /// <summary>A probe runs and some cases are known not to pass yet; <see cref="Measurement.Note"/> says how many.</summary>
    Partial,

    /// <summary>No probe exists yet. The claim is unverified, which is not the same as false.</summary>
    Unprobed,

    /// <summary>The dimension cannot apply here - an interpreter has no linker, a compiler has no immediate mode.</summary>
    NotApplicable,
  }

  internal sealed record Measurement(State State, int Covered, int Total, string Note = "");

  /// <param name="Id">Stable slug, used as the anchor in the generated README.</param>
  /// <param name="Title">One line, as it appears in the checklist.</param>
  /// <param name="Contract">What must hold for this dimension to be Held - written so a reader can judge the probe.</param>
  internal sealed record Dimension(string Id, string Title, string Contract);

  /// <summary>
  /// The twelve claims. The order is the order of the checklist and is deliberate: it runs from what
  /// the front end accepts, through what the middle end makes of it, to what the produced program
  /// actually does - so a dialect fails at the earliest stage that is wrong.
  /// </summary>
  internal static readonly Dimension[] Dimensions = [
    new("syntax", "Statement syntax and parameter combinations",
      "Every statement form the dialect provides is accepted, in each combination of its optional parameters."),
    new("lowering", "Lowers to the IR",
      "Every accepted form reaches the IR, or declines with a named reason rather than an internal exception."),
    new("dead-branch", "Syntax errors in unreachable branches are ignored, and warned about",
      "Source that no control flow can reach may be malformed without failing the compile, and says so."),
    new("live-branch", "Syntax errors on reachable flow fail",
      "The same malformed source, where control can reach it, is a diagnostic and not a miscompile."),
    new("foreign", "Syntax belonging to another dialect is rejected",
      "A form this dialect never had is refused, with a controlled diagnostic naming the requirement."),
    new("numeric-types", "Numeric typing follows the dialect",
      "Default types, suffix widths, literal typing and division result types are the dialect's own."),
    new("runtime-selection", "Runtime implementations follow the dialect",
      "Where two dialects differ in a runtime routine - rounding, float format - the right one is called."),
    new("runtime-behaviour", "Every runtime function's observable output",
      "Each runtime entry is exercised in all its variations and checked on stdout, files, exit code. "
      + "GRAPHICS IS THE OPEN PART: SCREEN, PSET, LINE, CIRCLE, PAINT, GET/PUT and PCOPY need checking "
      + "PIXEL BY PIXEL against the mode-13h framebuffer the emulator already keeps at 0xA0000, and "
      + "offscreen pages need the same treatment - a GUI statement that draws almost the right shape "
      + "passes every stdout-based test there is."),
    new("metastatements", "Metastatements and their effect on the produced executable",
      "$CPU, $FLOAT and the rest change the image in the documented way, per target and FPU mode."),
    new("quirks", "Documented quirks and bugs are reproduced",
      "Each quirk recorded for the dialect is reproduced, not merely tolerated."),
    new("bit-exact", "Numeric operations are bit-compatible",
      "Arithmetic - floating point above all - produces the same bits as the genuine implementation."),
    new("readme", "The battery documents itself",
      "tests/dialects/<name>/README.md is generated from these measurements and is current."),
  ];

  /// <summary>Every dialect the compiler claims to accept.</summary>
  internal static readonly Dialect[] All = StatementSurface.AllDialects;

  /// <summary>The battery directory for a dialect, relative to the repository root.</summary>
  internal static string Directory(Dialect dialect) => $"tests/dialects/{dialect.CanonicalName()}";
}
