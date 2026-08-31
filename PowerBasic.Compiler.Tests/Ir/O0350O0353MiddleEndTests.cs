using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression tests for the O0350-O0353 middle-end ports.</summary>
[TestFixture]
public sealed class O0350O0353MiddleEndTests {

  [Test]
  public void OverflowCoalescing_GivenTwoPureConsecutiveError6Guards_WhenRun_ThenOneBranchCarriesTheirOr() {
    var first = new IrArgument(IrType.I1, 0, "first");
    var second = new IrArgument(IrType.I1, 1, "second");
    var x = new IrArgument(IrType.I16, 2, "x");
    var fn = new IrFunction("f", IrType.Void, [first, second, x]);
    var error = new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var entry = fn.CreateBlock("entry");
    var trap1 = fn.CreateBlock("trap1");
    var middle = fn.CreateBlock("middle");
    var trap2 = fn.CreateBlock("trap2");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).CondBr(first, trap1, middle);
    var bt1 = new IrBuilder(trap1);
    bt1.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    bt1.Br(middle);
    var bm = new IrBuilder(middle);
    bm.Add(x, new IrConstantInt(IrType.I16, 1));
    bm.CondBr(second, trap2, exit);
    var bt2 = new IrBuilder(trap2);
    bt2.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    bt2.Br(exit);
    new IrBuilder(exit).Ret();

