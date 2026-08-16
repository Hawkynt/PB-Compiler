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

  /// <summary>
  /// Only a SINGLE renders through the seven-digit formatter. The lowering's test named the DOUBLE by
  /// its byte size and let everything else fall to the single, which put the two WIDER formats on the
  /// wrong side of it - <c>STR$</c> of an EXT holding 5/3 came back <c>1.666667</c>. Genuine PBC 3.50
  /// answers <c>1.66666666666667</c> (checked with <c>scripts/diff-one.sh</c>), and so does the direct
  /// emitter, whose dispatch names ByteSize 4 and falls everything else to the 64-bit renderer.
  /// </summary>
  [Test]
  public void Str_GivenAnExtendedValue_ThenItRendersFifteenSignificantDigits() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DIM ex AS EXT, sg AS SINGLE, db AS DOUBLE
    ex = G%(5) / G%(3)
    sg = G%(5) / G%(3)
    db = G%(5) / G%(3)
    PRINT STR$(ex)
    PRINT STR$(db)
    PRINT STR$(sg)
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, "1.66666666666667| 1.66666666666667| 1.666667");

  /// <summary>
  /// A float comparison happens at the x87's own width, so a SINGLE cell and an unrounded quotient are
  /// two different numbers. The lowering used to take the MAX of the two declared widths as the common
  /// compare type, which for a SINGLE against a SINGLE-typed constant expression narrowed the constant
  /// too - whereupon the two were bit-identical and <c>sg = 1 / 3</c> was TRUE. Genuine PBC 3.50 says
  /// <c>ne</c> for both spellings, and it is the second that shows the rule is about width rather than
  /// about folding: <c>.3333333</c> is a literal, not a quotient, and still is not the SINGLE 1/3.
  /// </summary>
  [Test]
  public void Compare_GivenASingleCellAgainstAnUnroundedConstant_ThenTheyAreNotEqual() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DIM sg AS SINGLE
    sg = G%(1) / G%(3)
    IF sg = 1 / 3 THEN PRINT "eq" ELSE PRINT "ne"
    IF sg = .3333333 THEN PRINT "eqlit" ELSE PRINT "nelit"
    IF sg < 1 / 3 THEN PRINT "lt" ELSE PRINT "nlt"
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, "ne|nelit|nlt");

  /// <summary>
  /// <c>CSNG</c> rounds, which the direct emitter was not doing: its <c>Coerce</c> answers "both sides
  /// are floats" and returns, so the value stayed at the register's own width. Genuine PBC 3.50 prints
  /// <c>.666666686534882</c> for <c>CDBL(CSNG(2 / 3))</c> - the single nearest 2/3, widened back - and
  /// <c>2.00000005960464</c> once that rounded value is multiplied by three and stored in a DOUBLE.
  /// The routed path had it right from the start; both are checked here, and the value has to be
  /// widened again before either says so, PRINT of a SINGLE showing seven digits whatever it holds.
  /// </summary>
  [Test]
  public void Csng_GivenADoubleQuotient_ThenTheResultCarriesOnlySinglePrecision() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DIM db AS DOUBLE, d2 AS DOUBLE
    db = G%(2) / G%(3)
    PRINT CDBL(CSNG(db))
    PRINT CEXT(CSNG(db))
    d2 = CSNG(db) * 3
    PRINT d2
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, ".666666686534882 | .666666686534882 | 2.00000005960464");

  /// <summary>
  /// A SINGLE loop counter is rounded to a SINGLE every time it is incremented, and the accumulated
  /// difference is the whole observation: <c>FOR x! = 0 TO 1 STEP .1</c> summed to 4.50000026077032
  /// on the direct path and under genuine PBC 3.50, and to 4.50000006705523 routed - the counter kept
  /// at eighty bits. Once <c>mem2reg</c> promotes the counter its four-byte cell is gone and the
  /// <c>fadd float</c> the lowering wrote is the only thing left that says SINGLE; the selector
  /// computed it at the register's width and stored the result in a ten-byte cell.
  /// </summary>
  [Test]
  public void For_GivenASingleCounter_ThenEachIncrementIsRoundedToASingle() => AgreesRouted("""
    DIM x AS SINGLE, total AS DOUBLE
    total = 0
    FOR x = 0 TO 1 STEP .1
      total = total + x
    NEXT x
    PRINT total
    """, "4.50000026077032");

  /// <summary>
  /// A <c>%</c> equate holds an INTEGER. Genuine PBC 3.50 will not even accept a fractional one -
  /// <c>%A = 3.75</c> is <c>Error 427: Integer constant expected</c> - and prints <c>0</c> for the
  /// <c>%B = 1 / 3</c> it does accept. This compiler is a superset there, which is fine; it was TWO
  /// supersets, the lowering carrying the folder's floating value where the direct emitter has always
  /// read the integer one.
  /// </summary>
  [Test]
  public void Equate_GivenAFractionalValue_ThenItHoldsTheIntegerPbWouldStore() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    %THIRD = 1 / 3
    DIM sg AS SINGLE
    sg = G%(1) / G%(3)
    PRINT %THIRD
    IF sg = %THIRD THEN PRINT "eq" ELSE PRINT "ne"
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    """, "0 |ne");

  /// <summary>
  /// A SINGLE FUNCTION result is rounded to a SINGLE, and O0102's return-value forwarding was
  /// dropping that: the epilogue's reload from the result variable's four-byte cell is not merely a
  /// move, it is the rounding, so leaving the value in ST(0) returned all eighty bits.
  /// <c>F! = v% / 3</c> handed back <c>1.66666666666667</c> under <c>--optimize</c> where the
  /// unoptimized build, the routed back end and genuine PBC 3.50 all say <c>1.66666662693024</c>.
  /// Both functions get two call sites, or interprocedural constant propagation proves the argument
  /// and the whole thing folds to an answer neither back end computed.
  /// </summary>
  [Test]
  public void Function_GivenASingleResult_ThenItIsRoundedToASingleUnderTheOptimizer() => AgreesRouted("""
    DECLARE FUNCTION G%(BYVAL v%)
    DECLARE FUNCTION F!(BYVAL v%)
    DIM db AS DOUBLE
    db = F!(G%(5))
    PRINT db
    PRINT F!(G%(7))
    FUNCTION G%(BYVAL v%) NOINLINE
      G% = v% + 0
    END FUNCTION
    FUNCTION F!(BYVAL v%) NOINLINE
      F! = v% / 3
    END FUNCTION
    """, "1.66666662693024 | 2.333333");

  /// <summary>
  /// A magnitude too small for the <b>interpreter's</b> 80-bit conversion to survive, which is not a
  /// compiler question and read exactly like one. <c>Cpu8086</c> scaled by multiplying with
  /// <c>Math.Pow(2, 63 - exponent)</c>: 1E-300 has a binary exponent of -997, so the power itself
  /// overflowed to infinity and the stored mantissa was zero. Every extended value below about
  /// 1E-289 was therefore ZERO to the oracle - and only on the path that parks intermediates in
  /// ten-byte cells, so it presented as a routed miscompile of the tiny-magnitude cases and nowhere
  /// else. Genuine PBC 3.50 answers 1E+300 and 2E+300 here (<c>scripts/diff-one.sh</c>).
  /// </summary>
  [Test]
  public void Divide_GivenAMagnitudeBelowTheExtendedScalingLimit_ThenTheValueSurvivesTheTenByteCell() => AgreesRouted("""
    DECLARE FUNCTION GD#(BYVAL v#)
    DIM db AS DOUBLE
    db = GD#(1E-300#)
    PRINT 1 / db
    PRINT GD#(2#) / db
    FUNCTION GD#(BYVAL v#) NOINLINE
      GD# = v# + 0#
    END FUNCTION
    """, "1E+300 | 2E+300");
}
