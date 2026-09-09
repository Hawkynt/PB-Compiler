using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0296 — ownership-safe dynamic-string copy-to-move conversion.</summary>
[TestFixture]
public sealed class StringMoveTests {

  [Test]
  public void DeadPrivateSource_GivenAssignmentCopy_ThenHandleIsMovedAndSourceIsCleared() {
    var fixture = BuildStraightLine();

    Assert.That(StringMove.Run(fixture.Function), Is.EqualTo(1));

    var transferAt = IndexIn(fixture.Entry, fixture.Transfer);
    Assert.Multiple(() => {
      Assert.That(fixture.Duplicate.Parent, Is.Null);
      Assert.That(fixture.Transfer.Value, Is.SameAs(fixture.SourceRead));
      Assert.That(transferAt, Is.GreaterThanOrEqualTo(0));
      Assert.That(fixture.Entry.Instructions[transferAt + 1], Is.TypeOf<IrStore>());
      var clear = (IrStore)fixture.Entry.Instructions[transferAt + 1];
      Assert.That(clear.Pointer, Is.SameAs(fixture.Source));
      Assert.That(clear.Value, Is.TypeOf<IrNullPtr>());
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
      Assert.That(StringMove.Run(fixture.Function), Is.Zero, "the ownership rewrite must be a fixpoint");
    });
  }

  [Test]
  public void SourceReadAfterAssignment_GivenTheOldValueIsStillObserved_ThenCopyRemains() {
    var fixture = BuildStraightLine(beforeSourceDestructor: (entry, source) => {
      var observed = entry.Append(new IrLoad(IrType.Ptr, source));
      entry.Append(new IrCall(IrType.Void, Runtime("observe", IrType.Void, IrType.Ptr), [observed]));
    });

    Assert.That(StringMove.Run(fixture.Function), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(fixture.Duplicate.Parent, Is.SameAs(fixture.Entry));
      Assert.That(fixture.Transfer.Value, Is.SameAs(fixture.Duplicate));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void EscapedSourceSlot_GivenItsAddressCanBeReachedByACall_ThenCopyRemains() {
    var fixture = BuildStraightLine(beforeSourceDestructor: (entry, source) =>
      entry.Append(new IrCall(IrType.Void, Runtime("touch_byref", IrType.Void, IrType.Ptr), [source])));

    Assert.That(StringMove.Run(fixture.Function), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(fixture.Duplicate.Parent, Is.SameAs(fixture.Entry));
      Assert.That(fixture.Transfer.Value, Is.SameAs(fixture.Duplicate));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void DeadSourceAcrossDiamond_GivenEveryPathDestroysIt_ThenMoveIsAllowed() {
    var duplicateFn = Runtime("rt_str_dup", IrType.Ptr, IrType.Ptr);
    var freeFn = Runtime("rt_str_free", IrType.Void, IrType.Ptr);
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("diamond", IrType.Void, [condition]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var left = fn.AddBlock(new IrBasicBlock("left"));
    var right = fn.AddBlock(new IrBasicBlock("right"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    var source = entry.Append(new IrAlloca(IrType.Ptr) { Name = "source", IsSourceVariable = true });
    var target = entry.Append(new IrAlloca(IrType.Ptr) { Name = "target", IsSourceVariable = true });
    entry.Append(new IrStore(new IrNullPtr(), source));
    entry.Append(new IrStore(new IrNullPtr(), target));
    var sourceRead = entry.Append(new IrLoad(IrType.Ptr, source));
    var duplicate = entry.Append(new IrCall(IrType.Ptr, duplicateFn, [sourceRead]));
    var transfer = entry.Append(new IrStore(duplicate, target));
    entry.Append(new IrCondBr(condition, left, right));
    Destroy(left, source, freeFn);
    left.Append(new IrBr(exit));
    Destroy(right, source, freeFn);
    right.Append(new IrBr(exit));
    Destroy(exit, target, freeFn);
    exit.Append(new IrRet());

    Assert.That(StringMove.Run(fn), Is.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(duplicate.Parent, Is.Null);
      Assert.That(transfer.Value, Is.SameAs(sourceRead));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void Mem2Reg_GivenADeadStringAssignmentSource_ThenO0296RunsBeforePromotion() {
    var fixture = BuildStraightLine();

    _ = Mem2Reg.Run(fixture.Function);

    Assert.Multiple(() => {
      Assert.That(fixture.Function.AllInstructions.OfType<IrCall>().Any(call =>
        call.Callee is IrFunction { Name: "rt_str_dup" }), Is.False);
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  private static Fixture BuildStraightLine(Action<IrBasicBlock, IrAlloca>? beforeSourceDestructor = null) {
    var duplicateFn = Runtime("rt_str_dup", IrType.Ptr, IrType.Ptr);
    var freeFn = Runtime("rt_str_free", IrType.Void, IrType.Ptr);
    var fn = new IrFunction("move", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var source = entry.Append(new IrAlloca(IrType.Ptr) { Name = "source", IsSourceVariable = true });
    var target = entry.Append(new IrAlloca(IrType.Ptr) { Name = "target", IsSourceVariable = true });
    entry.Append(new IrStore(new IrNullPtr(), source));
    entry.Append(new IrStore(new IrNullPtr(), target));
    var sourceRead = entry.Append(new IrLoad(IrType.Ptr, source));
    var duplicate = entry.Append(new IrCall(IrType.Ptr, duplicateFn, [sourceRead]));
    var transfer = entry.Append(new IrStore(duplicate, target));
    beforeSourceDestructor?.Invoke(entry, source);
    Destroy(entry, source, freeFn);
    Destroy(entry, target, freeFn);
    entry.Append(new IrRet());
    return new(fn, entry, source, sourceRead, duplicate, transfer);
  }

  private static void Destroy(IrBasicBlock block, IrAlloca slot, IrFunction free) {
    var value = block.Append(new IrLoad(IrType.Ptr, slot));
    block.Append(new IrCall(IrType.Void, free, [value]));
  }

  private static IrFunction Runtime(string name, IrType returnType, params IrType[] parameters)
    => new(name, returnType, parameters.Select((type, index) => new IrArgument(type, index)));

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }

  private sealed record Fixture(
    IrFunction Function,
    IrBasicBlock Entry,
    IrAlloca Source,
    IrLoad SourceRead,
    IrCall Duplicate,
    IrStore Transfer);
}
