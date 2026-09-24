using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Basic width-aware alias analysis over IR memory locations.</summary>
[TestFixture]
public sealed class AliasAnalysisTests {

  [Test]
  public void DistinctAllocas_AreNoAlias() {
    var first = new IrAlloca(IrType.I16);
    var second = new IrAlloca(IrType.I16);

    var result = IrAliasAnalysis.Alias(first, IrType.I16, second, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.NoAlias));
  }

  [Test]
  public void DistinctGlobals_AreNoAlias() {
    var first = new IrGlobalVariable("a", IrType.I16);
    var second = new IrGlobalVariable("b", IrType.I16);

    var result = IrAliasAnalysis.Alias(first, IrType.I16, second, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.NoAlias));
  }

  [Test]
  public void AdjacentTwoByteAccesses_AreNoAlias() {
    var storage = new IrAlloca(IrType.I16) { Count = 2 };
    var first = new IrGep(storage, IrBuilder.ConstI32(0));
    var second = new IrGep(storage, IrBuilder.ConstI32(2));

    var result = IrAliasAnalysis.Alias(first, IrType.I16, second, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.NoAlias));
  }

  [Test]
  public void OverlappingTwoByteAccesses_ArePartialAlias() {
    var storage = new IrAlloca(IrType.I16) { Count = 2 };
    var first = new IrGep(storage, IrBuilder.ConstI32(0));
    var second = new IrGep(storage, IrBuilder.ConstI32(1));

    var result = IrAliasAnalysis.Alias(first, IrType.I16, second, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.PartialAlias));
  }

  [Test]
  public void NestedConstantGeps_AreFlattened() {
    var storage = new IrAlloca(IrType.I16) { Count = 3 };
    var plusOne = new IrGep(storage, IrBuilder.ConstI32(1));
    var nested = new IrGep(plusOne, IrBuilder.ConstI32(1));
    var direct = new IrGep(storage, IrBuilder.ConstI32(2));

    var result = IrAliasAnalysis.Alias(nested, IrType.I16, direct, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.MustAlias));
  }

  [Test]
  public void ElementIndexedGep_UsesElementStorageWidth() {
    var storage = new IrAlloca(IrType.I16) { Count = 3 };
    var indexed = new IrGep(storage, IrBuilder.ConstI32(1), IrType.I16);
    var byteOffset = new IrGep(storage, IrBuilder.ConstI32(2));

    var result = IrAliasAnalysis.Alias(indexed, IrType.I16, byteOffset, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.MustAlias));
  }

  [Test]
  public void DynamicOffset_RemainsMayAlias() {
    var storage = new IrAlloca(IrType.I16) { Count = 3 };
    var index = new IrArgument(IrType.I32, 0, "i");
    var dynamicAddress = new IrGep(storage, index);
    var fixedAddress = new IrGep(storage, IrBuilder.ConstI32(2));

    var result = IrAliasAnalysis.Alias(dynamicAddress, IrType.I16, fixedAddress, IrType.I16);

    Assert.That(result, Is.EqualTo(IrAliasResult.MayAlias));
  }


  [Test]
  public void SharedPointerIdentity_PreservesRootAndOffsetAcrossBitcastsAndGeps() {
    var storage = new IrAlloca(IrType.I16) { Count = 4 };
    var plusTwo = new IrGep(storage, IrBuilder.ConstI32(2));
    var cast = new IrCast(IrCastOp.BitCast, plusTwo, IrType.Ptr);
    var direct = new IrGep(storage, IrBuilder.ConstI32(2));
    var identities = new IrPointerIdentityAnalysis();

    var identity = identities.TryResolve(cast);
    var alias = IrAliasAnalysis.Alias(cast, IrType.I16, direct, IrType.I16, identities);

    Assert.Multiple(() => {
      Assert.That(identity, Is.Not.Null);
      Assert.That(identity!.Value.Root, Is.SameAs(storage));
      Assert.That(identity.Value.ByteOffset, Is.EqualTo(2));
      Assert.That(identity.Value.IsUniqueObject, Is.True);
      Assert.That(alias, Is.EqualTo(IrAliasResult.MustAlias));
    });
  }

  [Test]
  public void SharedPointerIdentity_DistinctUniqueRootsStayNoAliasThroughBitcasts() {
    var first = new IrAlloca(IrType.I16);
    var second = new IrAlloca(IrType.I16);
    var firstCast = new IrCast(IrCastOp.BitCast, first, IrType.Ptr);
    var secondCast = new IrCast(IrCastOp.BitCast, second, IrType.Ptr);
    var identities = new IrPointerIdentityAnalysis();

    Assert.That(
      IrAliasAnalysis.Alias(firstCast, IrType.I16, secondCast, IrType.I16, identities),
      Is.EqualTo(IrAliasResult.NoAlias));
  }

  [Test]
  public void SharedPointerIdentity_DistinctByRefArgumentsRemainMayAlias() {
    var first = new IrArgument(IrType.Ptr, 0, "a");
    var second = new IrArgument(IrType.Ptr, 1, "b");
    var identities = new IrPointerIdentityAnalysis();

    Assert.Multiple(() => {
      Assert.That(identities.TryResolve(first)!.Value.IsUniqueObject, Is.False);
      Assert.That(identities.TryResolve(second)!.Value.IsUniqueObject, Is.False);
      Assert.That(
        IrAliasAnalysis.Alias(first, IrType.I16, second, IrType.I16, identities),
        Is.EqualTo(IrAliasResult.MayAlias),
        "separate pointer formals are not a noalias promise in PowerBASIC");
    });
  }

  [Test]
  public void WiderLaterStore_CanCompletelyOverwriteEarlierSubrange() {
    var storage = new IrAlloca(IrType.I16);
    var firstByte = new IrGep(storage, IrBuilder.ConstI32(0));
    var byteValue = new IrArgument(IrType.I8, 0, "b");
    var wordValue = new IrArgument(IrType.I16, 1, "w");
    var earlier = new IrStore(byteValue, firstByte);
    var later = new IrStore(wordValue, storage);

    var covers = IrAliasAnalysis.CompletelyOverwrites(later, earlier);

    Assert.That(covers, Is.True);
  }
}
