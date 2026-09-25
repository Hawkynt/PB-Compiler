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
  public void AlignmentFacts_CombineDominatingGuardsWithUndominatedAssumptions() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var function = new IrFunction("f", IrType.Void, [pointer]);
    var entry = function.CreateBlock("entry");
    var aligned16 = function.CreateBlock("aligned16");
    var other = function.CreateBlock("other");
    var join = function.CreateBlock("join");

    var guardBits = entry.Append(new IrCast(IrCastOp.PtrToInt, pointer, IrType.U32));
    var guardLow = entry.Append(new IrBinary(
      IrBinaryOp.And, guardBits, new IrConstantInt(IrType.U32, 15)));
    var guard = entry.Append(new IrCmp(
      IrCmpPred.Eq, guardLow, new IrConstantInt(IrType.U32, 0)));
    entry.Append(new IrCondBr(guard, aligned16, other));

    aligned16.Append(new IrBr(join));
    other.Append(new IrBr(join));

    var candidateBits = join.Append(new IrCast(IrCastOp.PtrToInt, pointer, IrType.U32));
    var candidateLow = join.Append(new IrBinary(
      IrBinaryOp.And, candidateBits, new IrConstantInt(IrType.U32, 7)));
    var aligned8 = join.Append(new IrCmp(
      IrCmpPred.Eq, candidateLow, new IrConstantInt(IrType.U32, 0)));
    join.Append(new IrRet());

    var facts = new IrAnalysisManager(function).Get(IrAnalyses.Facts);

    Assert.Multiple(() => {
      Assert.That(facts.AlignmentAt(pointer, aligned16), Is.EqualTo(16));
      Assert.That(facts.AlignmentAt(pointer, other), Is.EqualTo(1));
      Assert.That(facts.AlignmentAt(pointer, join), Is.EqualTo(1),
        "the reconverged block has both contexts and cannot inherit either edge fact");
      Assert.That(facts.Decide(aligned8, join), Is.Null);
      Assert.That(facts.DecideAlignmentUnder(aligned8, guard, assumptionOutcome: true, entry), Is.True);
      Assert.That(facts.DecideAlignmentUnder(aligned8, guard, assumptionOutcome: false, entry), Is.Null,
        "not 16-byte aligned does not imply not 8-byte aligned");
    });
  }

  [Test]
  public void AlignmentFacts_RecognizeExplicitRoundUpPointerArithmeticAndConstantOffsets() {
    var function = new IrFunction("f", IrType.Void);
    var entry = function.CreateBlock("entry");

    var backing = entry.Append(new IrAlloca(IrType.I8) { Count = 64 });
    var bits = entry.Append(new IrCast(IrCastOp.PtrToInt, backing, IrType.U32));
    var negated = entry.Append(new IrBinary(
      IrBinaryOp.Sub, new IrConstantInt(IrType.U32, 0), bits));
    var adjustment = entry.Append(new IrBinary(
      IrBinaryOp.And, negated, new IrConstantInt(IrType.U32, 15)));
    var aligned = entry.Append(new IrGep(backing, adjustment));
    var shifted = entry.Append(new IrGep(aligned, new IrConstantInt(IrType.I32, 8)));
    entry.Append(new IrRet());

    var facts = new IrAnalysisManager(function).Get(IrAnalyses.Facts);

    Assert.Multiple(() => {
      Assert.That(facts.AlignmentAt(aligned, entry), Is.EqualTo(16),
        "the ordinary IR round-up idiom must carry its alignment fact without target metadata");
      Assert.That(facts.AlignmentAt(shifted, entry), Is.EqualTo(8),
        "a constant byte displacement keeps only the common power-of-two divisor");
    });
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
      Assert.That(analyses.IsCached(IrAnalyses.Alignment), Is.True);
      Assert.That(analyses.IsCached(IrAnalyses.Dominators), Is.True);
    });

    analyses.Invalidate(IrPreservedAnalyses.Preserve(IrAnalyses.Facts));

    Assert.That(analyses.IsCached(IrAnalyses.Facts), Is.False,
      "preserving the facade cannot keep it alive after one of its recorded prerequisites is invalidated");
  }
}
