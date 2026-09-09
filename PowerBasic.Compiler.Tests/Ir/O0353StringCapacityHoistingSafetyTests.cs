using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Safety regressions for O0353 string-capacity hoisting.</summary>
[TestFixture]
public sealed class O0353StringCapacityHoistingSafetyTests {

  [Test]
  public void StringCapacityHoisting_GivenHeaderSideEffect_WhenRun_ThenTheBuildStaysInTheLoop() {
    var module = new IrModule("test");
    var append = module.AddFunction(new IrFunction("rt_str_append_lit", IrType.Ptr, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1), new IrArgument(IrType.I32, 2),
    ]));
    var sideEffect = module.AddFunction(new IrFunction("side_effect", IrType.Void, []));
    var fn = module.AddFunction(new IrFunction("f", IrType.Ptr, []));
    var preheader = fn.CreateBlock("preheader");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(preheader).Br(header);

    var bh = new IrBuilder(header);
    var counter = bh.Phi(IrType.I16);
    var text = bh.Phi(IrType.Ptr);
    bh.Call(IrType.Void, sideEffect);
    bh.CondBr(bh.Cmp(IrCmpPred.Sle, counter, new IrConstantInt(IrType.I16, 3)), body, exit);

    var bb = new IrBuilder(body);
    var appended = bb.Call(IrType.Ptr, append, text, new IrNullPtr(), IrBuilder.ConstI32(1));
    var next = bb.Add(counter, new IrConstantInt(IrType.I16, 1));
    bb.Br(header);
    counter.AddIncoming(new IrConstantInt(IrType.I16, 1), preheader);
    counter.AddIncoming(next, body);
    text.AddIncoming(new IrNullPtr(), preheader);
    text.AddIncoming(appended, body);
    new IrBuilder(exit).Ret(text);

    Assert.That(StringCapacityHoisting.Run(module), Is.Zero);
    Assert.That(body.Instructions.OfType<IrCall>().Single(), Is.SameAs(appended));
    Assert.That(preheader.Instructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void StringCapacityHoisting_GivenWrappingCounter_WhenRun_ThenItUsesTheMachineWidthTripCount() {
    var module = new IrModule("test");
    var append = module.AddFunction(new IrFunction("rt_str_append_lit", IrType.Ptr, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.Ptr, 1), new IrArgument(IrType.I32, 2),
    ]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Ptr, []));
    var preheader = fn.CreateBlock("preheader");
    var header = fn.CreateBlock("header");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");
    new IrBuilder(preheader).Br(header);

    var bh = new IrBuilder(header);
    var counter = bh.Phi(IrType.I8);
    var text = bh.Phi(IrType.Ptr);
    bh.CondBr(bh.Cmp(IrCmpPred.Sle, counter, new IrConstantInt(IrType.I8, 125)), body, exit);

    var bb = new IrBuilder(body);
    var appended = bb.Call(IrType.Ptr, append, text, new IrNullPtr(), IrBuilder.ConstI32(1));
    var next = bb.Add(counter, new IrConstantInt(IrType.I8, 9));
    bb.Br(header);
    counter.AddIncoming(new IrConstantInt(IrType.I8, 110), preheader);
    counter.AddIncoming(next, body);
    text.AddIncoming(new IrNullPtr(), preheader);
    text.AddIncoming(appended, body);
    new IrBuilder(exit).Ret(text);

    var changed = StringCapacityHoisting.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    var repeat = preheader.Instructions.OfType<IrCall>()
      .Single(call => call.Callee is IrFunction { Name: "rt_str_repeat" });
    Assert.That(repeat.GetOperand(1), Is.TypeOf<IrConstantInt>());
    Assert.That(((IrConstantInt)repeat.GetOperand(1)).Value, Is.EqualTo(144));
    Assert.That(body.Instructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
