using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrKnownBitsAnalysisTests {

  [Test]
  public void For_GivenBitwiseComposition_ThenTracksKnownZeroAndOneBits() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = new IrFunction("f", IrType.I32, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var lowZero = builder.And(x, new IrConstantInt(IrType.I32, unchecked((int)0xFFFFFF00u)));
    var lowOne = builder.Or(x, new IrConstantInt(IrType.I32, 0xFF));
    builder.Ret(lowZero);
    var analysis = new IrKnownBitsAnalysis(function);

    Assert.Multiple(() => {
      Assert.That(analysis.For(lowZero).AreZero(0xFF), Is.True);
      Assert.That(analysis.For(lowOne).AreOne(0xFF), Is.True);
    });
  }

  [Test]
  public void For_GivenZeroExtension_ThenHighBitsAreKnownZero() {
    var x = new IrArgument(IrType.I8, 0, "x");
    var function = new IrFunction("f", IrType.I32, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var extended = builder.ZExt(x, IrType.I32);
    builder.Ret(extended);

    var bits = new IrKnownBitsAnalysis(function).For(extended);

    Assert.That(bits.AreZero(0xFFFFFF00), Is.True);
  }

  [TestCase(IrBinaryOp.Or)]
  [TestCase(IrBinaryOp.Xor)]
  public void DemandedBits_GivenDerivedOperandKnownZeroInDiscardedWidth_ThenDropsOuterOperation(IrBinaryOp op) {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var function = new IrFunction("f", IrType.I8, [x, y]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var lowZero = builder.And(y, new IrConstantInt(IrType.I32, unchecked((int)0xFFFFFF00u)));
    var operation = builder.Binary(op, x, lowZero);
    var trunc = builder.Trunc(operation, IrType.I8);
    builder.Ret(trunc);

    var changes = DemandedBits.Run(function, new IrAnalysisManager(function)).Changes;

    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(trunc.Value, Is.SameAs(x));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void DemandedBits_GivenDerivedOperandKnownOneInDemandedWidth_ThenDropsOuterAnd() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var y = new IrArgument(IrType.I32, 1, "y");
    var function = new IrFunction("f", IrType.I8, [x, y]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var lowOne = builder.Or(y, new IrConstantInt(IrType.I32, 0xFF));
    var operation = builder.And(x, lowOne);
    var trunc = builder.Trunc(operation, IrType.I8);
    builder.Ret(trunc);

    var changes = DemandedBits.Run(function, new IrAnalysisManager(function)).Changes;

    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(trunc.Value, Is.SameAs(x));
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }
}
