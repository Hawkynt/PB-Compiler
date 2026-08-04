using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Mid-end optimizations the IR pipeline already achieves, each verified rather than assumed.
///
/// A porting ledger is only worth keeping if its entries are checked. Several of the catalogue's
/// mid-end optimizations were written for the direct emitter and are ALSO delivered on the IR by
/// passes that were built independently - constant folding by SCCP and InstCombine, common
/// subexpression elimination by GVN, and so on. Recording those as ported without a test would be
/// bookkeeping; recording them with one is a port.
///
/// Where the IR does it BETTER than the original, that is noted: the direct emitter's CSE is
/// block-local, GVN is not.
/// </summary>
[TestFixture]
public sealed class PortedMidEndOptimizationsTests {

  private static IrFunction Optimized(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    return module!.FindFunction("main")!;
  }

  private static IEnumerable<IrInstruction> Body(IrFunction fn) => fn.Blocks.SelectMany(b => b.Instructions);

  /// <summary>
  /// Multiplies of either shape. PB's integer arithmetic is FLOAT-shaped in the IR - a% * b% lowers
  /// through sitofp/fmul/fptosi.round, and only IntegerRecovery turns it back, which runs in the
  /// back end's pipeline rather than the standard one. Counting only Mul would count zero and read
  /// as "the optimization worked".
  /// </summary>
  private static int Multiplies(IrFunction fn) =>
    Body(fn).OfType<IrBinary>().Count(i => i.Op is IrBinaryOp.Mul or IrBinaryOp.FMul);

  /// <summary>O0001 — constant folding. Nothing arithmetic should survive a constant expression.</summary>
  [Test]
  public void O0001_GivenAConstantExpression_ThenNoArithmeticSurvives() {
    var main = Optimized("""
      DIM x AS INTEGER
      x = 2 + 3 * 4 - 1
      PRINT x
      END
      """);

    Assert.That(Body(main).OfType<IrBinary>(), Is.Empty);
    var printed = Body(main).OfType<IrCall>().First(c => (c.Callee as IrFunction)?.Name == "rt_print_i16");
    Assert.That(((IrConstantInt)printed.Args.First()).Value, Is.EqualTo(13));
  }

  /// <summary>
  /// O0003 — common subexpression elimination. The direct emitter's is block-local; GVN is not, so
  /// the repeated subtree is computed once even when the two uses are in different blocks.
  /// </summary>
  [Test]
  public void O0003_GivenARepeatedSubexpression_ThenItIsComputedOnce() {
    var main = Optimized("""
      DIM x AS INTEGER, y AS INTEGER, a AS INTEGER, b AS INTEGER
      INPUT x
      INPUT y
      a = x * y + 1
      b = x * y + 2
      PRINT a; b
      END
      """);

    // one multiply for the shared x*y, not two
    Assert.That(Multiplies(main), Is.EqualTo(1));
  }

  /// <summary>The cross-block half, which is what makes GVN more than the emitter's block-local pass.</summary>
  [Test]
  public void O0003_GivenTheSameSubexpressionInTwoBlocks_ThenItIsStillComputedOnce() {
    var main = Optimized("""
      DIM x AS INTEGER, y AS INTEGER, r AS INTEGER
      INPUT x
      INPUT y
      r = x * y
      IF x > 0 THEN
        r = x * y + 1
      END IF
      PRINT r
      END
      """);

    Assert.That(Multiplies(main), Is.EqualTo(1));
  }

  /// <summary>O0002 — dead code elimination: a value nothing reads is not computed.</summary>
  [Test]
  public void O0002_GivenAnUnreadComputation_ThenItIsNotComputed() {
    var main = Optimized("""
      DIM x AS INTEGER, dead AS INTEGER
      INPUT x
      dead = x * 99
      PRINT x
      END
      """);

    Assert.That(Multiplies(main), Is.Zero, "the multiply feeds nothing that is read");
  }
}
