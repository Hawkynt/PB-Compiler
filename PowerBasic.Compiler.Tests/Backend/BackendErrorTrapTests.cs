using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The <c>$ERROR</c> traps a program arms, inside a PROCEDURE, through the IR path - the case where
/// they were not emitted at all.
///
/// <para>
/// <c>$ERROR BOUNDS/OVERFLOW/NUMERIC ON</c> is a metastatement in the module body, and
/// <c>IrLowering</c> used to learn about it only by executing that statement. Each procedure is
/// lowered by its own <c>IrLowering</c> whose flags start clear, so the directive armed the check in
/// the module body and in nothing else: a <c>SUB</c> multiplied its way past 32767 and printed the
/// wrapped number where PowerBASIC stops the program.
/// </para>
///
/// <para>
/// <b>Measured by running the program, not by reading its bytes.</b> A byte assertion is how this
/// stayed invisible - the trap was never lowered, so there was nothing in the image to be missing from
/// a pattern nobody had looked for. Each case is executed under the interpreter and its output read
/// for the trap or the wrapped value the source calls for; the raise count is a secondary diagnostic
/// and never the only assertion.
/// </para>
///
/// <para>
/// Every subject reaches its procedure through <b>two</b> call sites with different arguments. One
/// call site is not a measurement: <c>NOINLINE</c> stops the body being absorbed but does nothing to
/// stop interprocedural constant propagation proving the argument, after which SCCP folds the
/// arithmetic and the test is about a program with no arithmetic in it.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendErrorTrapTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static byte[] Compile(string source) {
    var generator = new CodeGenerator(Bind(source)) { Optimize = true};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendRoutedNames, Is.Not.Empty, "the back end declined every procedure");
    return image;
  }

  /// <summary>
  /// The program's output, with whatever stopped the machine appended rather than thrown.
  ///
  /// <para>
  /// A trap that is missing does not merely print the wrong number - a FOR counter that wraps where it
  /// should have raised Error 6 never reaches its limit, and the interpreter gives up on it. Reported
  /// as a SKIP (the usual idiom here) that reads as "the interpreter cannot run this", which is
  /// precisely the defect wearing an excuse; folded into the output it is a difference from the
  /// expected text like any other.
  /// </para>
  /// </summary>
  private static string Run(byte[] image) {
    var cpu = Cpu8086.Run(image, new Dictionary<string, byte[]>(), out var fault);
    return fault is null ? cpu.Output : cpu.Output + "\n[stopped: " + fault.Message + "]";
  }

  /// <summary>How many <c>MOV AX, code / CALL</c> raise sequences the image holds.</summary>
  private static int CountRaise(byte[] image, byte code) {
    var count = 0;
    for (var i = 0; i + 3 < image.Length; ++i)
      if (image[i] == 0xB8 && image[i + 1] == code && image[i + 2] == 0x00 && image[i + 3] == 0xE8)
        ++count;
    return count;
  }

  private const string _CHECKED_MULTIPLY = """
    $ERROR OVERFLOW ON
    DECLARE SUB Show(BYVAL x%)
    Show 30000
    Show 7
    END
    SUB Show(BYVAL x%) NOINLINE
      PRINT x% * 2
    END SUB
    """;

  /// <summary>The same program with the directive OFF - the control that says the trap above is the directive's.</summary>
  private const string _UNCHECKED_MULTIPLY = """
    $ERROR OVERFLOW OFF
    DECLARE SUB Show(BYVAL x%)
    Show 30000
    Show 7
    END
    SUB Show(BYVAL x%) NOINLINE
      PRINT x% * 2
    END SUB
    """;

  private const string _CHECKED_SUBSCRIPT = """
    $ERROR BOUNDS ON
    DECLARE SUB Walk(BYVAL m%)
    Walk 5
    Walk 3
    END
    SUB Walk(BYVAL m%) NOINLINE
      DIM a%(1 TO 5), p%(1 TO 5), x%
      FOR i% = 1 TO m%
        x% = a%(p%(i%))
      NEXT i%
      PRINT x%
    END SUB
    """;

  private const string _CHECKED_COUNTER = """
    $ERROR NUMERIC ON
    DECLARE SUB Count(BYVAL m%)
    Count 32767
    Count 3
    END
    SUB Count(BYVAL m%) NOINLINE
      FOR i% = 32765 TO m%
        PRINT i%
      NEXT i%
    END SUB
    """;

  /// <summary>
  /// <c>$ERROR OVERFLOW ON</c> over a multiply inside a procedure: 30000 * 2 does not fit an INTEGER,
  /// so the program stops. Routed it printed -5536 and carried on.
  /// </summary>
  [Test]
  public void Execute_GivenCheckedMultiplyInsideAProcedure_WhenRouted_ThenTheOverflowStillTraps() {
    var image = Compile(_CHECKED_MULTIPLY);
    var output = Run(image);

    Assert.Multiple(() => {
      Assert.That(output, Does.Contain("RUNTIME ERROR"), "the Error 6 trap the directive armed is gone");
      Assert.That(output, Does.Not.Contain("-5536"), "the wrapped product was printed instead of trapping");
      Assert.That(CountRaise(image, 0x06), Is.Positive, "and the raise really is in the image");
    });
  }

  /// <summary>
  /// The control, and it is the half that makes the above a measurement rather than an assertion that
  /// every program traps: with the directive OFF the product wraps, the second call still runs, and no
  /// raise is emitted at all.
  /// </summary>
  [Test]
  public void Execute_GivenUncheckedMultiplyInsideAProcedure_WhenRouted_ThenTheProductWrapsWithNoTrap() {
    var image = Compile(_UNCHECKED_MULTIPLY);
    var output = Run(image);

    Assert.Multiple(() => {
      Assert.That(output, Does.Not.Contain("RUNTIME ERROR"), "nothing armed a trap here");
      Assert.That(output, Does.Contain(" 14 "), "the second call, 7 * 2, still ran");
      Assert.That(CountRaise(image, 0x06), Is.Zero,
        "an unarmed check must not reach the image");
    });
  }

  /// <summary>
  /// <c>$ERROR BOUNDS ON</c> over a subscript inside a procedure. The index comes out of a second array
  /// that is all zeros, which is below the first array's lower bound of 1 - so the read is out of range
  /// on the first iteration. Routed it printed 0.
  /// </summary>
  [Test]
  public void Execute_GivenCheckedSubscriptInsideAProcedure_WhenRouted_ThenTheOutOfRangeIndexStillTraps() {
    var image = Compile(_CHECKED_SUBSCRIPT);
    var output = Run(image);

    Assert.Multiple(() => {
      Assert.That(output, Does.Contain("RUNTIME ERROR"), "the Error 9 trap the directive armed is gone");
      Assert.That(CountRaise(image, 0x09), Is.Positive, "and the raise really is in the image");
    });
  }

  /// <summary>
  /// <c>$ERROR NUMERIC ON</c> over a FOR counter inside a procedure: stepping past 32767 wraps the
  /// counter, which is Error 6. Routed the loop ran on into the negatives instead.
  /// </summary>
  [Test]
  public void Execute_GivenCheckedForCounterInsideAProcedure_WhenRouted_ThenTheWrappedCounterStillTraps() {
    var output = Run(Compile(_CHECKED_COUNTER));

    Assert.Multiple(() => {
      Assert.That(output, Does.Contain("RUNTIME ERROR"), "the Error 6 trap the directive armed is gone");
      Assert.That(output, Does.Not.Contain("-32768"), "the counter wrapped instead of trapping");
    });
  }

  /// <summary>
  /// What the fix must NOT cost: a check the range analysis can prove will not fire is still elided.
  /// The counter runs 1 to 2 over an array dimensioned 1 to 2, so that subscript needs no guard, while
  /// the subscript it produces is unknown and keeps one. The baseline is the same program with the
  /// counter's bound read at run time, which makes the first subscript unprovable too: the proven build
  /// must emit FEWER raises than that and still stop. An over-conservative repair shows up as an equal
  /// count. (The baseline used to be the direct emitter; it is gone, and the analysis being measured
  /// now lives only in the back end, so the comparison is between two programs, not two compilers.)
  /// </summary>
  [Test]
  public void Execute_GivenAProvablyInRangeSubscript_WhenRouted_ThenOnlyThatCheckIsElided() {
    const string template = """
      $ERROR BOUNDS ON
      DIM a%(1 TO 5)
      DIM p%(1 TO 2)
      n% = LEN(COMMAND$) + 2
      FOR i% = 1 TO {0}
        x% = a%(p%(i%))
        PRINT x%
      NEXT i%
      END
      """;
    var provenImage = Compile(template.Replace("{0}", "2"));
    var unprovenImage = Compile(template.Replace("{0}", "n%"));

    Assert.Multiple(() => {
      Assert.That(Run(provenImage), Is.EqualTo(Run(unprovenImage)),
        "the out-of-range read must still stop the program");
      Assert.That(Run(provenImage), Does.Contain("RUNTIME ERROR"),
        "the out-of-range read must raise Error 9 at all");
      Assert.That(CountRaise(provenImage, 0x09), Is.LessThan(CountRaise(unprovenImage, 0x09)),
        "the provable check should still be elided");
      Assert.That(CountRaise(provenImage, 0x09), Is.Positive, "and the unprovable one kept");
    });
  }
}
