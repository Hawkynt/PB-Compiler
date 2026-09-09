using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0313 — inclusive integer prefix-scan recognition and SSA recurrence formation.</summary>
[TestFixture]
public sealed class ParallelPrefixScanTests {

  private sealed record Fixture(
    IrFunction Function,
    IrBasicBlock Entry,
    IrBasicBlock Head,
    IrBasicBlock Body,
    IrAlloca Output,
    IrPhi Counter,
    IrLoad Previous,
    IrBinary Update,
    IrStore Store);

  [TestCase(IrBinaryOp.Add)]
  [TestCase(IrBinaryOp.Mul)]
  [TestCase(IrBinaryOp.And)]
  [TestCase(IrBinaryOp.Or)]
  [TestCase(IrBinaryOp.Xor)]
  public void AssociativeIntegerScan_IsCarriedInSsa(IrBinaryOp op) {
    var fixture = BuildScan(op);

    var changes = ParallelPrefixScan.Run(fixture.Function);

    Assert.That(changes, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
    Assert.That(fixture.Previous.Parent, Is.Null,
      "the previous-element memory load is replaced by the carried scan value");
    Assert.That(fixture.Store.Value, Is.SameAs(fixture.Update),
      "every intermediate scan result must still be stored");

    var scan = fixture.Head.Phis.Single(phi => !ReferenceEquals(phi, fixture.Counter));
    Assert.That(scan.Type, Is.SameAs(IrType.I16));
    Assert.That(scan.IncomingFrom(fixture.Body), Is.SameAs(fixture.Update));
    Assert.That(fixture.Update.Lhs, Is.SameAs(scan));

    var seed = scan.IncomingFrom(fixture.Entry);
    Assert.That(seed, Is.TypeOf<IrLoad>());
    var seedLoad = (IrLoad)seed!;
    Assert.That(seedLoad.Parent, Is.SameAs(fixture.Entry));
    Assert.That(seedLoad.Pointer, Is.TypeOf<IrGep>());
    var seedAddress = (IrGep)seedLoad.Pointer;
    Assert.That(seedAddress.BasePtr, Is.SameAs(fixture.Output));
    Assert.That(seedAddress.ElementType, Is.SameAs(IrType.I16));

    // The original address was t[i-1]. Materializing it in the preheader substitutes the loop's
    // initial i=1, so the seed is exactly t[1-1], not an invented identity value.
    Assert.That(seedAddress.ByteOffset, Is.TypeOf<IrBinary>());
    var seedIndex = (IrBinary)seedAddress.ByteOffset;
    Assert.That(seedIndex.Op, Is.EqualTo(IrBinaryOp.Add));
    Assert.That(((IrConstantInt)seedIndex.Lhs).Value, Is.EqualTo(1));
    Assert.That(((IrConstantInt)seedIndex.Rhs).Value, Is.EqualTo(-1));

    Assert.That(ParallelPrefixScan.Run(fixture.Function), Is.Zero, "the canonicalization is idempotent");
  }

  [Test]
  public void PreviousValueOnRight_IsRecognized() {
    var fixture = BuildScan(IrBinaryOp.Add, previousOnRight: true);

    Assert.That(ParallelPrefixScan.Run(fixture.Function), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);

    var scan = fixture.Head.Phis.Single(phi => !ReferenceEquals(phi, fixture.Counter));
    Assert.That(fixture.Update.Rhs, Is.SameAs(scan));
  }

  [Test]
  public void NonAssociativeIntegerRecurrence_IsLeftAlone() {
    var fixture = BuildScan(IrBinaryOp.Sub);

    Assert.That(ParallelPrefixScan.Run(fixture.Function), Is.Zero);
    Assert.That(fixture.Previous.Parent, Is.SameAs(fixture.Body));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void FloatingPointScan_IsLeftAlone() {
    var fixture = BuildScan(IrBinaryOp.FAdd, IrType.F64);

    Assert.That(ParallelPrefixScan.Run(fixture.Function), Is.Zero);
    Assert.That(fixture.Previous.Parent, Is.SameAs(fixture.Body));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  [Test]
  public void SecondLoopCarriedMemoryDependence_BlocksRewrite() {
    var fixture = BuildScan(IrBinaryOp.Add, addSecondScan: true);

    Assert.That(ParallelPrefixScan.Run(fixture.Function), Is.Zero,
      "a scan is safe to carry only when O0172 proves its edge is the loop's sole carried memory dependence");
    Assert.That(fixture.Previous.Parent, Is.SameAs(fixture.Body));
    Assert.That(IrVerifier.Verify(fixture.Function), Is.Empty);
  }

  private static Fixture BuildScan(IrBinaryOp op, IrType? valueType = null,
      bool previousOnRight = false, bool addSecondScan = false) {
    valueType ??= IrType.I16;
    var fn = new IrFunction("scan", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var head = fn.CreateBlock("head");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    var output = entry.Append(new IrAlloca(valueType) { Count = 64 });
    var input = entry.Append(new IrAlloca(valueType) { Count = 64 });
    var other = addSecondScan ? entry.Append(new IrAlloca(valueType) { Count = 64 }) : null;
    entry.Append(new IrBr(head));

    var counter = head.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var test = head.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 16)));
    head.Append(new IrCondBr(test, body, exit));

    var builder = new IrBuilder(body);
    var previousIndex = builder.Add(counter, new IrConstantInt(IrType.I16, -1));
    var previous = builder.Load(valueType, builder.Gep(output, previousIndex, valueType));
    var current = builder.Load(valueType, builder.Gep(input, counter, valueType));
    var update = previousOnRight
      ? builder.Binary(op, current, previous)
      : builder.Binary(op, previous, current);
    var store = builder.Store(update, builder.Gep(output, counter, valueType));

    if (other is not null) {
      var otherPreviousIndex = builder.Add(counter, new IrConstantInt(IrType.I16, -1));
      var otherPrevious = builder.Load(valueType, builder.Gep(other, otherPreviousIndex, valueType));
      var otherUpdate = builder.Binary(IrBinaryOp.Add, otherPrevious, current);
      builder.Store(otherUpdate, builder.Gep(other, counter, valueType));
    }

    var next = builder.Add(counter, new IrConstantInt(IrType.I16, 1));
    builder.Br(head);
    new IrBuilder(exit).Ret();

    counter.AddIncoming(new IrConstantInt(IrType.I16, 1), entry);
    counter.AddIncoming(next, body);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    return new(fn, entry, head, body, output, counter, previous, update, store);
  }
}
