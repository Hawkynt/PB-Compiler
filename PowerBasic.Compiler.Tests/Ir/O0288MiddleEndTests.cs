using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression coverage for O0288 allocation sinking.</summary>
[TestFixture]
public sealed class O0288MiddleEndTests {

  [Test]
  public void AllocationSinking_GivenLiteralUsedOnlyInThenArm_WhenRun_ThenAllocationAndCleanupMoveIntoArm() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.Void, [condition]);
    var allocate = new IrFunction("rt_str_const", IrType.Ptr, [
      new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1),
    ]);
    var free = new IrFunction("rt_str_free", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);
    var length = new IrFunction("rt_str_len", IrType.I32, [new IrArgument(IrType.Ptr, 0)]);
    var entry = fn.CreateBlock("entry");
    var rare = fn.CreateBlock("rare");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    var text = be.Call(IrType.Ptr, allocate, new IrNullPtr(), IrBuilder.ConstI32(5));
    be.Call(IrType.Void, free, new IrNullPtr()); // first assignment's old string is provably empty
    be.CondBr(condition, rare, exit);
    var br = new IrBuilder(rare);
    br.Call(IrType.I32, length, text);
    br.Br(exit);
    var bx = new IrBuilder(exit);
    bx.Call(IrType.Void, free, text);
    bx.Ret();

    var changed = AllocationSinking.Run(fn);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(entry.Instructions.OfType<IrCall>().Select(CallName), Is.EqualTo(new[] { "rt_str_free" }));
    Assert.That(rare.Instructions.OfType<IrCall>().Select(CallName),
      Is.EqualTo(new[] { "rt_str_const", "rt_str_len", "rt_str_free" }));
    Assert.That(exit.Instructions.OfType<IrCall>(), Is.Empty);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void AllocationSinking_GivenLiteralUsedOnlyInElseArm_WhenRun_ThenAllocationMovesIntoElseArm() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.Void, [condition]);
    var allocate = StringAllocator();
    var free = StringFree();
    var length = StringLength();
    var entry = fn.CreateBlock("entry");
    var exit = fn.CreateBlock("exit");
    var rare = fn.CreateBlock("rare");

    var be = new IrBuilder(entry);
    var text = be.Call(IrType.Ptr, allocate, new IrNullPtr(), IrBuilder.ConstI32(5));
    be.CondBr(condition, exit, rare);
    var br = new IrBuilder(rare);
    br.Call(IrType.I32, length, text);
    br.Br(exit);
    var bx = new IrBuilder(exit);
    bx.Call(IrType.Void, free, text);
    bx.Ret();

    Assert.That(AllocationSinking.Run(fn), Is.EqualTo(1));
    Assert.That(entry.Instructions.OfType<IrCall>(), Is.Empty);
    Assert.That(rare.Instructions.OfType<IrCall>().Select(CallName),
      Is.EqualTo(new[] { "rt_str_const", "rt_str_len", "rt_str_free" }));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void AllocationSinking_GivenReadersInBothArms_WhenRun_ThenAllocationStaysBeforeBranch() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.Void, [condition]);
    var allocate = StringAllocator();
    var free = StringFree();
    var length = StringLength();
    var entry = fn.CreateBlock("entry");
    var yes = fn.CreateBlock("yes");
    var no = fn.CreateBlock("no");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    var text = be.Call(IrType.Ptr, allocate, new IrNullPtr(), IrBuilder.ConstI32(5));
    be.CondBr(condition, yes, no);
    var by = new IrBuilder(yes);
    by.Call(IrType.I32, length, text);
    by.Br(exit);
    var bn = new IrBuilder(no);
    bn.Call(IrType.I32, length, text);
    bn.Br(exit);
    var bx = new IrBuilder(exit);
    bx.Call(IrType.Void, free, text);
    bx.Ret();

    Assert.That(AllocationSinking.Run(fn), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().Single(), Is.SameAs(text));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void AllocationSinking_GivenObservableCallBeforeBranch_WhenRun_ThenAllocationDoesNotCrossIt() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.Void, [condition]);
    var allocate = StringAllocator();
    var free = StringFree();
    var length = StringLength();
    var sideEffect = new IrFunction("side_effect", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var rare = fn.CreateBlock("rare");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    var text = be.Call(IrType.Ptr, allocate, new IrNullPtr(), IrBuilder.ConstI32(5));
    be.Call(IrType.Void, sideEffect);
    be.CondBr(condition, rare, exit);
    var br = new IrBuilder(rare);
    br.Call(IrType.I32, length, text);
    br.Br(exit);
    var bx = new IrBuilder(exit);
    bx.Call(IrType.Void, free, text);
    bx.Ret();

    Assert.That(AllocationSinking.Run(fn), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().First(), Is.SameAs(text));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void AllocationSinking_GivenObservableWorkBeforeCleanup_WhenRun_ThenCleanupAndAllocationStayPut() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var fn = new IrFunction("f", IrType.Void, [condition]);
    var allocate = StringAllocator();
    var free = StringFree();
    var length = StringLength();
    var sideEffect = new IrFunction("side_effect", IrType.Void, []);
    var entry = fn.CreateBlock("entry");
    var rare = fn.CreateBlock("rare");
    var exit = fn.CreateBlock("exit");

    var be = new IrBuilder(entry);
    var text = be.Call(IrType.Ptr, allocate, new IrNullPtr(), IrBuilder.ConstI32(5));
    be.CondBr(condition, rare, exit);
    var br = new IrBuilder(rare);
    br.Call(IrType.I32, length, text);
    br.Br(exit);
    var bx = new IrBuilder(exit);
    bx.Call(IrType.Void, sideEffect);
    bx.Call(IrType.Void, free, text);
    bx.Ret();

    Assert.That(AllocationSinking.Run(fn), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().Single(), Is.SameAs(text));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  private static IrFunction StringAllocator() => new("rt_str_const", IrType.Ptr, [
    new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1),
  ]);

  private static IrFunction StringFree()
    => new("rt_str_free", IrType.Void, [new IrArgument(IrType.Ptr, 0)]);

  private static IrFunction StringLength()
    => new("rt_str_len", IrType.I32, [new IrArgument(IrType.Ptr, 0)]);

  private static string CallName(IrCall call) => ((IrFunction)call.Callee).Name;
}
