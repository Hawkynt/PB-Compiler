using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class InductionVariableSimplificationTests {

  private sealed record LoopFixture(
    IrFunction Fn,
    IrArgument Sink,
    IrBasicBlock Entry,
    IrBasicBlock Header,
    IrBasicBlock Body,
    IrBasicBlock Latch,
    IrBasicBlock Exit,
    IrPhi Counter,
    IrBinary NextCounter);

  private static LoopFixture CreateLoop(long start = 0, long limit = 8, long step = 1) {
    var sink = new IrArgument(IrType.Ptr, 0, "sink");
    var fn = new IrFunction("f", IrType.Void, [sink]);
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var latch = fn.CreateBlock("latch");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrBr(header));
    var counter = header.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, limit)));
    header.Append(new IrCondBr(test, body, exit));
    body.Append(new IrBr(latch));
    var nextCounter = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, step)));
    latch.Append(new IrBr(header));
    exit.Append(new IrRet());

    counter.AddIncoming(new IrConstantInt(IrType.I16, start), entry);
    counter.AddIncoming(nextCounter, latch);

    return new(fn, sink, entry, header, body, latch, exit, counter, nextCounter);
  }

  [Test]
  public void Run_GivenAffineDerivedValue_ThenCarriesValueWithOneLatchAdd() {
    var loop = CreateLoop();
    var scaled = new IrBinary(IrBinaryOp.Mul, loop.Counter, new IrConstantInt(IrType.I16, 2));
    loop.Body.InsertBefore(scaled, loop.Body.Terminator!);
    var derived = new IrBinary(IrBinaryOp.Add, scaled, new IrConstantInt(IrType.I16, 3)) { Name = "j" };
    loop.Body.InsertBefore(derived, loop.Body.Terminator!);
    var store = new IrStore(derived, loop.Sink);
    loop.Body.InsertBefore(store, loop.Body.Terminator!);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(InductionVariableSimplification.Run(loop.Fn), Is.EqualTo(1));

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    var iv = loop.Header.Phis.Single(phi => !ReferenceEquals(phi, loop.Counter));
    Assert.That(store.Value, Is.SameAs(iv));
    Assert.That(((IrConstantInt)iv.IncomingFrom(loop.Entry)!).Value, Is.EqualTo(3));
    var next = iv.IncomingFrom(loop.Latch) as IrBinary;
    Assert.That(next, Is.Not.Null);
    Assert.That(next!.Op, Is.EqualTo(IrBinaryOp.Add));
    Assert.That(next.Lhs, Is.SameAs(iv));
    Assert.That(((IrConstantInt)next.Rhs).Value, Is.EqualTo(2));
    Assert.That(derived.Parent, Is.Null);

    Dce.Run(loop.Fn);
    Assert.That(scaled.Parent, Is.Null, "the per-iteration multiply becomes dead");
  }

  [Test]
  public void Run_GivenAffineValuesThatWrap_ThenUsesModuloTypeArithmetic() {
    var loop = CreateLoop(start: 10_000, limit: 10_300, step: 100);
    var scaled = new IrBinary(IrBinaryOp.Mul, loop.Counter, new IrConstantInt(IrType.I16, 4));
    loop.Body.InsertBefore(scaled, loop.Body.Terminator!);
    var derived = new IrBinary(IrBinaryOp.Add, scaled, new IrConstantInt(IrType.I16, 30_000));
    loop.Body.InsertBefore(derived, loop.Body.Terminator!);
    loop.Body.InsertBefore(new IrStore(derived, loop.Sink), loop.Body.Terminator!);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(InductionVariableSimplification.Run(loop.Fn), Is.EqualTo(1));

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    var iv = loop.Header.Phis.Single(phi => !ReferenceEquals(phi, loop.Counter));
    Assert.That(((IrConstantInt)iv.IncomingFrom(loop.Entry)!).Value, Is.EqualTo(4_464),
      "4*10000+30000 wraps to 4464 in i16");
    var next = (IrBinary)iv.IncomingFrom(loop.Latch)!;
    Assert.That(((IrConstantInt)next.Rhs).Value, Is.EqualTo(400));
  }

  [Test]
  public void Run_GivenNonAffineCounterProduct_ThenDeclines() {
    var loop = CreateLoop();
    var square = new IrBinary(IrBinaryOp.Mul, loop.Counter, loop.Counter);
    loop.Body.InsertBefore(square, loop.Body.Terminator!);
    loop.Body.InsertBefore(new IrStore(square, loop.Sink), loop.Body.Terminator!);

    Assert.That(InductionVariableSimplification.Run(loop.Fn), Is.Zero);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(square.Parent, Is.SameAs(loop.Body));
    Assert.That(loop.Header.Phis.Count(), Is.EqualTo(1));
  }

  [Test]
  public void Run_GivenCheapOffsetOnly_ThenDoesNotAddLoopCarriedState() {
    var loop = CreateLoop();
    var offset = new IrBinary(IrBinaryOp.Add, loop.Counter, new IrConstantInt(IrType.I16, 3));
    loop.Body.InsertBefore(offset, loop.Body.Terminator!);
    loop.Body.InsertBefore(new IrStore(offset, loop.Sink), loop.Body.Terminator!);

    Assert.That(InductionVariableSimplification.Run(loop.Fn), Is.Zero);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(loop.Header.Phis.Count(), Is.EqualTo(1));
  }

  [Test]
  public void Run_GivenAffineValueOnPhiEdge_ThenDeclinesEdgeRewrite() {
    var loop = CreateLoop();
    var carried = loop.Header.AppendPhi(new IrPhi(IrType.I16) { Name = "carried" });
    var twice = new IrBinary(IrBinaryOp.Mul, loop.Counter, new IrConstantInt(IrType.I16, 2));
    loop.Latch.InsertBefore(twice, loop.NextCounter);
    carried.AddIncoming(new IrConstantInt(IrType.I16, 0), loop.Entry);
    carried.AddIncoming(twice, loop.Latch);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);

    Assert.That(InductionVariableSimplification.Run(loop.Fn), Is.Zero);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    Assert.That(twice.Parent, Is.SameAs(loop.Latch));
    Assert.That(carried.IncomingFrom(loop.Latch), Is.SameAs(twice));
  }

  [Test]
  public void StandardPipeline_GivenLongAffineLoop_ThenRunsO0062AndKeepsIrValid() {
    var loop = CreateLoop(limit: 32);
    var scaled = new IrBinary(IrBinaryOp.Mul, loop.Counter, new IrConstantInt(IrType.I16, 3));
    loop.Body.InsertBefore(scaled, loop.Body.Terminator!);
    var derived = new IrBinary(IrBinaryOp.Add, scaled, new IrConstantInt(IrType.I16, 7));
    loop.Body.InsertBefore(derived, loop.Body.Terminator!);
    var store = new IrStore(derived, loop.Sink);
    loop.Body.InsertBefore(store, loop.Body.Terminator!);

    var passes = IrPassManager.Standard(includeModulePasses: false);
    passes.VerifyEachPass = true;
    passes.RunToFixpoint(loop.Fn);

    Assert.That(IrVerifier.Verify(loop.Fn), Is.Empty);
    var iv = loop.Header.Phis.Single(phi => !ReferenceEquals(phi, loop.Counter));
    Assert.That(store.Value, Is.SameAs(iv));
    Assert.That(((IrConstantInt)iv.IncomingFrom(loop.Entry)!).Value, Is.EqualTo(7));
    Assert.That(loop.Fn.AllInstructions.OfType<IrBinary>().Any(binary => binary.Op == IrBinaryOp.Mul), Is.False,
      "the standard pipeline must reach O0062 and remove the per-iteration constant multiply");
  }
}
