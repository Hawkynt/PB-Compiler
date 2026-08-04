using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The transcendental intrinsics are INSTRUCTIONS on this target, not runtime routines: the x87 has
/// FSQRT, FSIN, FCOS, FPTAN, FPATAN and FYL2X. The IR spells them as calls because that is what the C
/// and LLVM back ends want, so the selector recognises the names and writes the sequences back out.
///
/// Each is checked against the direct emitter, which writes the same sequences inline - so a wrong
/// transcription (a missing FXCH, the wrong constant before FYL2X, a forgotten FSTP after FPTAN) shows
/// up as a different number rather than as nothing at all.
/// </summary>
[TestFixture]
public sealed class BackendMathIntrinsicTests {

  private static string Run(string source, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  [TestCase("SQR")]
  [TestCase("SIN")]
  [TestCase("COS")]
  [TestCase("TAN")]
  [TestCase("ATN")]
  [TestCase("LOG")]
  [TestCase("EXP")]
  public void Intrinsic_GivenARangeOfArguments_ThenTheRoutedPathAgreesWithTheDirectOne(string name) {
    // a loop, so the argument is not a constant the optimizer could fold away on either side
    var source = $"""
      DIM d AS DOUBLE
      DIM i AS INTEGER
      FOR i = 1 TO 6
        d = {name}(i / 2)
        PRINT d
      NEXT i
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), name);
  }

  /// <summary>
  /// TAN is the one whose sequence has a step that is easy to leave out: FPTAN pushes a 1.0 above its
  /// answer, and the FSTP that discards it is not optional. Without it the stack drifts and a LATER
  /// value comes back wrong, which is why this prints several.
  /// </summary>
  [Test]
  public void Tan_GivenSeveralInARow_ThenTheX87StackDoesNotDrift() {
    const string source = """
      DIM i AS INTEGER
      FOR i = 1 TO 8
        PRINT TAN(i / 4); SIN(i / 4)
      NEXT i
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)));
  }
}
