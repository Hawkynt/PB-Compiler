using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// 32-bit comparison materialized as PowerBASIC's -1/0 truth value. There is no 32-bit CMP on this
/// target, so it becomes a compare of the high words and - only when those are equal - a compare of
/// the low ones, unsigned even for the signed predicates because a signed 32-bit order is decided
/// entirely by the high half.
///
/// The cases are chosen to separate the halves: pairs that differ only in the low word, pairs that
/// differ only in the high word, pairs where the low words compare the other way from the whole
/// value, and the sign boundaries where a signed and an unsigned reading disagree.
/// </summary>
[TestFixture]
public sealed class BackendWideCompareTests {

  private static string Run(string source, bool routed, bool optimize = true) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>
  /// Without this, the cases below could all pass by falling back: when selection declines the direct
  /// emitter takes the function and both sides of the comparison are the same compiler.
  /// </summary>
  [Test]
  public void Compare_GivenTwoLongs_ThenTheFunctionActuallyRoutes() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize("""
      DIM a AS LONG
      DIM b AS LONG
      a = 100000
      b = 100001
      PRINT (a < b)
      """, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard().RunOnModule(module);

    var main = module.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var m = InstructionSelector.TrySelect(main, out var reason);
    Assert.That(m, Is.Not.Null, $"selection declined: {reason}");
    Assert.That(LinearScanAllocator.Allocate(m!), Is.Not.Null, "and it allocates, so the function routes");
  }

  [TestCase("100000", "100001")]        // differs only in the low word
  [TestCase("100001", "100000")]
  [TestCase("100000", "200000")]        // differs only in the high word
  [TestCase("65536", "1")]              // the low words compare the OTHER way from the values
  [TestCase("1", "65536")]
  [TestCase("-100000", "100000")]       // across zero: the sign lives in the high half
  [TestCase("-100001", "-100000")]      // both negative
  [TestCase("2147483647", "-2147483648")]  // the signed extremes
  [TestCase("100000", "100000")]        // equal
  public void Compare_GivenTwoLongs_ThenEveryPredicateAgreesWithTheDirectEmitter(string left, string right) {
    var source = $"""
      DIM a AS LONG
      DIM b AS LONG
      a = {left}
      b = {right}
      PRINT (a <  b); (a <= b); (a >  b); (a >= b); (a =  b); (a <> b)
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"{left} vs {right}");
  }

  /// <summary>The same values driving a BRANCH, which is the other half: the -1/0 is tested against zero.</summary>
  [TestCase("100000", "100001")]
  [TestCase("65536", "1")]
  [TestCase("-100000", "100000")]
  public void Compare_GivenItDrivesABranch_ThenBothPathsAgree(string left, string right) {
    var source = $"""
      DIM a AS LONG
      DIM b AS LONG
      a = {left}
      b = {right}
      IF a < b THEN
        PRINT "lt"
      ELSEIF a > b THEN
        PRINT "gt"
      ELSE
        PRINT "eq"
      END IF
      """;

    Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"{left} vs {right}");
  }

  /// <summary>
  /// The pairs whose DIFFERENCE does not fit a LONG. Every case above writes its operands down, so
  /// the optimizer folds the comparison and neither back end is asked the question - which is why
  /// the signed extremes were already listed there and still measured nothing. Here both operands
  /// come back out of a <c>NOINLINE</c> function called from two sites with different arguments, so
  /// no fold and no interprocedural propagation can prove either one.
  ///
  /// <para>
  /// The direct emitter decided a 32-bit ordering by the SIGN of <c>left - right</c>, which is right
  /// only while that subtraction stays in range: <c>-2147483648&amp; - 3&amp;</c> is <c>+2147483645</c>,
  /// whose sign says "greater". <c>-2147483648&amp; &lt; 3&amp;</c> therefore answered 0 where genuine
  /// PBC 3.50 answers -1 (<c>scripts/diff-one.sh tests/diff/DIFF98.BAS pb35</c>), in every dialect
  /// and both optimizer settings; the routed path was right throughout.
  /// </para>
  /// </summary>
  [TestCase("-2147483648", "3", "-1  0 -1  0  0 -1")]
  [TestCase("3", "-2147483648", "0 -1  0 -1  0 -1")]
  [TestCase("-2147483647", "3", "-1  0 -1  0  0 -1")]
  [TestCase("2147483647", "-3", "0 -1  0 -1  0 -1")]
  [TestCase("2147483647", "-2147483648", "0 -1  0 -1  0 -1")]
  [TestCase("-2147483648", "-2147483648", "0  0 -1 -1 -1  0")]
  [TestCase("-1073741824", "3", "-1  0 -1  0  0 -1")]   // the control: the difference still fits
  public void Compare_GivenADifferenceTooWideForTheType_ThenBothPathsGivePowerBasicsAnswer(
      string left, string right, string expected) {
    var source = $"""
      DECLARE FUNCTION Given&(BYVAL v&)

      DIM a AS LONG
      DIM b AS LONG
      a = Given&({left})
      b = Given&({right})
      PRINT (a <  b); (a >  b); (a <= b); (a >= b); (a =  b); (a <> b)
      END

      FUNCTION Given&(BYVAL v&) NOINLINE
        Given& = v&
      END FUNCTION
      """;

    foreach (var optimize in new[] { true, false }) {
      var direct = Run(source, routed: false, optimize);
      Assert.That(direct, Is.EqualTo(expected), $"direct, optimize={optimize}: {left} vs {right}");
      Assert.That(Run(source, routed: true, optimize), Is.EqualTo(direct),
        $"routed, optimize={optimize}: {left} vs {right}");
    }
  }

  /// <summary>The same overflowing ordering driving a BRANCH, where the -1/0 is never materialized.</summary>
  [TestCase("-2147483648", "3", "lt")]
  [TestCase("3", "-2147483648", "gt")]
  [TestCase("2147483647", "-2147483648", "gt")]
  [TestCase("-2147483648", "-2147483648", "eq")]
  public void Compare_GivenAnOverflowingDifferenceDrivesABranch_ThenBothPathsTakeTheSameArm(
      string left, string right, string expected) {
    var source = $"""
      DECLARE FUNCTION Given&(BYVAL v&)

      DIM a AS LONG
      DIM b AS LONG
      a = Given&({left})
      b = Given&({right})
      IF a < b THEN
        PRINT "lt"
      ELSEIF a > b THEN
        PRINT "gt"
      ELSE
        PRINT "eq"
      END IF
      END

      FUNCTION Given&(BYVAL v&) NOINLINE
        Given& = v&
      END FUNCTION
      """;

    foreach (var optimize in new[] { true, false }) {
      var direct = Run(source, routed: false, optimize);
      Assert.That(direct, Is.EqualTo(expected), $"direct, optimize={optimize}: {left} vs {right}");
      Assert.That(Run(source, routed: true, optimize), Is.EqualTo(direct),
        $"routed, optimize={optimize}: {left} vs {right}");
    }
  }
}
