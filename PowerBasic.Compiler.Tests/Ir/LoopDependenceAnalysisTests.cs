using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class LoopDependenceAnalysisTests {

  private static IrLoopDependenceInfo AnalyzeLoop(
      Action<IrBuilder, IrPhi, IrAlloca, IrAlloca> emit,
      long start = 0,
      long limit = 8,
      long step = 1) {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var head = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    var first = entry.Append(new IrAlloca(IrType.I8) { Count = 256 });
    var second = entry.Append(new IrAlloca(IrType.I8) { Count = 256 });
    entry.Append(new IrBr(head));

    var counter = head.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var predicate = step > 0 ? IrCmpPred.Slt : IrCmpPred.Sgt;
    var test = head.Append(new IrCmp(predicate, counter, new IrConstantInt(IrType.I16, limit)));
    head.Append(new IrCondBr(test, body, exit));

    var builder = new IrBuilder(body);
    emit(builder, counter, first, second);
    var next = builder.Add(counter, new IrConstantInt(IrType.I16, step));
    builder.Br(head);
    new IrBuilder(exit).Ret();

    counter.AddIncoming(new IrConstantInt(IrType.I16, start), entry);
    counter.AddIncoming(next, body);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    return IrLoopDependenceAnalysis.Analyze(fn, head)!;
  }

  private static IrValue Address(IrBuilder builder, IrValue storage, IrPhi counter, long scale = 1, long offset = 0) {
    IrValue index = counter;
    if (scale != 1)
      index = builder.Mul(index, new IrConstantInt(IrType.I16, scale));
    if (offset != 0)
      index = builder.Add(index, new IrConstantInt(IrType.I16, offset));
    return builder.Gep(storage, index);
  }

  [Test]
  public void DistinctArrays_AreProvenIndependent() {
    var info = AnalyzeLoop((builder, i, first, second) => {
      builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i));
      builder.Load(IrType.I8, Address(builder, second, i));
    });

    Assert.That(info.IsComplete, Is.True);
    Assert.That(info.Dependences, Is.Empty);
    Assert.That(info.HasLoopCarriedDependence, Is.False);
  }

  [Test]
  public void PreviousElementRead_IsDistanceOneFlowDependence() {
    IrLoad? load = null;
    IrStore? store = null;
    var info = AnalyzeLoop((builder, i, first, _) => {
      load = builder.Load(IrType.I8, Address(builder, first, i, offset: -1));
      store = builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i));
    }, start: 1, limit: 8);

    Assert.That(info.IsComplete, Is.True);
    var dependence = info.Dependences.Single();
    Assert.That(dependence.Kind, Is.EqualTo(IrDependenceKind.Flow));
    Assert.That(dependence.Direction, Is.EqualTo(IrDependenceDirection.Less));
    Assert.That(dependence.Distance, Is.EqualTo(1));
    Assert.That(dependence.Source.Instruction, Is.SameAs(store));
    Assert.That(dependence.Sink.Instruction, Is.SameAs(load));
  }

  [Test]
  public void NextElementRead_IsDistanceOneAntiDependence() {
    IrLoad? load = null;
    IrStore? store = null;
    var info = AnalyzeLoop((builder, i, first, _) => {
      load = builder.Load(IrType.I8, Address(builder, first, i, offset: 1));
      store = builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i));
    }, limit: 7);

    Assert.That(info.IsComplete, Is.True);
    var dependence = info.Dependences.Single();
    Assert.That(dependence.Kind, Is.EqualTo(IrDependenceKind.Anti));
    Assert.That(dependence.Direction, Is.EqualTo(IrDependenceDirection.Less));
    Assert.That(dependence.Distance, Is.EqualTo(1));
    Assert.That(dependence.Source.Instruction, Is.SameAs(load));
    Assert.That(dependence.Sink.Instruction, Is.SameAs(store));
  }

  [Test]
  public void SameElementReadThenWrite_IsOnlyEqualDirection() {
    var info = AnalyzeLoop((builder, i, first, _) => {
      builder.Load(IrType.I8, Address(builder, first, i));
      builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i));
    });

    Assert.That(info.IsComplete, Is.True);
    var dependence = info.Dependences.Single();
    Assert.That(dependence.Kind, Is.EqualTo(IrDependenceKind.Anti));
    Assert.That(dependence.Direction, Is.EqualTo(IrDependenceDirection.Equal));
    Assert.That(dependence.Distance, Is.Zero);
    Assert.That(info.HasLoopCarriedDependence, Is.False);
  }

  [Test]
  public void ByteStrideWordStore_SelfOverlapsNextIteration() {
    var info = AnalyzeLoop((builder, i, first, _) => {
      builder.Store(new IrConstantInt(IrType.I16, 0x1234), Address(builder, first, i));
    });

    Assert.That(info.IsComplete, Is.True);
    var dependence = info.Dependences.Single();
    Assert.That(dependence.Kind, Is.EqualTo(IrDependenceKind.Output));
    Assert.That(dependence.Direction, Is.EqualTo(IrDependenceDirection.Less));
    Assert.That(dependence.Distance, Is.EqualTo(1));
  }

  [Test]
  public void InvariantStore_HasLoopCarriedOutputDependence() {
    var info = AnalyzeLoop((builder, _, first, _) =>
      builder.Store(new IrConstantInt(IrType.I8, 1), first));

    Assert.That(info.IsComplete, Is.True);
    Assert.That(info.HasLoopCarriedDependence, Is.True);
    Assert.That(info.Dependences, Has.Some.Matches<IrLoopDependence>(d =>
      d.Kind == IrDependenceKind.Output && d.Distance == 1));
  }

  [Test]
  public void UnequalStrides_WhenGcdCannotDivideOffset_AreProvenIndependent() {
    var info = AnalyzeLoop((builder, i, first, _) => {
      builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i, scale: 4));
      builder.Load(IrType.I8, Address(builder, first, i, scale: 6, offset: 1));
    });

    Assert.That(info.IsComplete, Is.True);
    Assert.That(info.Dependences, Is.Empty);
  }

  [Test]
  public void UnequalStrides_WhenCheapTestsAdmitCollision_RemainUnknown() {
    var info = AnalyzeLoop((builder, i, first, _) => {
      builder.Store(new IrConstantInt(IrType.I8, 1), Address(builder, first, i, scale: 4));
      builder.Load(IrType.I8, Address(builder, first, i, scale: 6, offset: 2));
    });

    Assert.That(info.IsComplete, Is.False);
  }

  [Test]
  public void WrappingAffineIndex_RemainsUnknownRatherThanUsingMathematicalValue() {
    var info = AnalyzeLoop((builder, i, first, _) => {
      var address = Address(builder, first, i, scale: 4000);
      builder.Store(new IrConstantInt(IrType.I8, 1), address);
      builder.Load(IrType.I8, address);
    }, limit: 10);

    Assert.That(info.IsComplete, Is.False);
  }
}