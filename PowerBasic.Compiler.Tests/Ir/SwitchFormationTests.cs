using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <see cref="SwitchFormation"/>: putting a <c>SELECT CASE</c> back together out of the per-arm compare
/// chain the lowering emits, so that a back end has one dispatch to select rather than six blocks of
/// comparisons.
///
/// <para>
/// The cases below are the equivalence classes of what a branch condition can SAY about one integer
/// subject - a value list, a range, an exclusion, and the same three spelled as an <c>IF</c> - plus the
/// boundaries where the pass must decline: two values, a range too wide to enumerate, and a chain over
/// more than one variable.
/// </para>
/// </summary>
[TestFixture]
public sealed class SwitchFormationTests {

  /// <summary>The optimized <c>main</c> of a pb36 program, at the point the routing runs this pass.</summary>
  private static IrFunction Optimized(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model);
    Assert.That(module, Is.Not.Null, "the program must lower");
    IrPassManager.Standard(true).RunOnModule(module!);
    var main = module!.FindFunction("main");
    Assert.That(main, Is.Not.Null);
    return main!;
  }

  private static IrSwitch? FormAndFind(string source, out IrFunction fn) {
    fn = Optimized(source);
    if (SwitchFormation.Run(fn) > 0) {
      SimplifyCfg.Run(fn);
      Dce.Run(fn);
    }
    Assert.That(IrVerifier.Verify(fn), Is.Empty, "the pass must leave valid SSA");
    return fn.AllInstructions.OfType<IrSwitch>().FirstOrDefault();
  }

  private static IrSwitch? FormAndFind(string source) => FormAndFind(source, out _);

  /// <summary>A SELECT over a subject the folder cannot resolve - the only kind with a dispatch left.</summary>
  private static string Select(string arms) => $"""
    DIM x AS INTEGER
    READ x
    SELECT CASE x
    {arms}
    END SELECT
    DATA 3
    END
    """;

  [Test]
  public void Run_GivenAValueListArm_ThenOneCasePerValue() {
    var formed = FormAndFind(Select("""
      CASE 1, 8, 15
        PRINT "a"
      CASE ELSE
        PRINT "z"
      """));

    Assert.That(formed, Is.Not.Null);
    Assert.That(formed!.Cases.Select(c => c.Value), Is.EquivalentTo(new long[] { 1, 8, 15 }));
    Assert.That(formed.Cases.Select(c => c.Target).Distinct().Count(), Is.EqualTo(1), "all three reach one arm");
  }

  [Test]
  public void Run_GivenAConstantRangeArm_ThenTheRangeIsEnumerated() {
    // the two signed compares the lowering emits intersect to one interval, which is what makes the
    // values contiguous - and contiguity is the whole signal a back end needs to emit a range test
    var formed = FormAndFind(Select("""
      CASE 0 TO 9
        PRINT "in"
      CASE ELSE
        PRINT "out"
      """));

    Assert.That(formed, Is.Not.Null);
    Assert.That(formed!.Cases.Select(c => c.Value), Is.EquivalentTo(Enumerable.Range(0, 10).Select(v => (long)v)));
  }

  [Test]
  public void Run_GivenSeveralArms_ThenTheWholeChainBecomesOneSwitch() {
    var formed = FormAndFind(Select("""
      CASE 0, 4, 8, 12
        PRINT "a"
      CASE 1, 5, 9, 13
        PRINT "b"
      CASE 2, 6, 10, 14
        PRINT "c"
      CASE ELSE
        PRINT "z"
      """), out var fn);

    Assert.That(formed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(formed!.Cases, Has.Count.EqualTo(12), "every arm's values, in one dispatch");
      Assert.That(formed.Cases.Select(c => c.Target).Distinct().Count(), Is.EqualTo(3), "three arm bodies");
      Assert.That(fn.AllInstructions.OfType<IrSwitch>().Count(), Is.EqualTo(1), "and only one switch");
      Assert.That(fn.AllInstructions.OfType<IrCmp>(), Is.Empty,
        "the twelve compares the chain was made of are gone - a switch that left them behind would "
        + "cost the dispatch AND the chain");
    });
  }

  [Test]
  public void Run_GivenEarlierArmsClaimingAValue_ThenTheFirstArmKeepsIt() {
    var formed = FormAndFind(Select("""
      CASE 1, 2, 3
        PRINT "a"
      CASE 3, 4, 5
        PRINT "b"
      CASE ELSE
        PRINT "z"
      """));

    Assert.That(formed, Is.Not.Null);
    var first = formed!.Cases.First(c => c.Value == 1).Target;
    Assert.That(formed.Cases.Where(c => c.Value == 3).Select(c => c.Target), Is.EqualTo(new[] { first }),
      "3 belongs to the arm that named it first, exactly as the compare chain decided");
  }

  /// <summary>
  /// The arm that ends a chain has to SURVIVE it. <c>CASE IS &gt; 1000</c> reads as a perfectly good set
  /// of 31767 values, is then rejected for being too wide to enumerate, and so becomes the dispatch's
  /// default - which is why counting it as consumed before it was used deleted the block the switch had
  /// just pointed at, and sent every non-member to whatever followed.
  /// </summary>
  [Test]
  public void Run_GivenAComparisonArmAfterTheChain_ThenItRemainsAsTheDefault() {
    var formed = FormAndFind(Select("""
      CASE 1, 3, 5, 9
        PRINT "set"
      CASE IS > 1000
        PRINT "big"
      CASE ELSE
        PRINT "else"
      """), out var fn);

    Assert.That(formed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(formed!.Cases, Has.Count.EqualTo(4));
      Assert.That(formed.DefaultTarget.Parent, Is.SameAs(fn), "the default block is still in the function");
      Assert.That(formed.DefaultTarget.Instructions.OfType<IrCmp>().Any(), Is.True, "and still tests > 1000");
    });
  }

  [Test]
  public void Run_GivenAnOrChainOfEqualities_ThenTheIfBecomesASwitch() {
    var formed = FormAndFind("""
      DIM k AS INTEGER
      READ k
      IF k = 1 OR k = 8 OR k = 15 THEN PRINT "a" ELSE PRINT "b"
      DATA 8
      END
      """);

    Assert.That(formed, Is.Not.Null);
    Assert.That(formed!.Cases.Select(c => c.Value), Is.EquivalentTo(new long[] { 1, 8, 15 }));
  }

  [Test]
  public void Run_GivenAnAndChainOfInequalities_ThenTheExcludedValuesBecomeTheCases() {
    // the De Morgan complement: `k <> 2 AND k <> 5 AND k <> 11` is true for everything BUT those three,
    // so the enumerable side is the exclusion set and the arm it reaches is the ELSE
    var formed = FormAndFind("""
      DIM k AS INTEGER
      READ k
      IF k <> 2 AND k <> 5 AND k <> 11 THEN PRINT "y" ELSE PRINT "n"
      DATA 5
      END
      """);

    Assert.That(formed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(formed!.Cases.Select(c => c.Value), Is.EquivalentTo(new long[] { 2, 5, 11 }));
      Assert.That(formed.Cases.Select(c => c.Target).Distinct().Count(), Is.EqualTo(1));
      Assert.That(formed.Cases.All(c => !ReferenceEquals(c.Target, formed.DefaultTarget)), Is.True,
        "excluded values go one way and everything else the other");
    });
  }

  [Test]
  public void Run_GivenTwoValues_ThenNoSwitchIsFormed()
    => Assert.That(FormAndFind(Select("""
      CASE 1, 15
        PRINT "a"
      CASE ELSE
        PRINT "z"
      """)), Is.Null, "two compares are already the cheapest dispatch there is");

  [Test]
  public void Run_GivenARangeWiderThanTheCap_ThenNoSwitchIsFormed()
    => Assert.That(FormAndFind(Select("""
      CASE 0 TO 1000
        PRINT "in"
      CASE ELSE
        PRINT "out"
      """)), Is.Null, "enumerating 1001 values buys nothing a back end can use");

  [Test]
  public void Run_GivenAChainOverTwoVariables_ThenNoSwitchIsFormed()
    => Assert.That(FormAndFind("""
      DIM k AS INTEGER, j AS INTEGER
      READ k, j
      IF k = 1 OR j = 8 OR k = 15 THEN PRINT "a"
      DATA 1, 8
      END
      """), Is.Null, "a set membership is about ONE value");

  [Test]
  public void Run_GivenAStringSubject_ThenNoSwitchIsFormed()
    => Assert.That(FormAndFind("""
      DIM s AS STRING
      READ s
      SELECT CASE s
        CASE "a"
          PRINT 1
        CASE "b"
          PRINT 2
        CASE "c"
          PRINT 3
        CASE ELSE
          PRINT 0
      END SELECT
      DATA b
      END
      """), Is.Null, "a string comparison is a runtime call, not an integer predicate");
}
