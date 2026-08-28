using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Numeric and string <c>INPUT</c>, and narrowing to a BYTE, on the x86-16 back end.
///
/// <para>
/// The IR declares <c>rt_input_i16</c> and its neighbours because the same declarations also feed the
/// C back end, where such a function really exists. The DOS runtime had no such entry - the direct
/// emitter composes the answer at the call site, reading a token, VAL'ing it and rounding into the
/// target. It now composes it once, under those names, so both back ends see one shape and the ABI
/// table stays a plain list of claims rather than a little language for sequences.
/// </para>
/// <para>
/// The tests read from a FILE rather than the console: the test CPU has no stdin, and the file forms
/// go through the very same routines with the file number in place of the console's zero.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendInputRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output;
  }

  private static IEnumerable<string> RoutedNames(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = true };
    cg.EmitExecutable();
    return cg.BackendRoutedNames.ToList();
  }

  /// <summary>Writes a file, reads it back through INPUT #, and prints what came out.</summary>
  private static readonly (string Name, string Source)[] _programs = [
    ("integers", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      PRINT #1, "42"
      PRINT #1, "-7"
      CLOSE #1
      DIM a AS INTEGER
      DIM b AS INTEGER
      OPEN "OUT.TXT" FOR INPUT AS #1
      INPUT #1, a
      INPUT #1, b
      CLOSE #1
      PRINT a; b
      END
      """),
    ("longs", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      PRINT #1, "2000000000"
      CLOSE #1
      DIM v AS LONG
      OPEN "OUT.TXT" FOR INPUT AS #1
      INPUT #1, v
      CLOSE #1
      PRINT v
      END
      """),
    ("floats", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      PRINT #1, "3.5"
      PRINT #1, "1.25"
      CLOSE #1
      DIM s AS SINGLE
      DIM d AS DOUBLE
      OPEN "OUT.TXT" FOR INPUT AS #1
      INPUT #1, s
      INPUT #1, d
      CLOSE #1
      PRINT s; d
      END
      """),
    ("strings", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      WRITE #1, "hello", "world"
      CLOSE #1
      DIM a AS STRING
      DIM b AS STRING
      OPEN "OUT.TXT" FOR INPUT AS #1
      INPUT #1, a
      INPUT #1, b
      CLOSE #1
      PRINT a; "/"; b
      END
      """),
    // rounding: INPUT of a fractional number into an INTEGER goes through the same nearest-even
    // rounding an assignment does, which is what FISTP does with the default control word
    ("rounding into an integer", """
      OPEN "OUT.TXT" FOR OUTPUT AS #1
      PRINT #1, "2.5"
      PRINT #1, "3.5"
      PRINT #1, "-2.5"
      CLOSE #1
      DIM a AS INTEGER
      DIM b AS INTEGER
      DIM c AS INTEGER
      OPEN "OUT.TXT" FOR INPUT AS #1
      INPUT #1, a
      INPUT #1, b
      INPUT #1, c
      CLOSE #1
      PRINT a; b; c
      END
      """),
  ];

  [Test]
  public void Input_GivenEveryTargetType_ThenTheRoutedProgramMatchesTheDirectEmitter() {
    foreach (var (name, source) in _programs)
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// And the back end really took the body. Without this the comparison above would pass on every
  /// program the selector quietly declined, since both sides would then be the direct emitter.
  /// </summary>
  [Test]
  public void Input_GivenEveryTargetType_ThenTheBackEndOwnsTheBody() {
    foreach (var (name, source) in _programs)
      Assert.That(RoutedNames(source), Does.Contain("main"), $"program '{name}' was not routed");
  }

  /// <summary>
  /// The four widths the routed path could not read at all: their runtime entries were declared -
  /// the same declarations feed the C back end, where they exist - and the DOS runtime composed one
  /// only for <c>i16</c>, <c>i32</c> and the floats, so <c>INPUT #1, q&amp;&amp;</c> declined with
  /// "not in the runtime ABI table".
  ///
  /// <para>
  /// What each one had to be is decided by where the direct emitter's <c>Coerce</c> narrows the
  /// VAL'd number for that target type, and the four are not one shape: a BYTE and a WORD are both
  /// <c>ValueKind.Int16</c> there and share the 16-bit entry, a DWORD takes the UNSIGNED arm because
  /// a signed 32-bit <c>FISTP</c> answers 8000_0000h for 4000000000, and a QUAD stays on the x87
  /// because 64 bits of integer have no register pair on this target and a DOUBLE would drop eleven
  /// mantissa bits on the way.
  /// </para>
  ///
  /// <para>
  /// Every value here is past the signed range of its own width, which is the only place the choice
  /// of arm is observable at all.
  /// </para>
  /// </summary>
  private static readonly (string Name, string Source, string Expected)[] _unsignedAndQuadPrograms = [
    ("byte past the signed range", InputProgram("BYTE", "200"), "200"),
    ("byte at the top", InputProgram("BYTE", "255"), "255"),
    ("word past the signed range", InputProgram("WORD", "65535"), "65535"),
    ("word past a signed word by one", InputProgram("WORD", "32768"), "32768"),
    ("dword past the signed range", InputProgram("DWORD", "4000000000"), "4000000000"),
    ("dword at the top", InputProgram("DWORD", "4294967295"), "4294967295"),
    ("quad that a LONG cannot hold", InputProgram("QUAD", "8589934592"), "8589934592"),
    // 57 significant bits: more than a DOUBLE's 53, so a QUAD that went anywhere near one on the way
    // in comes back with the last digits wrong
    ("quad past a double's mantissa", InputProgram("QUAD", "76861433640456465"), null!),
  ];

  private static string InputProgram(string type, string literal) => $"""
    OPEN "OUT.TXT" FOR OUTPUT AS #1
    PRINT #1, "{literal}"
    CLOSE #1
    DIM v AS {type}
    OPEN "OUT.TXT" FOR INPUT AS #1
    INPUT #1, v
    CLOSE #1
    PRINT v
    END
    """;

  /// <summary>
  /// The defect the widths above exposed, and it was in the entry that had been there all along.
  /// <c>rt_inp_i16</c> narrowed with a 16-bit <c>FISTP</c> and <c>rt_inp_i32</c> with a 32-bit one -
  /// and <c>FISTP</c> does not wrap. Given a value its destination cannot hold it writes the
  /// INDEFINITE, so <c>INPUT #1, a%</c> on 40000 answered -32768 where PB wraps to -25536. Both now
  /// store through one size more and keep the low half, which is what the direct emitter's
  /// <c>Coerce</c> does and says it does.
  /// </summary>
  private static readonly (string Name, string Source, string Expected)[] _outOfRangePrograms = [
    ("integer above the signed range", InputProgram("INTEGER", "40000"), "-25536"),
    ("integer below it", InputProgram("INTEGER", "-40000"), "25536"),
    ("long above the signed range", InputProgram("LONG", "3000000000"), "-1294967296"),
  ];

  [Test]
  public void Input_GivenAByteWordDwordOrQuad_ThenTheRoutedProgramRoutesAndMatchesTheDirectEmitter() {
    AssertRoutedMatchesDirect(_unsignedAndQuadPrograms);
  }

  [Test]
  public void Input_GivenAValuePastTheTargetsSignedRange_ThenItWrapsAsTheDirectEmitterWraps() {
    AssertRoutedMatchesDirect(_outOfRangePrograms);
  }

  private static void AssertRoutedMatchesDirect((string Name, string Source, string Expected)[] programs) {
    foreach (var (name, source, expected) in programs)
      foreach (var optimize in new[] { false, true }) {
        var direct = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = false };
        var routed = new CodeGenerator(Bind(source)) { Optimize = optimize, UseExperimentalBackend = true };
        var directOutput = Cpu8086.Run(direct.EmitExecutable()).Output;
        var routedOutput = Cpu8086.Run(routed.EmitExecutable()).Output;

        Assert.That(routed.BackendRoutedNames, Does.Contain("main"),
          $"'{name}' was not routed (optimize={optimize}), so this compares the direct emitter with itself");
        Assert.That(routedOutput, Is.EqualTo(directOutput), $"'{name}' (optimize={optimize})");
        if (expected is not null)
          Assert.That(routedOutput.Trim(), Is.EqualTo(expected), $"'{name}' (optimize={optimize})");
      }
  }

  /// <summary>
  /// Narrowing to a BYTE, which is a change of VIEW rather than of content - the low half of the word
  /// already holding the value. A truncation that masked the wrong half, or that widened instead,
  /// would show up here as soon as the value exceeds a byte.
  /// </summary>
  [TestCase("300", "44")]
  [TestCase("255", "255")]
  [TestCase("256", "0")]
  [TestCase("-1", "255")]
  public void ByteNarrowing_GivenAValuePastAByte_ThenOnlyTheLowByteSurvives(string assigned, string expected) {
    var source = $"""
      DIM v AS INTEGER
      DIM b AS BYTE
      v = {assigned}
      b = v
      PRINT b
      END
      """;
    Assert.That(Run(source, routed: true).Trim(), Is.EqualTo(expected));
    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }
}
