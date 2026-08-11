using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// PowerBASIC's two decimal types through the retargetable path, and the reason they are not one
/// feature: <c>BCD</c> (<c>@@</c>) is a ten-byte x87 extended cell, so its bits ARE the value, while
/// <c>FIX</c> (<c>@</c>) is a scaled INT64 - the number times ten to the power of <c>pbvFixDigits</c>
/// - so reading one divides and writing one multiplies and rounds.
///
/// That exponent lives in a RUNTIME cell, not in the compiler, which is why the conversions are calls
/// to <c>rt_fixdn</c> / <c>rt_fixup</c> rather than arithmetic this pass folds. A compile-time divide
/// by 100 would agree with every test here and stop agreeing the moment a program assigned
/// <c>pbvFixDigits</c>, which is the case the last test covers.
/// </summary>
[TestFixture]
public sealed class BackendFixBcdTests {

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), cg.BackendRoutedNames.ToList());
  }

  private static void BothPathsAgree(string source, string expected) {
    var (routed, names) = Run(source, routed: true);
    Assert.That(names, Does.Contain("main"), "the back end has to have taken the module body");
    Assert.That(routed, Is.EqualTo(Run(source, routed: false).Output), "the two emitters disagree");
    Assert.That(routed, Is.EqualTo(expected));
  }

  /// <summary>
  /// A FIX cell quantizes at the STORE, to pbvFixDigits places - two by default - so 1.23456 keeps
  /// 1.23 and nothing of the rest survives the assignment. The negative case is here because the
  /// rounding is to nearest and not toward zero, and the two disagree on exactly the half.
  /// </summary>
  [Test]
  public void Route_GivenFixAssignments_ThenTheValueIsRoundedToTwoDecimalsAtTheStore() =>
    BothPathsAgree("""
      f@ = 1.23456
      PRINT f@
      f@ = 2.555
      PRINT f@
      f@ = -2.555
      PRINT f@
      """, "1.23 | 2.56 |-2.56");

  /// <summary>
  /// Arithmetic happens at the x87's own width and only the STORE quantizes, which is what makes
  /// 10 / 3 print 3.33 rather than something the cell could not hold - and what makes the whole
  /// storage-versus-value distinction necessary rather than decorative.
  /// </summary>
  [Test]
  public void Route_GivenFixArithmetic_ThenItComputesInExtendedAndQuantizesOnlyOnStore() =>
    BothPathsAgree("""
      g@ = 10
      h@ = 3
      PRINT g@ / h@
      PRINT g@ * h@
      PRINT g@ + h@
      PRINT g@ - h@
      k@ = 100
      k@ = k@ / 3
      PRINT k@
      """, "3.33333333333333 | 30 | 13 | 7 | 33.33");

  /// <summary>
  /// BCD is a float cell and carries its source's binary noise - .1 + .2 is not .3 - which is the
  /// distinction from FIX stated as behaviour rather than as a byte count.
  /// </summary>
  [Test]
  public void Route_GivenBcdArithmetic_ThenItBehavesAsTheExtendedFloatItIs() =>
    BothPathsAgree("""
      b@@ = 1.5
      c@@ = 0.0625
      PRINT b@@ + c@@
      PRINT b@@ * c@@
      PRINT b@@ / c@@
      PRINT SIZEOF(b@@); SIZEOF(c@@)
      """, "1.5625 | .09375 | 24 | 10  10");

  /// <summary>A FIX cell is eight bytes and a BCD cell ten - the sizes the two types are declared at.</summary>
  [Test]
  public void Route_GivenSizeof_ThenFixIsEightBytesAndBcdTen() =>
    BothPathsAgree("""
      f@ = 1
      b@@ = 1
      PRINT SIZEOF(f@); SIZEOF(b@@)
      """, "8  10");

  /// <summary>
  /// The scale is a runtime cell and not a constant: assigning pbvFixDigits changes what every later
  /// FIX store keeps and every later read divides by. A compile-time scaling would pass every other
  /// test here and fail this one.
  ///
  /// <para>
  /// The stored value is a VARIABLE rather than a literal, and that is not incidental. The direct
  /// emitter folds a FIX literal store at compile time against a hard-coded 100
  /// (CodeGenerator.Places, "FIX literal stores round DECIMALLY at compile time"), so with
  /// pbvFixDigits at 4 it writes the two-digit scaling and reads it back at four, printing .0123
  /// where the runtime scaling gives 1.2346. That is a defect in the fold rather than a fact about
  /// FIX, no corpus program reaches it, and reproducing it here would be writing the bug down twice.
  /// </para>
  /// </summary>
  [Test]
  public void Route_GivenPbvFixDigitsChanged_ThenLaterFixStoresQuantizeAtTheNewScale() =>
    BothPathsAgree("""
      x# = 1.23456
      PRINT pbvFixDigits
      f@ = x#
      PRINT f@
      pbvFixDigits = 4
      f@ = x#
      PRINT f@
      """, "2 | 1.23 | 1.2346");

  /// <summary>
  /// What the lowering actually emits, so the test above cannot pass by accident: a FIX cell is an
  /// i64, a read is <c>sitofp</c> then <c>rt_fix_down</c>, and a write is <c>rt_fix_up</c> then
  /// <c>fptosi</c>. A BCD cell is an f80 with no conversion either way.
  /// </summary>
  [Test]
  public void Lower_GivenFixAndBcdVariables_ThenOnlyFixCarriesTheScalingCalls() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      f@ = 1.5
      PRINT f@
      b@@ = 1.5
      PRINT b@@
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var main = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var instructions = main.Blocks.SelectMany(b => b.Instructions).ToList();

    Assert.That(instructions.OfType<IrAlloca>().Select(a => a.Allocated.ToString()),
      Is.EquivalentTo(new[] { "i64", "f80" }), "FIX is a scaled integer cell, BCD an extended float one");
    Assert.That(instructions.OfType<IrCall>().Select(c => (c.Callee as IrFunction)?.Name)
        .Where(n => n is not null && n.StartsWith("rt_fix", StringComparison.Ordinal)),
      Is.EqualTo(new[] { "rt_fix_up", "rt_fix_down" }), "one scaling per FIX access, none for BCD");
  }
}
