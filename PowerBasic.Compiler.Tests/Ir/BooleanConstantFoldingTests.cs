using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// BASIC's TRUE is <b>-1</b>, and a comparison the optimizer decides at compile time has to be that
/// same -1. Folding a sign-extension straight to the target width lost it: an <c>i1</c> constant holds
/// its raw 1, so <c>sext i1 1 to i16</c> folded to 1, and every constant-folded comparison used as a
/// VALUE - <c>PRINT a% = 50</c> - went out as 1 on the native, C and LLVM back ends alike.
///
/// The differential harness found this by running a battery program both ways and diffing the file it
/// wrote; no amount of reading the IR had. Sign-extension now goes through the source width first.
/// </summary>
[TestFixture]
public sealed class BooleanConstantFoldingTests {

  [Test]
  public void Fold_GivenSignExtendedTrue_ThenIsMinusOne() {
    var cast = new IrCast(IrCastOp.SExt, IrBuilder.ConstBool(true), IrType.I16);

    var folded = IrConstFold.TryFold(cast);

    Assert.That(folded, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(-1), "BASIC's TRUE is -1, not 1");
  }

  [Test]
  public void Fold_GivenSignExtendedFalse_ThenIsZero() {
    var folded = IrConstFold.TryFold(new IrCast(IrCastOp.SExt, IrBuilder.ConstBool(false), IrType.I16));

    Assert.That(((IrConstantInt)folded!).Value, Is.Zero);
  }

  [Test]
  public void Fold_GivenZeroExtendedTrue_ThenIsOne() {
    // ZExt is the unsigned widening and must NOT become -1 - only the signed one carries the sign
    var folded = IrConstFold.TryFold(new IrCast(IrCastOp.ZExt, IrBuilder.ConstBool(true), IrType.I16));

    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(1));
  }

  [TestCase(-1L)]
  [TestCase(32767L)]
  [TestCase(-32768L)]
  public void Fold_GivenAWiderSource_ThenTheValueIsUnchanged(long value) {
    // the fix must not disturb widening from a type whose values are already in range
    var folded = IrConstFold.TryFold(new IrCast(IrCastOp.SExt, new IrConstantInt(IrType.I16, value), IrType.I32));

    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(value));
  }
}
