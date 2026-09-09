using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class StringAllocationCoalescingTests {

  [Test]
  public void Run_GivenThreeBorrowedBoundedSubstrings_ThenOneRegionReplacesTheOwnershipCopies() {
    var (module, fn, entry, source, dup, left, right, mid, free) = SubstringFixture();
    var b = new IrBuilder(entry);
    var a = b.Call(IrType.Ptr, left, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(10));
    var c = b.Call(IrType.Ptr, mid, b.Call(IrType.Ptr, dup, source),
      IrBuilder.ConstI32(11), IrBuilder.ConstI32(10));
    var d = b.Call(IrType.Ptr, right, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(10));
    b.Call(IrType.Void, free, a);
    b.Call(IrType.Void, free, c);
    b.Call(IrType.Void, free, d);
    b.Ret();

    var changed = StringAllocationCoalescing.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    var calls = entry.Instructions.OfType<IrCall>().ToList();
    Assert.That(calls.Select(CallName), Is.EqualTo(new[] {
      "rt_str_coalesce_begin",
      "rt_str_left_borrow_coalesced",
      "rt_str_mid_borrow_coalesced",
      "rt_str_right_borrow_coalesced",
      "rt_str_free", "rt_str_free", "rt_str_free",
      "rt_str_coalesce_end",
    }));
    Assert.That(calls.Count(call => CallName(call) == "rt_str_dup"), Is.Zero);
    Assert.That(calls[1].GetOperand(1), Is.SameAs(source));
    Assert.That(calls[2].GetOperand(1), Is.SameAs(source));
    Assert.That(calls[3].GetOperand(1), Is.SameAs(source));
    Assert.That(calls[0].GetOperand(1), Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)calls[0].GetOperand(1)).Value, Is.EqualTo(42)); // 3 * (4-byte header + 10 bytes)
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenAnObservableCallBetweenTwoAndOneAllocations_ThenNoRegionCrossesTheBarrier() {
    var (module, fn, entry, source, dup, left, right, mid, _) = SubstringFixture();
    var sideEffect = module.AddFunction(new IrFunction("side_effect", IrType.Void, []));
    var b = new IrBuilder(entry);
    b.Call(IrType.Ptr, left, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(4));
    b.Call(IrType.Ptr, right, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(4));
    b.Call(IrType.Void, sideEffect);
    b.Call(IrType.Ptr, mid, b.Call(IrType.Ptr, dup, source),
      IrBuilder.ConstI32(2), IrBuilder.ConstI32(4));
    b.Ret();

    Assert.That(StringAllocationCoalescing.Run(module), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().Select(CallName), Does.Not.Contain("rt_str_coalesce_begin"));
    Assert.That(entry.Instructions.OfType<IrCall>().Count(call => CallName(call) == "rt_str_dup"), Is.EqualTo(3));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenSpaceStringAndChrWithStaticBounds_ThenTheirWorstCaseBytesAreReservedTogether() {
    var module = new IrModule("test");
    var space = module.AddFunction(new IrFunction("rt_str_space", IrType.Ptr,
      [new IrArgument(IrType.I32, 0)]));
    var fill = module.AddFunction(new IrFunction("rt_str_string", IrType.Ptr,
      [new IrArgument(IrType.I32, 0), new IrArgument(IrType.I32, 1)]));
    var chr = module.AddFunction(new IrFunction("rt_str_chr", IrType.Ptr,
      [new IrArgument(IrType.I32, 0)]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, []));
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Call(IrType.Ptr, space, IrBuilder.ConstI32(4));
    b.Call(IrType.Ptr, fill, IrBuilder.ConstI32(5), IrBuilder.ConstI32('x'));
    b.Call(IrType.Ptr, chr, IrBuilder.ConstI32('!'));
    b.Ret();

    Assert.That(StringAllocationCoalescing.Run(module), Is.EqualTo(1));
    var calls = entry.Instructions.OfType<IrCall>().ToList();
    Assert.That(calls.Select(CallName), Is.EqualTo(new[] {
      "rt_str_coalesce_begin", "rt_str_space_coalesced", "rt_str_string_coalesced",
      "rt_str_chr_coalesced", "rt_str_coalesce_end",
    }));
    Assert.That(((IrConstantInt)calls[0].GetOperand(1)).Value, Is.EqualTo(22));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenAnAlreadyCoalescedFunction_ThenASecondSweepIsIdempotent() {
    var (module, fn, entry, source, dup, left, right, mid, _) = SubstringFixture();
    var b = new IrBuilder(entry);
    b.Call(IrType.Ptr, left, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(3));
    b.Call(IrType.Ptr, mid, b.Call(IrType.Ptr, dup, source),
      IrBuilder.ConstI32(2), IrBuilder.ConstI32(3));
    b.Call(IrType.Ptr, right, b.Call(IrType.Ptr, dup, source), IrBuilder.ConstI32(3));
    b.Ret();

    Assert.That(StringAllocationCoalescing.Run(module), Is.EqualTo(1));
    Assert.That(StringAllocationCoalescing.Run(module), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().Count(call => CallName(call) == "rt_str_coalesce_begin"), Is.EqualTo(1));
    Assert.That(entry.Instructions.OfType<IrCall>().Count(call => CallName(call) == "rt_str_coalesce_end"), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Run_GivenAFunctionWithNonLocalErrorControlFlow_ThenItIsLeftUntouched() {
    var module = new IrModule("test");
    var chr = module.AddFunction(new IrFunction("rt_str_chr", IrType.Ptr,
      [new IrArgument(IrType.I32, 0)]));
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, []));
    fn.HasErrorHandler = true;
    var entry = fn.CreateBlock("entry");
    var b = new IrBuilder(entry);
    b.Call(IrType.Ptr, chr, IrBuilder.ConstI32('a'));
    b.Call(IrType.Ptr, chr, IrBuilder.ConstI32('b'));
    b.Call(IrType.Ptr, chr, IrBuilder.ConstI32('c'));
    b.Ret();

    Assert.That(StringAllocationCoalescing.Run(module), Is.Zero);
    Assert.That(entry.Instructions.OfType<IrCall>().Select(CallName), Is.All.EqualTo("rt_str_chr"));
  }

  private static (IrModule Module, IrFunction Function, IrBasicBlock Entry, IrArgument Source,
    IrFunction Dup, IrFunction Left, IrFunction Right, IrFunction Mid, IrFunction Free) SubstringFixture() {
    var module = new IrModule("test");
    var dup = module.AddFunction(new IrFunction("rt_str_dup", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0)]));
    var left = module.AddFunction(new IrFunction("rt_str_left", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var right = module.AddFunction(new IrFunction("rt_str_right", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var mid = module.AddFunction(new IrFunction("rt_str_mid", IrType.Ptr,
      [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1), new IrArgument(IrType.I32, 2)]));
    var free = module.AddFunction(new IrFunction("rt_str_free", IrType.Void,
      [new IrArgument(IrType.Ptr, 0)]));
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var fn = module.AddFunction(new IrFunction("f", IrType.Void, [source]));
    return (module, fn, fn.CreateBlock("entry"), source, dup, left, right, mid, free);
  }

  private static string CallName(IrCall call) => ((IrFunction)call.Callee).Name;
}
