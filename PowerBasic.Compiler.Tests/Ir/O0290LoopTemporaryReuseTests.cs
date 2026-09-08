using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0290 loop-temporary reuse: allocation lifetime, escape and fresh-zero proofs.</summary>
[TestFixture]
public sealed class O0290LoopTemporaryReuseTests {

  [Test]
  public void Run_GivenPrivateFullyInitializedArrayHeapTemporary_ThenAllocatesOnceAroundTheLoop() {
    var fixture = BuildLoop(readBeforeWrite: false, escape: false, allocationBytes: 2, readType: IrType.I16);

    var changed = LoopTemporaryReuse.Run(fixture.Function);

    Assert.Multiple(() => {
      Assert.That(changed, Is.EqualTo(1));
      Assert.That(fixture.Allocation.Parent, Is.SameAs(fixture.Preheader));
      Assert.That(fixture.Free.Parent, Is.SameAs(fixture.Exit));
      Assert.That(fixture.Body.Instructions.OfType<IrCall>(), Is.Empty);
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenReadBeforeOverwrite_ThenFreshZeroSemanticsPreventReuse() {
    var fixture = BuildLoop(readBeforeWrite: true, escape: false, allocationBytes: 2, readType: IrType.I16);

    Assert.Multiple(() => {
      Assert.That(LoopTemporaryReuse.Run(fixture.Function), Is.Zero);
      Assert.That(fixture.Allocation.Parent, Is.SameAs(fixture.Body));
      Assert.That(fixture.Free.Parent, Is.SameAs(fixture.Body));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenOnlyPartOfAReadIsOverwritten_ThenFreshZeroSemanticsPreventReuse() {
    var fixture = BuildLoop(readBeforeWrite: false, escape: false, allocationBytes: 4, readType: IrType.I32,
      storedType: IrType.I16);

    Assert.That(LoopTemporaryReuse.Run(fixture.Function), Is.Zero);
    Assert.That(fixture.Allocation.Parent, Is.SameAs(fixture.Body));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void Run_GivenPointerPassedToAnotherCall_ThenTheTemporaryIsNotProvenPrivate() {
    var fixture = BuildLoop(readBeforeWrite: false, escape: true, allocationBytes: 2, readType: IrType.I16);

    Assert.Multiple(() => {
      Assert.That(LoopTemporaryReuse.Run(fixture.Function), Is.Zero);
      Assert.That(fixture.Allocation.Parent, Is.SameAs(fixture.Body));
      Assert.That(fixture.Free.Parent, Is.SameAs(fixture.Body));
      Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    });
  }

  private static Fixture BuildLoop(bool readBeforeWrite, bool escape, int allocationBytes, IrType readType,
      IrType? storedType = null) {
    storedType ??= readType;
    var allocate = new IrFunction("rt_arr_alloc", IrType.FarPtr, [new IrArgument(IrType.I32, 0)]);
    var free = new IrFunction("rt_arr_free", IrType.Void,
      [new IrArgument(IrType.FarPtr, 0), new IrArgument(IrType.I32, 1)]);
    var observe = new IrFunction("observe", IrType.Void, [new IrArgument(IrType.FarPtr, 0)]);
    var function = new IrFunction("f", IrType.Void);
    var preheader = function.CreateBlock("preheader");
    var header = function.CreateBlock("loop.header");
    var body = function.CreateBlock("loop.body");
    var exit = function.CreateBlock("exit");

    new IrBuilder(preheader).Br(header);
    var bh = new IrBuilder(header);
    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 1), preheader);
    var test = bh.Cmp(IrCmpPred.Sle, counter, new IrConstantInt(IrType.I16, 4));
    bh.CondBr(test, body, exit);

    var bb = new IrBuilder(body);
    var allocation = bb.Call(IrType.FarPtr, allocate, new IrConstantInt(IrType.I32, allocationBytes));
    if (escape)
      bb.Call(IrType.Void, observe, allocation);
    if (readBeforeWrite)
      bb.Load(readType, allocation);
    bb.Store(ValueFor(storedType), allocation);
    if (!readBeforeWrite)
      bb.Load(readType, allocation);
    var next = bb.Add(counter, new IrConstantInt(IrType.I16, 1));
    var release = bb.Call(IrType.Void, free, allocation, new IrConstantInt(IrType.I32, allocationBytes));
    bb.Br(header);
    counter.AddIncoming(next, body);
    new IrBuilder(exit).Ret();

    return new(function, preheader, body, exit, allocation, release);
  }

  private static IrValue ValueFor(IrType type) => type switch {
    { IsInteger: true } => new IrConstantInt(type, 7),
    _ => throw new ArgumentException("the test helper only needs integer stores", nameof(type)),
  };

  private sealed record Fixture(IrFunction Function, IrBasicBlock Preheader, IrBasicBlock Body,
    IrBasicBlock Exit, IrCall Allocation, IrCall Free);
}
