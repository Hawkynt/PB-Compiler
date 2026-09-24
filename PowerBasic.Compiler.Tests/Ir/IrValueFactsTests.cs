using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrValueFactsTests {

  [Test]
  public void FactsAt_ComposesRangeKnownBitsAndExplicitGuardNullness() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var function = new IrFunction("f", IrType.Void, [pointer]);
    var entry = function.CreateBlock("entry");
    var nonNull = function.CreateBlock("nonnull");
    var exit = function.CreateBlock("exit");

    var eb = new IrBuilder(entry);
    var guard = eb.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr(IrType.Ptr));
    eb.CondBr(guard, nonNull, exit);

    var nb = new IrBuilder(nonNull);
    var repeated = nb.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr(IrType.Ptr));
    nb.CondBr(repeated, exit, exit);
    new IrBuilder(exit).Ret();

    var analyses = new IrAnalysisManager(function);
    var facts = analyses.Get(IrAnalyses.Facts);
    var constant = new IrConstantInt(IrType.I16, 5);
    var range = facts.RangeAt(constant, nonNull);
    var bits = facts.KnownBits(constant);

    Assert.Multiple(() => {
      Assert.That(facts.NullnessAt(pointer, nonNull), Is.EqualTo(IrNullness.NonNull));
      Assert.That(facts.Decide(repeated, nonNull), Is.True);
      Assert.That(range.Lo, Is.EqualTo(5));
      Assert.That(range.Hi, Is.EqualTo(5));
      Assert.That(bits.AreOne(0b0101), Is.True);
      Assert.That(bits.AreZero(0b1010), Is.True);
    });
  }

  [Test]
  public void FactsAt_DoesNotInferNonNullFromDereference() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var function = new IrFunction("f", IrType.Void, [pointer]);
    var entry = function.CreateBlock("entry");
    var after = function.CreateBlock("after");

    var eb = new IrBuilder(entry);
    eb.Load(IrType.I16, pointer);
    eb.Br(after);
    new IrBuilder(after).Ret();

    var facts = new IrAnalysisManager(function).Get(IrAnalyses.Facts);

    Assert.That(facts.NullnessAt(pointer, after), Is.EqualTo(IrNullness.Unknown),
      "address zero can be readable on the DOS memory model; a dereference is not a nullness proof");
  }

  [Test]
  public void FactsAnalysis_DependsOnItsIndependentDomainsRatherThanOwningASecondLattice() {
    var function = new IrFunction("f", IrType.Void);
    new IrBuilder(function.CreateBlock("entry")).Ret();
    var analyses = new IrAnalysisManager(function);

    _ = analyses.Get(IrAnalyses.Facts);

    Assert.Multiple(() => {
      Assert.That(analyses.IsCached(IrAnalyses.Facts), Is.True);
      Assert.That(analyses.IsCached(IrAnalyses.Ranges), Is.True);
      Assert.That(analyses.IsCached(IrAnalyses.KnownBits), Is.True);
      Assert.That(analyses.IsCached(IrAnalyses.Nullness), Is.True);
      Assert.That(analyses.IsCached(IrAnalyses.Dominators), Is.True);
    });

    analyses.Invalidate(IrPreservedAnalyses.Preserve(IrAnalyses.Facts));

    Assert.That(analyses.IsCached(IrAnalyses.Facts), Is.False,
      "preserving the facade cannot keep it alive after one of its recorded prerequisites is invalidated");
  }
}
