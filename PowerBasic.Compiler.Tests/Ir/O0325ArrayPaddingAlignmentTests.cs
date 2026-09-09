using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class O0325ArrayPaddingAlignmentTests {

  [Test]
  public void GivenWholeVectorArray_WhenBaseAlignmentRuns_ThenItStillAlignsTheBase() {
    var index = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [index]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 20, Name = "a", IsSourceVariable = true });
    var element = entry.Append(new IrGep(array, index, IrType.I16));
    var value = entry.Append(new IrLoad(IrType.I16, element));
    entry.Append(new IrRet(value));

    Assert.That(ArrayBaseAlignment.Run(fn, vectorBytes: 8, pointerBits: 16), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var backing = entry.Instructions.OfType<IrAlloca>().Single(a => a.Name == "a.aligned.storage");
    var aligned = entry.Instructions.OfType<IrGep>()
      .Single(g => ReferenceEquals(g.BasePtr, backing) && g.ElementType is null);
    var adjustment = aligned.ByteOffset as IrBinary;

    Assert.Multiple(() => {
      Assert.That(backing.Allocated, Is.EqualTo(IrType.I16));
      Assert.That(backing.Count, Is.EqualTo(24), "one complete vector provides forward-alignment slack");
      Assert.That(backing.IsSourceVariable, Is.True);
      Assert.That(element.BasePtr, Is.SameAs(aligned));
      Assert.That(adjustment, Is.Not.Null);
      Assert.That(adjustment!.Op, Is.EqualTo(IrBinaryOp.And));
      Assert.That(adjustment.Rhs, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)adjustment.Rhs).Value, Is.EqualTo(7));
    });

    Assert.That(ArrayBaseAlignment.Run(fn, vectorBytes: 8, pointerBits: 16), Is.Zero,
      "the over-allocation marker must make the transform stable at fixpoint");
  }

  [Test]
  public void GivenUnpaddedArray_WhenBothO0325HalvesRun_ThenTailAndBaseAreCovered() {
    var index = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [index]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 17, Name = "a" });
    var element = entry.Append(new IrGep(array, index, IrType.I16));
    var value = entry.Append(new IrLoad(IrType.I16, element));
    entry.Append(new IrRet(value));

    Assert.That(ArrayPaddingAlignment.Run(fn, vectorBytes: 8), Is.EqualTo(1));
    Assert.That(ArrayBaseAlignment.Run(fn, vectorBytes: 8, pointerBits: 16), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var backing = entry.Instructions.OfType<IrAlloca>().Single(a => a.Name == "a.aligned.storage");
    Assert.That(backing.Count, Is.EqualTo(24), "20 logical padded elements plus one four-element vector of slack");
  }

  [Test]
  public void GivenTargetFacts_WhenStandardPipelineRuns_ThenO0325RemainsValidAtFixpoint() {
    var index = new IrArgument(IrType.I32, 0, "i");
    var fn = new IrFunction("f", IrType.I16, [index]);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 17, Name = "a" });
    var element = entry.Append(new IrGep(array, index, IrType.I16));
    var value = entry.Append(new IrLoad(IrType.I16, element));
    entry.Append(new IrRet(value));
    var passes = IrPassManager.Standard(
      optimizeForSpeed: false,
      includeModulePasses: false,
      dataLayoutTarget: new IrDataLayoutTarget(PointerBits: 16, VectorBytes: 8));
    passes.VerifyEachPass = true;

    Assert.That(passes.RunToFixpoint(fn), Is.GreaterThanOrEqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "a.aligned.storage"), Is.True);
  }

  [Test]
  public void GivenEscapedArray_WhenBaseAlignmentRuns_ThenLayoutIsNotChanged() {
    var fn = new IrFunction("f", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 20, Name = "a" });
    var sink = entry.Append(new IrAlloca(IrType.Ptr) { Name = "sink" });
    entry.Append(new IrStore(array, sink));
    entry.Append(new IrRet());

    Assert.That(ArrayBaseAlignment.Run(fn, vectorBytes: 8, pointerBits: 16), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "a.aligned.storage"), Is.False);
  }

  [TestCase(6, 16, TestName = "GivenNonPowerOfTwoVector_WhenAligning_ThenDecline")]
  [TestCase(32, 16, TestName = "GivenAlignmentWiderThanRealModeSegmentGuarantee_WhenAligning_ThenDecline")]
  [TestCase(8, 24, TestName = "GivenUnsupportedPointerWidth_WhenAligning_ThenDecline")]
  public void GivenUnsupportedTargetFacts_WhenBaseAlignmentRuns_ThenItDeclines(int vectorBytes, int pointerBits) {
    var fn = new IrFunction("f", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    entry.Append(new IrAlloca(IrType.I16) { Count = 20, Name = "a" });
    entry.Append(new IrRet());

    Assert.That(ArrayBaseAlignment.Run(fn, vectorBytes, pointerBits), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
