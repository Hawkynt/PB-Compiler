using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0285 — whole-module merging of provably private read-only byte blobs.</summary>
[TestFixture]
public sealed class ConstantDataMergingTests {

  [Test]
  public void ExactDuplicate_GivenReadOnlyByteBlobs_ThenOneGlobalIsReused() {
    var module = new IrModule("test");
    var first = Blob(module, "first", [1, 2, 3, 4]);
    var second = Blob(module, "second", [1, 2, 3, 4]);
    var firstAccess = Read(module, first, "read_first");
    var secondAccess = Read(module, second, "read_second");

    Assert.That(ConstantDataMerging.Run(module), Is.EqualTo(1));

    var kept = module.FindGlobal("first");
    Assert.Multiple(() => {
      Assert.That(kept, Is.SameAs(first));
      Assert.That(module.FindGlobal("second"), Is.Null);
      Assert.That(firstAccess.BasePtr, Is.SameAs(kept));
      Assert.That(secondAccess.BasePtr, Is.SameAs(kept));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void ContainedBlob_GivenAReadOnlySlice_ThenItsUsersAreRebasedIntoTheLargerBlob() {
    var module = new IrModule("test");
    var large = Blob(module, "large", [10, 20, 30, 40, 50]);
    var small = Blob(module, "small", [30, 40]);
    _ = Read(module, large, "read_large");
    var smallAccess = Read(module, small, "read_small");

    Assert.That(ConstantDataMerging.Run(module), Is.EqualTo(1));

    Assert.That(smallAccess.BasePtr, Is.TypeOf<IrGep>());
    var rebased = (IrGep)smallAccess.BasePtr;
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("large"), Is.SameAs(large));
      Assert.That(module.FindGlobal("small"), Is.Null);
      Assert.That(rebased.BasePtr, Is.SameAs(large));
      Assert.That(rebased.ByteOffset, Is.TypeOf<IrConstantInt>());
      Assert.That(((IrConstantInt)rebased.ByteOffset).Value, Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void PrefixSuffixOverlap_GivenTwoReadOnlyBlobs_ThenTheirSharedTailAndHeadAreStoredOnce() {
    var module = new IrModule("test");
    var left = Blob(module, "left", [1, 2, 3, 4]);
    var right = Blob(module, "right", [3, 4, 5, 6]);
    var leftAccess = Read(module, left, "read_left");
    var rightAccess = Read(module, right, "read_right");

    Assert.That(ConstantDataMerging.Run(module), Is.EqualTo(1));

    var merged = module.FindGlobal("left");
    Assert.That(merged, Is.Not.Null);
    Assert.That(rightAccess.BasePtr, Is.TypeOf<IrGep>());
    var rebased = (IrGep)rightAccess.BasePtr;
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("right"), Is.Null);
      Assert.That(merged!.Bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
      Assert.That(leftAccess.BasePtr, Is.SameAs(merged));
      Assert.That(rebased.BasePtr, Is.SameAs(merged));
      Assert.That(((IrConstantInt)rebased.ByteOffset).Value, Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void AddressIdentity_GivenAComparisonOfBlobPointers_ThenStorageRemainsDistinct() {
    var module = new IrModule("test");
    var first = Blob(module, "first", [7, 8, 9]);
    var second = Blob(module, "second", [7, 8, 9]);
    var fn = module.AddFunction(new IrFunction("same_address", IrType.I1));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var comparison = entry.Append(new IrCmp(IrCmpPred.Eq, first, second));
    entry.Append(new IrRet(comparison));

    Assert.That(ConstantDataMerging.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("first"), Is.SameAs(first));
      Assert.That(module.FindGlobal("second"), Is.SameAs(second));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void WritableAlias_GivenAStoreThroughTheBlobPointer_ThenStorageIsNotMerged() {
    var module = new IrModule("test");
    var writable = Blob(module, "writable", [1, 2, 3]);
    var duplicate = Blob(module, "duplicate", [1, 2, 3]);
    var fn = module.AddFunction(new IrFunction("write", IrType.Void));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(writable, new IrConstantInt(IrType.I32, 1)));
    entry.Append(new IrStore(new IrConstantInt(IrType.I8, 9), at));
    entry.Append(new IrRet());
    _ = Read(module, duplicate, "read_duplicate");

    Assert.That(ConstantDataMerging.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("writable"), Is.SameAs(writable));
      Assert.That(module.FindGlobal("duplicate"), Is.SameAs(duplicate));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void InlineAssembly_GivenOtherwiseMergeableBlobs_ThenTheModuleIsLeftUntouched() {
    var module = new IrModule("test");
    var first = Blob(module, "first", [1, 2, 3]);
    var second = Blob(module, "second", [1, 2, 3]);
    _ = Read(module, first, "read_first");
    _ = Read(module, second, "read_second");
    module.AddFunction(new IrFunction("opaque", IrType.Void) { HasInlineAsm = true });

    Assert.That(ConstantDataMerging.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("first"), Is.SameAs(first));
      Assert.That(module.FindGlobal("second"), Is.SameAs(second));
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void EqualChoices_GivenDifferentInsertionOrder_ThenTheSameSymbolAndBytesWin() {
    var forward = BuildDeterminismCase(reverse: false);
    var reverse = BuildDeterminismCase(reverse: true);

    Assert.That(ConstantDataMerging.Run(forward), Is.EqualTo(2));
    Assert.That(ConstantDataMerging.Run(reverse), Is.EqualTo(2));

    var forwardPool = forward.Globals.Single(global => global.Bytes is not null);
    var reversePool = reverse.Globals.Single(global => global.Bytes is not null);
    Assert.Multiple(() => {
      Assert.That(forwardPool.Name, Is.EqualTo(reversePool.Name));
      Assert.That(forwardPool.Bytes, Is.EqualTo(reversePool.Bytes));
      Assert.That(IrVerifier.Verify(forward), Is.Empty);
      Assert.That(IrVerifier.Verify(reverse), Is.Empty);
    });
  }

  private static IrModule BuildDeterminismCase(bool reverse) {
    var module = new IrModule("test");
    var specs = new (string Name, byte[] Bytes)[] {
      ("a", [1, 2, 3, 4]),
      ("b", [3, 4, 5, 6]),
      ("c", [5, 6, 7, 8]),
    };
    if (reverse)
      Array.Reverse(specs);
    foreach (var (name, bytes) in specs) {
      var blob = Blob(module, name, bytes);
      _ = Read(module, blob, "read_" + name);
    }
    return module;
  }

  private static IrGlobalVariable Blob(IrModule module, string name, byte[] bytes)
    => module.AddGlobal(new IrGlobalVariable(name, IrType.I8) {
      Bytes = bytes,
      Count = bytes.Length,
      IsZeroInitialized = false,
    });

  private static IrGep Read(IrModule module, IrGlobalVariable blob, string functionName) {
    var fn = module.AddFunction(new IrFunction(functionName, IrType.I8));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(blob, new IrConstantInt(IrType.I32, 0)));
    var value = entry.Append(new IrLoad(IrType.I8, at));
    entry.Append(new IrRet(value));
    return at;
  }
}
