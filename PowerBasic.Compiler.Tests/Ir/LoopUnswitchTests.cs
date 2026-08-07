using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0114 — loop unswitching. A conditional inside a loop whose condition never changes is tested every
/// iteration for the same answer; the test moves out and the loop is cloned per outcome.
///
/// The saving is not the compare - it is that each clone has the condition as a CONSTANT, so the
/// branch folds and the arm that cannot run is deleted. So the acceptance test looks at what survives
/// the following passes, not at the shape immediately after cloning.
/// </summary>
[TestFixture]
public sealed class LoopUnswitchTests {

  private static IrFunction Lowered(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);
    // LICM first, and that ordering is the whole composition: `IF mode THEN` lowers to a COMPARE
    // computed inside the loop, and a condition defined inside the region cannot be specialized by
    // cloning - the clone gets its own copy of the compare, so seeding the original with a constant
    // reaches nothing. Hoisting it out first is what makes the value substitutable.
    Licm.Run(fn);
    return fn;
  }

  private const string _invariantTest = """
    DIM i AS INTEGER
    DIM mode AS INTEGER
    DIM t AS INTEGER
    INPUT mode
    FOR i = 1 TO 40
      IF mode THEN
        t = t + 2
      ELSE
        t = t + 3
      END IF
    NEXT i
    PRINT t
    """;

  [Test]
  public void Loop_GivenAnInvariantConditionInside_ThenItIsUnswitched() {
    var fn = Lowered(_invariantTest);
    var before = fn.Blocks.Count;

    Assert.That(LoopUnswitch.Run(fn), Is.EqualTo(1));
    Assert.That(fn.Blocks.Count, Is.GreaterThan(before), "the loop is cloned once per outcome");
    Assert.That(IrVerifier.Verify(fn), Is.Empty, "the rewritten function must still be valid IR");
  }

  /// <summary>
  /// The point of the transform: with the condition constant inside each clone, the branch folds and
  /// one arm goes. If both arms survive, the clone was made and nothing was gained.
  /// </summary>
  [Test]
  public void Loop_GivenItIsUnswitched_ThenEachCloneKeepsOnlyOneArm() {
    var fn = Lowered(_invariantTest);
    Assume.That(LoopUnswitch.Run(fn), Is.EqualTo(1));

    for (var i = 0; i < 4; ++i) {
      InstCombine.Run(fn);
      Sccp.Run(fn);
      SimplifyCfg.Run(fn);
      Dce.Run(fn);
    }

    var conditional = fn.Blocks.Count(b => b.Terminator is IrCondBr);
    Assert.That(conditional, Is.LessThanOrEqualTo(3),
      "one test in the preheader and one loop-back test per clone; the inner branch should be gone");
  }

  /// <summary>A condition computed inside the loop is not invariant, whatever it looks like.</summary>
  [Test]
  public void Loop_GivenTheConditionIsComputedInside_ThenItIsLeftAlone() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      FOR i = 1 TO 40
        IF i > 20 THEN
          t = t + 2
        ELSE
          t = t + 3
        END IF
      NEXT i
      PRINT t
      """);

    Assert.That(LoopUnswitch.Run(fn), Is.Zero, "the test depends on the counter, so it changes every iteration");
  }

  [Test]
  public void Loop_GivenNoConditionalInside_ThenThereIsNothingToUnswitch() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      FOR i = 1 TO 40
        t = t + 1
      NEXT i
      PRINT t
      """);

    Assert.That(LoopUnswitch.Run(fn), Is.Zero);
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var fn = Lowered(_invariantTest);
    fn.HasErrorHandler = true;

    Assert.That(LoopUnswitch.Run(fn), Is.Zero);
  }
}
