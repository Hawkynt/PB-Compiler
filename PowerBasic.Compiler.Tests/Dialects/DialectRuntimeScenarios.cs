using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Dialects;

/// <summary>
/// D8 - what the runtime functions actually do, checked by running the produced executable.
///
/// The expected values here are taken from the LANGUAGE DEFINITION, not from what this compiler
/// currently prints. That distinction is the whole point: an expectation captured from our own output
/// passes by construction and would notice nothing. <c>LEN("abc")</c> is 3 in every BASIC ever
/// written, <c>MID$("abcde", 2, 3)</c> is "bcd", <c>INSTR</c> is 1-based and answers 0 when the needle
/// is absent - these are facts about the language that a wrong implementation contradicts.
///
/// Where a result is NOT fixed by the definition - the exact column widths PRINT uses, how many
/// significant digits a SINGLE shows - nothing is asserted here. Those are per-dialect rendering, and
/// pinning them from memory would be inventing an oracle. They belong to the runtime-differential
/// harness, which compares against the genuine binary rather than against a guess.
/// </summary>
internal static class DialectRuntimeScenarios {

  /// <param name="Id">Stable name for the scenario.</param>
  /// <param name="Body">The program, without line numbers - the probe adds them for the interpreters.</param>
  /// <param name="Expect">
  /// The exact text the program must print, lines joined with '|'. A NUMERIC result carries BASIC's
  /// sign column - PRINT puts a blank in front of a non-negative number - so the expectation for LEN
  /// is " 3" and not "3". Four scenarios here were written without it and reported the compiler
  /// wrong about a rule the compiler was following.
  /// </param>
  /// <param name="Why">The rule being checked, for the failure message.</param>
  /// <param name="Applies">Dialects that have the function; null means all of them.</param>
  internal sealed record Scenario(string Id, string Body, string Expect, string Why, Func<Dialect, bool>? Applies = null);

  private static bool Borland(Dialect d) => d.Family() == DialectFamily.Borland;

  internal static readonly Scenario[] All = [
    // --- string length and slicing: 1-based, and clamping rather than faulting ------------------
    new("len", "PRINT LEN(\"abc\")", " 3", "LEN counts characters"),
    new("len.empty", "PRINT LEN(\"\")", " 0", "the empty string has length zero, not one"),
    new("left", "PRINT LEFT$(\"abcde\", 2)", "ab", "LEFT$ takes from the start"),
    new("left.over", "PRINT LEFT$(\"ab\", 99)", "ab", "asking for more than there is yields all of it, not an error"),
    new("left.zero", "PRINT LEFT$(\"abc\", 0) + \"|\"", "|", "zero characters is the empty string"),
    new("right", "PRINT RIGHT$(\"abcde\", 2)", "de", "RIGHT$ takes from the end"),
    new("mid.three", "PRINT MID$(\"abcde\", 2, 3)", "bcd", "MID$ is 1-BASED: position 2 is the second character"),
    new("mid.past", "PRINT MID$(\"abc\", 9, 2) + \"|\"", "|", "a start past the end yields empty, not an error"),

    // --- searching: 1-based, zero for absent ------------------------------------------------------
    new("instr.found", "PRINT INSTR(\"hello\", \"ll\")", " 3", "INSTR answers a 1-based position"),
    new("instr.absent", "PRINT INSTR(\"hello\", \"z\")", " 0", "absent is 0 - the value that cannot be a position"),

    // --- case and trimming -----------------------------------------------------------------------
    new("ucase", "PRINT UCASE$(\"aBc\")", "ABC", "UCASE$ raises every letter"),
    new("lcase", "PRINT LCASE$(\"AbC\")", "abc", "LCASE$ lowers every letter"),
    new("ltrim", "PRINT LTRIM$(\"   x\") + \"|\"", "x|", "LTRIM$ removes LEADING blanks only"),
    new("rtrim", "PRINT RTRIM$(\"x   \") + \"|\"", "x|", "RTRIM$ removes TRAILING blanks only"),

    // --- character codes --------------------------------------------------------------------------
    new("chr", "PRINT CHR$(65)", "A", "CHR$ maps a code to its character"),
    new("asc", "PRINT ASC(\"A\")", " 65", "ASC maps a character to its code"),
    new("string.repeat", "PRINT STRING$(3, \"x\")", "xxx", "STRING$ repeats the FIRST character of its argument"),
    new("space", "PRINT SPACE$(3) + \"|\"", "   |", "SPACE$ yields exactly that many blanks"),

    // --- numbers ----------------------------------------------------------------------------------
    new("abs", "PRINT ABS(-9)", " 9", "ABS drops the sign"),
    new("sgn.neg", "PRINT SGN(-9)", "-1", "SGN answers -1, 0 or 1"),
    new("sgn.zero", "PRINT SGN(0)", " 0", "SGN of zero is zero, not one"),
    new("int.floor", "PRINT INT(2.7)", " 2", "INT FLOORS - toward minus infinity"),
    new("int.negative", "PRINT INT(-2.7)", "-3", "flooring -2.7 gives -3, which is what separates INT from FIX"),
    new("fix.truncate", "PRINT FIX(-2.7)", "-2", "FIX truncates TOWARD ZERO"),
    new("sqr", "PRINT SQR(16)", " 4", "SQR of a perfect square is exact"),
    new("mod", "PRINT 7 MOD 3", " 1", "MOD is the remainder"),
    new("intdiv", "PRINT 7 \\ 2", " 3", "backslash is integer division"),

    // --- an observable side effect other than stdout ------------------------------------------------
    new("file.roundtrip",
      "OPEN \"R.TXT\" FOR OUTPUT AS #1\nPRINT #1, \"written\"\nCLOSE #1\nOPEN \"R.TXT\" FOR INPUT AS #2\nLINE INPUT #2, a$\nCLOSE #2\nPRINT a$",
      "written",
      "what PRINT # writes is what LINE INPUT # reads back", Borland),
  ];
}
