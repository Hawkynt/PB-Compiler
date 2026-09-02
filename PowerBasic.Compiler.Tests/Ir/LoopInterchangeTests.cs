using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class LoopInterchangeTests {

  private sealed record Fixture(
    IrFunction Fn,
    IrBasicBlock OuterHeader,
    IrBasicBlock InnerHeader,
    IrBasicBlock Exit,
    IrPhi OuterCounter,
    IrPhi InnerCounter,
    IrStore BodyStore);

  private static Fixture CreateNest(
      long outerCoefficient = 1,
      long innerCoefficient = 64,
      bool aliasSourceAndDestination = false,
      bool addOuterExitUse = false,
      bool addInnerExitCarrier = false,
      int outerTrips = 32,
      int innerTrips = 32) {
    var sink = new IrArgument(IrType.Ptr, 0, "sink");
    var fn = new IrFunction("f", IrType.Void, [sink]);
    var entry = fn.CreateBlock("entry");
    var outerHeader = fn.CreateBlock("outer.head");
    var innerPreheader = fn.CreateBlock("inner.pre");
    var innerHeader = fn.CreateBlock("inner.head");
    var innerBody = fn.CreateBlock("inner.body");
    var outerLatch = fn.CreateBlock("outer.latch");
    var exit = fn.CreateBlock("exit");

    var source = entry.Append(new IrAlloca(IrType.I8) { Count = 4096 });
    var destination = aliasSourceAndDestination
      ? source
      : entry.Append(new IrAlloca(IrType.I8) { Count = 4096 });
    entry.Append(new IrBr(outerHeader));

    var i = outerHeader.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var outerTest = outerHeader.Append(new IrCmp(IrCmpPred.Slt, i, new IrConstantInt(IrType.I16, outerTrips)));
    outerHeader.Append(new IrCondBr(outerTest, innerPreheader, exit));
    innerPreheader.Append(new IrBr(innerHeader));

    var j = innerHeader.AppendPhi(new IrPhi(IrType.I16) { Name = "j" });
    var innerTest = innerHeader.Append(new IrCmp(IrCmpPred.Slt, j, new IrConstantInt(IrType.I16, innerTrips)));
    innerHeader.Append(new IrCondBr(innerTest, innerBody, outerLatch));

    var body = new IrBuilder(innerBody);
    var index = AffineIndex(body, i, j, outerCoefficient, innerCoefficient);
    var loaded = body.Load(IrType.I8, body.Gep(source, index));
    var store = body.Store(loaded, body.Gep(destination, index));
    var nextJ = body.Add(j, new IrConstantInt(IrType.I16, 1));
    body.Br(innerHeader);

    var outerBuilder = new IrBuilder(outerLatch);
    var nextI = outerBuilder.Add(i, new IrConstantInt(IrType.I16, 1));
    outerBuilder.Br(outerHeader);

    i.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    i.AddIncoming(nextI, outerLatch);
    j.AddIncoming(new IrConstantInt(IrType.I16, 0), innerPreheader);
    j.AddIncoming(nextJ, innerBody);

    var exitBuilder = new IrBuilder(exit);
    if (addOuterExitUse)
      exitBuilder.Store(i, sink);
    if (addInnerExitCarrier) {
      var carrier = outerHeader.AppendPhi(new IrPhi(IrType.I16) { Name = "j.exit" });
      carrier.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
      carrier.AddIncoming(j, outerLatch);
      exitBuilder.Store(carrier, sink);
    }
    exitBuilder.Ret();

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    return new(fn, outerHeader, innerHeader, exit, i, j, store);
  }

  private static IrValue AffineIndex(IrBuilder builder, IrValue outer, IrValue inner, long outerCoefficient, long innerCoefficient) {
    IrValue? result = null;
    AddTerm(outer, outerCoefficient);
    AddTerm(inner, innerCoefficient);
    return result ?? new IrConstantInt(IrType.I16, 0);

    void AddTerm(IrValue value, long coefficient) {
      if (coefficient == 0)
        return;
      IrValue term = coefficient == 1
        ? value
        : builder.Mul(value, new IrConstantInt(IrType.I16, coefficient));
      result = result is null ? term : builder.Add(result, term);
    }
  }

  [Test]
  public void Nest_GivenOuterCounterHasSmallerMemoryStride_ThenCountersAreInterchanged() {
    var fixture = CreateNest();

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);

    Assert.That(fixture.OuterCounter.Parent, Is.Null);
    Assert.That(fixture.InnerCounter.Parent, Is.Null);
    Assert.That(fixture.OuterHeader.Phis.Single().Name, Is.EqualTo("j"));
    Assert.That(fixture.InnerHeader.Phis.Single().Name, Is.EqualTo("i"));
  }

  [Test]
  public void Nest_GivenCurrentInnerStrideIsAlreadySmaller_ThenItIsLeftAlone() {
    var fixture = CreateNest(outerCoefficient: 64, innerCoefficient: 1);

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
    Assert.That(fixture.OuterCounter.Parent, Is.SameAs(fixture.OuterHeader));
  }

  [Test]
  public void Nest_GivenReadAndWriteMayAliasAtDifferentSites_ThenLegalityDeclines() {
    var fixture = CreateNest(aliasSourceAndDestination: true);

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
  }

  [Test]
  public void Nest_GivenWriteDoesNotVaryWithOuterLoop_ThenSelfDependenceDeclines() {
    var sink = new IrArgument(IrType.Ptr, 0, "sink");
    var fn = new IrFunction("f", IrType.Void, [sink]);
    var entry = fn.CreateBlock("entry");
    var outerHeader = fn.CreateBlock("outer.head");
    var innerPreheader = fn.CreateBlock("inner.pre");
    var innerHeader = fn.CreateBlock("inner.head");
    var innerBody = fn.CreateBlock("inner.body");
    var outerLatch = fn.CreateBlock("outer.latch");
    var exit = fn.CreateBlock("exit");
    var destination = entry.Append(new IrAlloca(IrType.I8) { Count = 64 });
    entry.Append(new IrBr(outerHeader));
    var i = outerHeader.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var outerTest = outerHeader.Append(new IrCmp(IrCmpPred.Slt, i, new IrConstantInt(IrType.I16, 32)));
    outerHeader.Append(new IrCondBr(outerTest, innerPreheader, exit));
    innerPreheader.Append(new IrBr(innerHeader));
    var j = innerHeader.AppendPhi(new IrPhi(IrType.I16) { Name = "j" });
    var innerTest = innerHeader.Append(new IrCmp(IrCmpPred.Slt, j, new IrConstantInt(IrType.I16, 32)));
    innerHeader.Append(new IrCondBr(innerTest, innerBody, outerLatch));
    var body = new IrBuilder(innerBody);
    body.Store(new IrConstantInt(IrType.I8, 7), body.Gep(destination, j));
    var nextJ = body.Add(j, new IrConstantInt(IrType.I16, 1));
    body.Br(innerHeader);
    var latch = new IrBuilder(outerLatch);
    var nextI = latch.Add(i, new IrConstantInt(IrType.I16, 1));
    latch.Br(outerHeader);
    i.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    i.AddIncoming(nextI, outerLatch);
    j.AddIncoming(new IrConstantInt(IrType.I16, 0), innerPreheader);
    j.AddIncoming(nextJ, innerBody);
    new IrBuilder(exit).Ret();

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(LoopInterchange.Run(fn), Is.Zero, "every outer iteration writes the same j-addresses");
  }

  [Test]
  public void Nest_GivenOuterCounterIsReadAfterLoop_ThenItsFinalValueIsPreserved() {
    var fixture = CreateNest(addOuterExitUse: true);
    var exitStore = fixture.Exit.Instructions.OfType<IrStore>().Single();

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
    Assert.That(exitStore.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)exitStore.Value).Value, Is.EqualTo(32));
  }

  [Test]
  public void Nest_GivenInnerCounterHasOuterExitCarrier_ThenItsFinalValueIsPreserved() {
    var fixture = CreateNest(addInnerExitCarrier: true);
    var exitStore = fixture.Exit.Instructions.OfType<IrStore>().Single();

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
    Assert.That(exitStore.Value, Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)exitStore.Value).Value, Is.EqualTo(32));
    Assert.That(fixture.OuterHeader.Phis.Select(phi => phi.Name), Is.EqualTo(new[] { "j" }));
  }

  [Test]
  public void Nest_GivenAffineIndexCanWrap_ThenItIsLeftAlone() {
    var fixture = CreateNest(outerCoefficient: 1, innerCoefficient: 2000);

    Assert.That(LoopInterchange.Run(fixture.Fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
  }
}
