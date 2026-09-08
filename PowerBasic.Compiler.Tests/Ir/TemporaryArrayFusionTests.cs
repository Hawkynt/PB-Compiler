using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class TemporaryArrayFusionTests {

  [Test]
  public void O0328_LoadRematerialization_DeclinesAcrossProducerCall()
    => AssertDeclinesAcrossOpaqueCall(callInProducer: true);

  [Test]
  public void O0328_LoadRematerialization_DeclinesAcrossConsumerCall()
    => AssertDeclinesAcrossOpaqueCall(callInProducer: false);

  private static void AssertDeclinesAcrossOpaqueCall(bool callInProducer) {
    var fn = Build(callInProducer);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(TemporaryArrayFusion.Run(fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Any(a => a.Name == "tmp"), Is.True);
  }

  private static IrFunction Build(bool callInProducer) {
    var opaque = new IrFunction("opaque", IrType.Void, [new IrArgument(IrType.Ptr, 0, "p")]);
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var pHeader = fn.CreateBlock("p.header");
    var pBody = fn.CreateBlock("p.body");
    var pLatch = fn.CreateBlock("p.latch");
    var between = fn.CreateBlock("between");
    var cHeader = fn.CreateBlock("c.header");
    var cBody = fn.CreateBlock("c.body");
    var cLatch = fn.CreateBlock("c.latch");
    var exit = fn.CreateBlock("exit");
    var source = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "src" });
    var temp = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "tmp" });
    var output = entry.Append(new IrAlloca(IrType.I16) { Count = 8, Name = "out" });
    entry.Append(new IrBr(pHeader));

    var p = pHeader.AppendPhi(new IrPhi(IrType.I32));
    p.AddIncoming(new IrConstantInt(IrType.I32, 0), entry);
    pHeader.Append(new IrCondBr(pHeader.Append(new IrCmp(IrCmpPred.Slt, p, new IrConstantInt(IrType.I32, 8))), pBody, between));
    var srcPtr = pBody.Append(new IrGep(source, p, IrType.I16));
    var src = pBody.Append(new IrLoad(IrType.I16, srcPtr));
    var doubled = pBody.Append(new IrBinary(IrBinaryOp.Mul, src, new IrConstantInt(IrType.I16, 2)));
    pBody.Append(new IrStore(doubled, pBody.Append(new IrGep(temp, p, IrType.I16))));
    if (callInProducer)
      pBody.Append(new IrCall(IrType.Void, opaque, [source]));
    pBody.Append(new IrBr(pLatch));
    var pNext = pLatch.Append(new IrBinary(IrBinaryOp.Add, p, new IrConstantInt(IrType.I32, 1)));
    pLatch.Append(new IrBr(pHeader));
    p.AddIncoming(pNext, pLatch);
    between.Append(new IrBr(cHeader));

    var c = cHeader.AppendPhi(new IrPhi(IrType.I32));
    c.AddIncoming(new IrConstantInt(IrType.I32, 0), between);
    cHeader.Append(new IrCondBr(cHeader.Append(new IrCmp(IrCmpPred.Slt, c, new IrConstantInt(IrType.I32, 8))), cBody, exit));
    if (!callInProducer)
      cBody.Append(new IrCall(IrType.Void, opaque, [source]));
    var tempValue = cBody.Append(new IrLoad(IrType.I16, cBody.Append(new IrGep(temp, c, IrType.I16))));
    var plusOne = cBody.Append(new IrBinary(IrBinaryOp.Add, tempValue, new IrConstantInt(IrType.I16, 1)));
    cBody.Append(new IrStore(plusOne, cBody.Append(new IrGep(output, c, IrType.I16))));
    cBody.Append(new IrBr(cLatch));
    var cNext = cLatch.Append(new IrBinary(IrBinaryOp.Add, c, new IrConstantInt(IrType.I32, 1)));
    cLatch.Append(new IrBr(cHeader));
    c.AddIncoming(cNext, cLatch);
    exit.Append(new IrRet());

    return fn;
  }
}
