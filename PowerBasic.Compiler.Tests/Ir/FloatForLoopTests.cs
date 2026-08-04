using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>FOR</c> over a SINGLE/DOUBLE counter. The block structure is the integer loop's, with float
/// operations in place of the integer ones - an ordered compare for the test, <c>FAdd</c> for the
/// step.
///
/// The counter is deliberately NOT rewritten into an integer loop when the bounds look whole. A float
/// counter <b>accumulates</b> its step, which is why <c>FOR x! = 0 TO 1 STEP .1</c> famously runs nine
/// times rather than ten: a tenth is not representable, the error accumulates, and the eleventh value
/// lands just past 1. Reproducing that is the whole point of a fidelity compiler.
/// </summary>
[TestFixture]
public sealed class FloatForLoopTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IEnumerable<IrInstruction> Body(IrModule module)
    => module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions);

  [Test]
  public void Lower_GivenAnAscendingFloatCounter_ThenTestsWithAnOrderedLessOrEqual() {
    var module = Lower("""
      DIM x AS SINGLE
      FOR x = 0 TO 1 STEP .1
        PRINT x
      NEXT x
      """);

    var compares = Body(module).OfType<IrCmp>().Select(c => c.Pred).ToList();
    Assert.That(compares, Does.Contain(IrCmpPred.Fole));
    Assert.That(compares, Does.Not.Contain(IrCmpPred.Sle), "a float counter is not compared as an integer");
  }

  [Test]
  public void Lower_GivenADescendingFloatCounter_ThenTestsTheOtherWayRound() {
    var module = Lower("""
      DIM x AS SINGLE
      FOR x = 1 TO 0 STEP -.25
        PRINT x
      NEXT x
      """);

    Assert.That(Body(module).OfType<IrCmp>().Select(c => c.Pred), Does.Contain(IrCmpPred.Foge));
  }

  [Test]
  public void Lower_GivenAFloatCounter_ThenTheStepIsAFloatAdd() {
    // the counter ACCUMULATES its step - it is not recomputed from a trip count, which is what makes
    // the representation error visible exactly as the genuine compiler shows it
    var module = Lower("""
      DIM x AS SINGLE
      FOR x = 0 TO 1 STEP .1
        PRINT x
      NEXT x
      """);

    Assert.That(Body(module).OfType<IrBinary>().Select(b => b.Op), Does.Contain(IrBinaryOp.FAdd));
  }

  [Test]
  public void Lower_GivenARuntimeStep_ThenAsksTheDirectionEachTimeRound() {
    // an unknown sign means the test cannot be picked at compile time: it becomes
    // (step >= 0 AND i <= limit) OR (step < 0 AND i >= limit), which is loop-invariant enough for LICM
    var module = Lower("""
      DIM x AS SINGLE
      DIM s AS SINGLE
      s = .5
      FOR x = 0 TO 2 STEP s
        PRINT x
      NEXT x
      """);

    var compares = Body(module).OfType<IrCmp>().Select(c => c.Pred).ToList();
    Assert.That(compares, Does.Contain(IrCmpPred.Foge));
    Assert.That(compares, Does.Contain(IrCmpPred.Fole), "both directions are tested");
  }

  [Test]
  public void Lower_GivenADoubleCounter_ThenLowersToo() {
    var module = Lower("""
      DIM d AS DOUBLE
      FOR d = 0 TO 3
        PRINT d
      NEXT d
      """);

    Assert.That(Body(module).OfType<IrBinary>().Any(b => b.Op == IrBinaryOp.FAdd && b.Type == IrType.F64));
  }
}
