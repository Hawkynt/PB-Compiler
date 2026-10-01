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
/// Each is checked against the values the functions have - so a wrong transcription (a missing FXCH,
/// the wrong constant before FYL2X, a forgotten FSTP after FPTAN) shows up as a different number
/// rather than as nothing at all.
/// </summary>
[TestFixture]
public sealed class BackendMathIntrinsicTests {

  private static string Run(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true};
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  // f(0.5) .. f(3); each agrees with the function to fourteen significant digits, and the fifteenth
  // is the runtime formatter's rounding of the eighty-bit result
  [TestCase("SQR", ".707106781186548 | 1 | 1.22474487139159 | 1.4142135623731 | 1.58113883008419 | 1.73205080756888")]
  [TestCase("SIN", ".479425538604203 | .841470984807896 | .997494986604054 | .909297426825682 | .598472144103956 | .141120008059867")]
  [TestCase("COS", ".877582561890373 | .54030230586814 | .0707372016677028 |-.416146836547142 |-.801143615546934 |-.989992496600446")]
  [TestCase("TAN", ".546302489843791 | 1.5574077246549 | 14.1014199471717 |-2.18503986326152 |-.74702229723866 |-.142546543074278")]
  [TestCase("ATN", ".463647609000806 | .785398163397448 | .982793723247329 | 1.10714871779409 | 1.19028994968253 | 1.24904577239825")]
  [TestCase("LOG", "-.693147180559945 | 0 | .405465108108164 | .693147180559945 | .916290731874155 | 1.09861228866811")]
  [TestCase("EXP", "1.64872127070013 | 2.71828182845904 | 4.48168907033806 | 7.38905609893065 | 12.1824939607035 | 20.0855369231877")]
  public void Intrinsic_GivenARangeOfArguments_ThenPrintsTheFunctionsValues(string name, string expected) {
    // a loop, so the argument is not a constant the optimizer could fold away on either side
    var source = $"""
      DIM d AS DOUBLE
      DIM i AS INTEGER
      FOR i = 1 TO 6
        d = {name}(i / 2)
        PRINT d
      NEXT i
      """;

    Assert.That(Run(source), Is.EqualTo(expected), name);
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

    // TAN(i/4) and SIN(i/4) for i = 1 .. 8; TAN crosses its pole between i = 6 and i = 7
    Assert.That(Run(source), Is.EqualTo(
      ".255341921221036  .247403959254523 | .546302489843791  .479425538604203 | "
      + ".931596459944072  .681638760023334 | 1.5574077246549  .841470984807896 | "
      + "3.00956967386283  .948984619355586 | 14.1014199471717  .997494986604054 |"
      + "-5.52037992250933  .983985946873937 |-2.18503986326152  .909297426825682"));
  }
}
