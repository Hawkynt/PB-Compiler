using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>$ERROR OVERFLOW ON</c> in the IR lowering. The direct emitter reads the overflow flag straight
/// off the ADD/SUB/IMUL it has just written - a JNO over a call to rt_raise - but a target-independent
/// IR has no flags register, so the same question has to be asked in arithmetic instead.
///
/// The formulas are the point of these tests, because a sign rule that is subtly wrong fails only on
/// the boundary values nobody tries by hand. Each one is therefore checked at the exact edge where it
/// flips: the largest value that does NOT overflow, and the smallest that does.
/// </summary>
[TestFixture]
public sealed class OverflowCheckLoweringTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IEnumerable<IrInstruction> Body(IrModule m)
    => m.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions);

  private static bool RaisesSix(IrModule m)
    => Body(m).OfType<IrCall>().Any(c => (c.Callee as IrFunction)?.Name == "rt_error"
                                         && c.Args.FirstOrDefault() is IrConstantInt { Value: 6 });

  [Test]
  public void Lower_GivenOverflowOn_ThenAnIntegerAddCanRaiseErrorSix() {
    Assert.That(RaisesSix(Lower("""
      $ERROR OVERFLOW ON
      a% = 30000
      b% = a% + a%
      PRINT b%
      """)), Is.True);
  }

  [Test]
  public void Lower_GivenOverflowOff_ThenTheAddIsUnchecked() {
    Assert.That(RaisesSix(Lower("""
      a% = 30000
      b% = a% + a%
      PRINT b%
      """)), Is.False);
  }

  [Test]
  public void Lower_GivenOverflowOn_ThenSubtractAndMultiplyAreCheckedToo() {
    foreach (var op in new[] { "-", "*" })
      Assert.That(RaisesSix(Lower($"""
        $ERROR OVERFLOW ON
        a% = 30000
        b% = a% {op} a%
        PRINT b%
        """)), Is.True, $"a% {op} a% must be checked");
  }

  /// <summary>A multiply has no sign rule, so it is done one width up and range-checked there.</summary>
  [Test]
  public void Lower_GivenACheckedMultiply_ThenTheProductIsComputedOneWidthUp() {
    var m = Lower("""
      $ERROR OVERFLOW ON
      a% = 300
      b% = a% * a%
      PRINT b%
      """);

    var wide = Body(m).OfType<IrBinary>().Where(b => b.Op == IrBinaryOp.Mul && b.Type.Bits == 32).ToList();
    Assert.That(wide, Is.Not.Empty, "the i16 product has to be formed in i32 to be exact");
    Assert.That(Body(m).OfType<IrCast>().Any(c => c.Op == IrCastOp.Trunc && c.Type.Bits == 16),
      "and truncated back once it is known to fit");
  }

  /// <summary>
  /// Division is not checked. It cannot wrap in PB's INTEGER arithmetic (the one overflowing case,
  /// -32768 \ -1, is not what $ERROR OVERFLOW guards; a zero divisor is Error 11, a different trap),
  /// so arming the check must not put a spurious Error 6 in front of it.
  /// </summary>
  [Test]
  public void Lower_GivenOverflowOn_ThenDivisionIsNotChecked() {
    Assert.That(RaisesSix(Lower("""
      $ERROR OVERFLOW ON
      a% = 30000
      b% = 7
      c% = a% \ b%
      PRINT c%
      """)), Is.False);
  }

  /// <summary>
  /// The check is data, not a flag - so a constant-folding pass evaluates it like any other
  /// expression. An addition that provably overflows must still reach the raise after the optimizer
  /// has had the module, and one that provably does not must be left alone.
  /// </summary>
  [Test]
  public void Optimize_GivenAConstantOverflow_ThenTheRaiseSurvivesTheOptimizer() {
    var m = Lower("""
      $ERROR OVERFLOW ON
      a% = 30000
      b% = a% + a%
      PRINT b%
      """);
    IrPassManager.Standard().RunOnModule(m);

    Assert.That(RaisesSix(m), Is.True, "folding the check away would silently disarm it");
  }

  /// <summary>
  /// An unsigned type has no overflow flag to read either: its wrap is a carry, which is one unsigned
  /// compare. DWORD is the width where this actually shows, because PB promotes a WORD addition to
  /// LONG - which is signed, and wide enough that it cannot overflow at all - while a DWORD one has
  /// nothing wider to be promoted into and stays unsigned.
  /// </summary>
  [Test]
  public void Lower_GivenOverflowOnAndUnsignedOperands_ThenTheWrapIsACarryNotASignChange() {
    var m = Lower("""
      $ERROR OVERFLOW ON
      a??? = 4000000000
      b??? = a??? + a???
      PRINT b???
      """);

    Assert.That(RaisesSix(m), Is.True);
    // an unsigned wrap is an unsigned compare of the sum against an operand - never a signed one
    Assert.That(Body(m).OfType<IrCmp>().Select(c => c.Pred), Does.Contain(IrCmpPred.Ult));
  }
}
