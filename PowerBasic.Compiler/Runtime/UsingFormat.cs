namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// The <c>PRINT USING</c> / <c>USING$</c> format string, read once and shared by both code
/// generators.
///
/// <para>
/// A format is a sequence of literal runs and numeric FIELDS. A field is a run of <c>#</c> digit
/// positions, optionally carrying commas inside the run (thousands grouping) and optionally
/// followed by <c>.</c> and a second run of <c>#</c> for the fraction. Everything else - text,
/// <c>$</c>, <c>*</c>, <c>+</c>, <c>&amp;</c>, <c>^</c> - is literal and prints verbatim.
/// </para>
///
/// <para>
/// This lives here, beside the runtime it describes, because it is a CONTRACT rather than a
/// convenience: the direct emitter and the IR lowering both parse the format at compile time and
/// both hand <see cref="Spec"/> to the same hand-written routine (<c>rt_usefmt</c>), which reads
/// the width from <c>CH</c> and the decimals and grouping flag from <c>CL</c>. Two copies of this
/// reading would agree with each other for exactly as long as nobody touched one of them, and the
/// differential harness compares the two emitters against each other - so a shared misreading
/// would pass the comparison and still be wrong. One reading is one thing to be right about.
/// </para>
///
/// <para>
/// <b>What is deliberately NOT modelled.</b> Genuine PowerBASIC gives <c>$$</c> a floating currency
/// sign, <c>**</c> asterisk fill, <c>+</c>/<c>-</c> sign placement, <c>^^^^</c> exponential form and
/// <c>\ \</c> / <c>&amp;</c> string fields. None of them are here, because none of them are in the
/// DOS runtime either: <c>rt_usefmt</c> renders a right-aligned fixed-point number and nothing else.
/// Recognising them here would produce a field the runtime cannot fill, which is worse than printing
/// them as the literal characters they currently are.
/// </para>
/// </summary>
internal static class UsingFormat {

  /// <summary>
  /// One numeric field: its total printed width, its fraction digits, and whether the digit run
  /// carried commas and so asks for thousands grouping.
  /// </summary>
  internal readonly record struct Field(int Width, int Decimals, bool Group) {

    /// <summary>
    /// The field packed the way <c>rt_usefmt</c> reads it: the width in the high byte, the decimal
    /// count in the low seven bits of the low byte, and the grouping flag in bit 7 of that byte.
    /// </summary>
    internal int Spec => (this.Width << 8) | this.Decimals | (this.Group ? 0x80 : 0);
  }

  /// <summary>One piece of a format: literal text to print verbatim, or a numeric field to fill.</summary>
  internal readonly record struct Segment(string? Literal, Field? Field);

  /// <summary>Splits <paramref name="format"/> into its literal runs and numeric fields, in order.</summary>
  internal static List<Segment> Parse(string format) {
    var segments = new List<Segment>();
    var literal = "";
    for (var i = 0; i < format.Length;) {
      if (format[i] != '#') {
        literal += format[i++];
        continue;
      }
      if (literal.Length > 0) {
        segments.Add(new(literal, null));
        literal = "";
      }
      var digits = 0;
      var commas = 0;
      for (;;) {
        if (i < format.Length && format[i] == '#') {
          ++digits;
          ++i;
          continue;
        }
        // a comma inside the digit run requests thousands grouping
        if (i + 1 < format.Length && format[i] == ',' && format[i + 1] == '#') {
          ++commas;
          ++i;
          continue;
        }
        break;
      }
      var decimals = 0;
      if (i < format.Length && format[i] == '.') {
        ++i;
        while (i < format.Length && format[i] == '#') {
          ++decimals;
          ++i;
        }
      }
      var width = digits + commas + (decimals > 0 ? decimals + 1 : 0);
      segments.Add(new(null, new Field(width, decimals, commas > 0)));
    }
    if (literal.Length > 0)
      segments.Add(new(literal, null));
    return segments;
  }
}
