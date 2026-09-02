using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DemandedBitsTests {

  [TestCase(IrBinaryOp.And, 0x123400FFL)]
  [TestCase(IrBinaryOp.Or, 0x7FFF0000L)]
  [TestCase(IrBinaryOp.Xor, 0x7FFF0000L)]
  public void Run_GivenOperationOnlyTouchesDiscardedBits_ThenTruncReadsOriginalValue(
      IrBinaryOp op, long constant) {
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = new IrFunction("f", IrType.I8, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var operation = builder.Binary(op, x, new IrConstantInt(IrType.I32, constant));
    var trunc = builder.Trunc(operation, IrType.I8);
    builder.Ret(trunc);

    var changes = DemandedBits.Run(function);

    Assert.That(changes, Is.EqualTo(1));
    Assert.That(trunc.Value, Is.SameAs(x));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void Run_GivenConstantOnLeft_ThenCommutativeDiscardedBitsStillDisappear() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = new IrFunction("f", IrType.I8, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var operation = builder.Or(new IrConstantInt(IrType.I32, 0x7FFF0000), x);
    var trunc = builder.Trunc(operation, IrType.I8);
    builder.Ret(trunc);

    Assert.That(DemandedBits.Run(function), Is.EqualTo(1));
    Assert.That(trunc.Value, Is.SameAs(x));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void Run_GivenOperationTouchesDemandedBit_ThenItIsPreserved() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = new IrFunction("f", IrType.I8, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var operation = builder.Xor(x, new IrConstantInt(IrType.I32, 1));
    var trunc = builder.Trunc(operation, IrType.I8);
    builder.Ret(trunc);

    Assert.That(DemandedBits.Run(function), Is.EqualTo(0));
    Assert.That(trunc.Value, Is.SameAs(operation));
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [TestCase(IrCastOp.ZExt)]
  [TestCase(IrCastOp.SExt)]
  public void Run_GivenExtensionImmediatelyTruncatedToSourceWidth_ThenRoundTripVanishes(IrCastOp extensionOp) {
    var x = new IrArgument(IrType.I8, 0, "x");
    var function = new IrFunction("f", IrType.I8, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var extension = builder.Cast(extensionOp, x, IrType.I32);
    var trunc = builder.Trunc(extension, IrType.I8);
    var ret = builder.Ret(trunc);

    Assert.That(DemandedBits.Run(function), Is.EqualTo(1));
    Assert.That(ret.Value, Is.SameAs(x));
    Assert.That(trunc.Parent, Is.Null);
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }

  [Test]
  public void Standard_GivenSpeedObjective_ThenDemandedBitsRunsInsideFixpointPipeline() {
    var x = new IrArgument(IrType.I32, 0, "x");
    var function = new IrFunction("f", IrType.I8, [x]);
    var builder = new IrBuilder(function.CreateBlock("entry"));
    var operation = builder.Or(x, new IrConstantInt(IrType.I32, 0x7FFF0000));
    builder.Ret(builder.Trunc(operation, IrType.I8));

    IrPassManager.Standard(optimizeForSpeed: true, includeModulePasses: false).RunToFixpoint(function);

    Assert.That(function.AllInstructions.OfType<IrBinary>(), Is.Empty);
    Assert.That(IrVerifier.Verify(function), Is.Empty);
  }
}
