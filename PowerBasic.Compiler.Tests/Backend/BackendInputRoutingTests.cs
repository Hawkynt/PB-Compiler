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