    var changed = OverflowCheckCoalescing.Run(fn);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(entry.Terminator, Is.TypeOf<IrBr>());
    Assert.That(middle.Terminator, Is.TypeOf<IrCondBr>());
    Assert.That(((IrCondBr)middle.Terminator!).Condition, Is.TypeOf<IrBinary>());
    Assert.That(((IrBinary)((IrCondBr)middle.Terminator!).Condition).Op, Is.EqualTo(IrBinaryOp.Or));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void OverflowCoalescing_GivenAnObservableCallBetweenGuards_WhenRun_ThenTheChecksStaySeparate() {
    var first = new IrArgument(IrType.I1, 0, "first");
    var second = new IrArgument(IrType.I1, 1, "second");
    var fn = new IrFunction("f", IrType.Void, [first, second]);
    var error = new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]);
    var sideEffect = new IrFunction("side_effect", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var trap1 = fn.CreateBlock("trap1");
    var middle = fn.CreateBlock("middle");
    var trap2 = fn.CreateBlock("trap2");
    var exit = fn.CreateBlock("exit");

    new IrBuilder(entry).CondBr(first, trap1, middle);
    var bt1 = new IrBuilder(trap1);
    bt1.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    bt1.Br(middle);
    var bm = new IrBuilder(middle);
    bm.Call(IrType.Void, sideEffect);
    bm.CondBr(second, trap2, exit);
    var bt2 = new IrBuilder(trap2);
    bt2.Call(IrType.Void, error, IrBuilder.ConstI32(6));
    bt2.Br(exit);
    new IrBuilder(exit).Ret();

    Assert.That(OverflowCheckCoalescing.Run(fn), Is.Zero);
    Assert.That(entry.Terminator, Is.TypeOf<IrCondBr>());
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PointerCheckElim_GivenAnExplicitDominatingNonNullTest_WhenRun_ThenTheRepeatedTestIsTrue() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.Void, [pointer]);
    var entry = fn.CreateBlock("entry");
    var checkedBlock = fn.CreateBlock("checked");
    var repeatedTrue = fn.CreateBlock("repeated.true");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    be.CondBr(be.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr()), checkedBlock, exit);
    var bc = new IrBuilder(checkedBlock);
    bc.Load(IrType.I16, pointer); // deliberately NOT used as proof
    var repeated = bc.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr());
    bc.CondBr(repeated, repeatedTrue, exit);
    new IrBuilder(repeatedTrue).Br(exit);
    new IrBuilder(exit).Ret();

    var changed = PointerCheckElim.Run(fn);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(((IrCondBr)checkedBlock.Terminator!).Condition, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrCondBr)checkedBlock.Terminator!).Condition).Value, Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void PointerCheckElim_GivenOnlyADereferenceBeforeTheTest_WhenRun_ThenNoNonNullFactIsInvented() {
    var pointer = new IrArgument(IrType.Ptr, 0, "p");
    var fn = new IrFunction("f", IrType.Void, [pointer]);
    var entry = fn.CreateBlock("entry");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");
    var be = new IrBuilder(entry);
    be.Load(IrType.I16, pointer);
    var comparison = be.Cmp(IrCmpPred.Ne, pointer, new IrNullPtr());
    be.CondBr(comparison, yes, no);
    new IrBuilder(yes).Ret();
    new IrBuilder(no).Ret();

    Assert.That(PointerCheckElim.Run(fn), Is.Zero);
    Assert.That(((IrCondBr)entry.Terminator!).Condition, Is.SameAs(comparison));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ConversionRangeElim_GivenUnsignedByteConvertedToFloat_WhenComparedBelowMinusHalf_ThenTheGuardIsFalse() {
    var value = new IrArgument(IrType.U8, 0, "value");
    var fn = new IrFunction("f", IrType.Void, [value]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("trap");
    var exit = fn.CreateBlock("exit");
    var be = new IrBuilder(entry);
    var floating = be.Cast(IrCastOp.UIToFP, value, IrType.F64);
    var outside = be.Cmp(IrCmpPred.Folt, floating, IrBuilder.ConstFloat(IrType.F64, -0.5));
    be.CondBr(outside, trap, exit);
    new IrBuilder(trap).Br(exit);
    new IrBuilder(exit).Ret();

    var changed = ConversionRangeCheckElim.Run(fn);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(((IrCondBr)entry.Terminator!).Condition, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)((IrCondBr)entry.Terminator!).Condition).Value, Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ConversionRangeElim_GivenAnArbitraryFloat_WhenRun_ThenNaNPreventsAClaim() {
    var value = new IrArgument(IrType.F64, 0, "value");
    var fn = new IrFunction("f", IrType.Void, [value]);
    var entry = fn.CreateBlock("entry");
    var trap = fn.CreateBlock("trap");
    var exit = fn.CreateBlock("exit");
    var be = new IrBuilder(entry);
    var comparison = be.Cmp(IrCmpPred.Fogt, value, IrBuilder.ConstFloat(IrType.F64, 255.5));
    be.CondBr(comparison, trap, exit);
    new IrBuilder(trap).Br(exit);
    new IrBuilder(exit).Ret();

    Assert.That(ConversionRangeCheckElim.Run(fn), Is.Zero);
    Assert.That(((IrCondBr)entry.Terminator!).Condition, Is.SameAs(comparison));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void StringCapacityHoisting_GivenExactTripLiteralAppendLoop_WhenRun_ThenTheSuffixIsBuiltOnceBeforeTheLoop() {
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
    var counter = bh.Phi(IrType.I16);
    var text = bh.Phi(IrType.Ptr);
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

    var changed = StringCapacityHoisting.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(body.Instructions.OfType<IrCall>(), Is.Empty);
    Assert.That(preheader.Instructions.OfType<IrCall>().Select(c => ((IrFunction)c.Callee).Name),
      Is.EquivalentTo(new[] { "rt_str_const", "rt_str_repeat", "rt_str_concat" }));
    Assert.That(text.IncomingFrom(body), Is.SameAs(text));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void StringCapacityHoisting_GivenAnotherCallInsideTheLoop_WhenRun_ThenItDoesNotMoveTheBuild() {
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
    bh.CondBr(bh.Cmp(IrCmpPred.Sle, counter, new IrConstantInt(IrType.I16, 3)), body, exit);
    var bb = new IrBuilder(body);
    var appended = bb.Call(IrType.Ptr, append, text, new IrNullPtr(), IrBuilder.ConstI32(1));
    bb.Call(IrType.Void, sideEffect);
    var next = bb.Add(counter, new IrConstantInt(IrType.I16, 1));
    bb.Br(header);
    counter.AddIncoming(new IrConstantInt(IrType.I16, 1), preheader);
    counter.AddIncoming(next, body);
    text.AddIncoming(new IrNullPtr(), preheader);
    text.AddIncoming(appended, body);
    new IrBuilder(exit).Ret(text);

    Assert.That(StringCapacityHoisting.Run(module), Is.Zero);
    Assert.That(body.Instructions.OfType<IrCall>().Count(), Is.EqualTo(2));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
