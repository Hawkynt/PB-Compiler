using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The three CSE shapes the direct emitter needed separate machinery for (O0185 past a merge, O0186
/// into a loop preheader, O0188 an IF condition), checked on the IR.
///
/// The direct tier works on a cache of emitted expressions and has to prove, by hand, that no arm of a
/// branch overwrote an input - which is what <c>RetainPastMerge</c>, <c>CollectWrites</c> and their
/// kin are for. In SSA that proof is the representation: if an operand is still the same SSA value, no
/// intervening store changed it, and GVN's dominator-scoped table then reuses the leader wherever it
/// dominates. So all three shapes should already be handled with no pass of their own.
///
/// These are the tests that say so rather than assuming it. Each counts the surviving arithmetic.
/// </summary>
[TestFixture]
public sealed class CseShapeTests {

  private static IrFunction Optimized(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    return module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>PB multiplies are float-shaped in the IR, so both spellings have to be counted.</summary>
  private static int Multiplies(IrFunction fn)
    => fn.AllInstructions.OfType<IrBinary>().Count(b => b.Op is IrBinaryOp.Mul or IrBinaryOp.FMul);

  /// <summary>O0185: a value computed before an IF is still valid after the merge.</summary>
  [Test]
  public void Cse_GivenAValueRecomputedAfterAMerge_ThenItSurvivesTheBranch() {
    var fn = Optimized("""
      DIM x AS INTEGER, y AS INTEGER, a AS INTEGER, b AS INTEGER, c AS INTEGER, flag AS INTEGER
      INPUT x, y, flag
      a = y * 320 + x
      IF flag THEN
        c = 1
      ELSE
        c = 2
      END IF
      b = y * 320 + x
      PRINT a; b; c
      """);

    Assert.That(Multiplies(fn), Is.EqualTo(1),
      "the pre-branch block dominates the merge, so the second y*320 is the first");
  }

  /// <summary>O0188: a condition recomputed inside the arm it guards.</summary>
  [Test]
  public void Cse_GivenTheConditionRecomputedInsideTheArm_ThenItIsComputedOnce() {
    var fn = Optimized("""
      DIM x AS INTEGER, y AS INTEGER, r AS INTEGER
      INPUT x, y
      IF x * y > 10 THEN
        r = x * y
      ELSE
        r = 0
      END IF
      PRINT r
      """);

    Assert.That(Multiplies(fn), Is.EqualTo(1), "the test dominates the arm that recomputes it");
  }

  /// <summary>O0186: a loop-invariant value computed before the loop and again inside it.</summary>
  [Test]
  public void Cse_GivenAnInvariantRecomputedInTheLoop_ThenItIsComputedOnce() {
    var fn = Optimized("""
      DIM x AS INTEGER, y AS INTEGER, i AS INTEGER, t AS INTEGER
      INPUT x, y
      t = x * y
      FOR i = 1 TO 40
        t = t + x * y
      NEXT i
      PRINT t
      """);

    Assert.That(Multiplies(fn), Is.EqualTo(1), "the preheader dominates the body");
  }
}
