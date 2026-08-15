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

  /// <summary>
  /// As <see cref="Agrees"/>, and additionally that the module body ROUTED - without which the two
  /// builds are one build and the comparison holds by construction.
  /// </summary>
  private static void AgreesRouted(string body, string expected) {
    var source = body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var routed = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = true };
    routed.EmitExecutable();
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the back end did not take the module body under test");
    Assert.That(Run(body, routed: true), Is.EqualTo(Run(body, routed: false)));
    Assert.That(Run(body, routed: false), Is.EqualTo(expected), "and the answer is PB's");
  }

  /// <summary>
  /// A value stored into a SINGLE is ROUNDED to a SINGLE, and stays rounded when it is widened back.
  /// This back end parks every float in a ten-byte cell at the x87's own width on purpose, so an
  /// <c>fptrunc</c> between two of those cells changed nothing at all - and once <c>mem2reg</c>
  /// promotes the variable there is no four-byte cell left to do the rounding either.
  ///
  /// <para>
  /// Widening the value back is what makes it observable: PRINT of a SINGLE shows seven significant
  /// digits whatever the cell holds, so the SINGLE formatter cannot tell a rounded value from an
  /// unrounded one. Genuine PBC 3.50 answers 1.66666662693024, which is the single nearest 5/3.
  /// The operands come through a two-call-site NOINLINE function so nothing folds.
  /// </para>
  /// </summary>
  [Test]
  public void Store_GivenAQuotientInASingle_ThenTheCellHoldsOnlySinglePrecision() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DIM p AS INTEGER, q AS INTEGER
    DIM sg AS SINGLE, db AS DOUBLE
    p = G%(5)
    q = G%(3)
    sg = p / q
    db = sg
    PRINT db
    PRINT sg * 3
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, "1.66666662693024 | 4.99999988079071");

  /// <summary>
  /// The same rounding, arrived at through a SINGLE array element rather than a scalar - the storage
  /// kind is what decides whether a four-byte cell exists at all, and an element's does not get
  /// promoted the way a scalar's does. The eighty-to-sixty-four rounding is NOT tested here: this
  /// interpreter's x87 carries a double, so a DOUBLE and an EXTENDED are the same value to it and a
  /// test of that width would measure nothing.
  /// </summary>
  [Test]
  public void Store_GivenAQuotientInASingleArrayElement_ThenTheElementHoldsOnlySinglePrecision() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DIM p AS INTEGER, q AS INTEGER
    DIM a(1 TO 2) AS SINGLE
    DIM db AS DOUBLE
    p = G%(5)
    q = G%(3)
    a(1) = p / q
    db = a(1)
    PRINT db
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, "1.66666662693024");

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
