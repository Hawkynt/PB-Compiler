using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Float WIDTH through the x86-16 back end, which is the thing the differential battery caught it on.
///
/// Running the whole battery with the routed back end enabled scores 498 of 504 against the genuine
/// compilers where the direct path scores 504. Every one of the six is a float printed differently,
/// and they are two separate faults wearing the same clothes:
///
///   * a DOUBLE quotient one ulp out - .6666666666666666 for .6666666666666667
///   * a DOUBLE quotient carrying only SINGLE precision - .6666666865348816, which is the float 2/3
///     widened back up, so the value was rounded to 32 bits somewhere it should have stayed on the
///     x87
///
/// The second is the serious one: it is not a rounding disagreement but lost precision, and it prints
/// plausibly enough to read as the first.
/// </summary>
[TestFixture]
public sealed class BackendFloatWidthTests {

  private static string Run(string body, bool routed) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  private static void Agrees(string body) =>
    Assert.That(Run(body, routed: true), Is.EqualTo(Run(body, routed: false)));

  [Test]
  public void Divide_GivenDoubleOperands_ThenTheRoutedPathKeepsTheSameDigits() => Agrees("""
    DIM a AS DOUBLE, b AS DOUBLE
    a = 2
    b = 3
    PRINT a / b
    """);

  [Test]
  public void Divide_GivenIntegerLiteralsIntoADouble_ThenTheRoutedPathKeepsTheSameDigits() => Agrees("""
    DIM d AS DOUBLE
    d = 2 / 3
    PRINT d
    """);

  [Test]
  public void Log_GivenEulersNumber_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    PRINT LOG(2.718281828459045#)
    """);

  [Test]
  public void Sqrt_GivenTwo_ThenTheRoutedPathAgreesWithTheDirectOne() => Agrees("""
    DIM d AS DOUBLE
    d = SQR(2)
    PRINT d
    """);

  /// <summary>Integer over integer yields a DOUBLE in PB, and the quotient's last digit says so.</summary>
  [Test]
  public void Divide_GivenTwoIntegerVariables_ThenTheRoutedPathKeepsDoublePrecision() => Agrees("""
    C% = 2
    D% = 3
    PRINT C% / D%
    """);

  /// <summary>A folded constant quotient must fold at DOUBLE width, not at SINGLE.</summary>
  [Test]
  public void Str_GivenAConstantQuotient_ThenTheRoutedPathKeepsDoublePrecision() => Agrees("""
    PRINT STR$(2 / 3)
    """);

  [Test]
  public void Print_GivenAConstantQuotient_ThenTheRoutedPathKeepsDoublePrecision() => Agrees("""
    PRINT 2 / 3
    """);

  /// <summary>A double that passes through a FUNCTION must not be narrowed on the way.</summary>
  [Test]
  public void Function_GivenADoubleResult_ThenTheRoutedPathKeepsItsWidth() => Agrees("""
    FUNCTION Third AS DOUBLE
      DIM a AS DOUBLE
      a = 2
      Third = a / 3
    END FUNCTION
    PRINT Third
    """);
}
