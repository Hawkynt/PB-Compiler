using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Regression tests for O0286's length-only string allocation elimination.</summary>
[TestFixture]
public sealed class O0286AllocationEliminationTests {

  [TestCase("rt_str_left")]
  [TestCase("rt_str_right")]
  public void EdgeSlice_GivenLengthOnlyUse_WhenRun_ThenAllocationBecomesClampedSourceLength(string sliceName) {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var sliceFn = Runtime(module, sliceName, IrType.Ptr, IrType.Ptr, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var count = new IrArgument(IrType.I32, 1, "count");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, count]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var slice = builder.Call(IrType.Ptr, sliceFn, source, count);
    builder.Ret(builder.Call(IrType.I32, lengthFn, slice));

    var changed = StringAllocationElimination.Run(module);

    Assert.That(changed, Is.EqualTo(1));
    Assert.That(Calls(function, sliceName), Is.Empty);
    var sourceLength = Calls(function, "rt_str_len").Single();
    Assert.That(sourceLength.GetOperand(1), Is.SameAs(source));
    Assert.That(function.AllInstructions.OfType<IrCmp>().Select(c => c.Pred),
      Is.SupersetOf(new[] { IrCmpPred.Slt, IrCmpPred.Sgt }));
    Assert.That(((IrRet)block.Terminator!).Value, Is.TypeOf<IrSelect>());
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Mid_GivenLengthOnlyUse_WhenRun_ThenStartAndCountRulesAreKeptInScalarIr() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var midFn = Runtime(module, "rt_str_mid", IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var start = new IrArgument(IrType.I32, 1, "start");
    var count = new IrArgument(IrType.I32, 2, "count");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, start, count]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var slice = builder.Call(IrType.Ptr, midFn, source, start, count);
    builder.Ret(builder.Call(IrType.I32, lengthFn, slice));

    Assert.That(StringAllocationElimination.Run(module), Is.EqualTo(1));

    Assert.That(Calls(function, "rt_str_mid"), Is.Empty);
    Assert.That(Calls(function, "rt_str_len").Single().GetOperand(1), Is.SameAs(source));
    var predicates = function.AllInstructions.OfType<IrCmp>().Select(c => c.Pred).ToArray();
    Assert.That(predicates, Does.Contain(IrCmpPred.Slt)); // start < 1
    Assert.That(predicates, Does.Contain(IrCmpPred.Sle)); // count <= 0
    Assert.That(predicates.Count(p => p == IrCmpPred.Sgt), Is.EqualTo(2)); // start past end, count past remainder
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void MidToEnd_GivenLengthOnlyUse_WhenRun_ThenAllocationBecomesRemainingLength() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var midFn = Runtime(module, "rt_str_mid2", IrType.Ptr, IrType.Ptr, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var start = new IrArgument(IrType.I32, 1, "start");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, start]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var slice = builder.Call(IrType.Ptr, midFn, source, start);
    builder.Ret(builder.Call(IrType.I32, lengthFn, slice));

    Assert.That(StringAllocationElimination.Run(module), Is.EqualTo(1));

    Assert.That(Calls(function, "rt_str_mid2"), Is.Empty);
    Assert.That(function.AllInstructions.OfType<IrBinary>().Select(binary => binary.Op),
      Is.SupersetOf(new[] { IrBinaryOp.Sub, IrBinaryOp.Add }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Slice_GivenAnotherUser_WhenRun_ThenItIsNotMaterializationFree() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var leftFn = Runtime(module, "rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var count = new IrArgument(IrType.I32, 1, "count");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, count]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var slice = builder.Call(IrType.Ptr, leftFn, source, count);
    var first = builder.Call(IrType.I32, lengthFn, slice);
    var second = builder.Call(IrType.I32, lengthFn, slice);
    builder.Ret(builder.Add(first, second));

    Assert.That(StringAllocationElimination.Run(module), Is.Zero);
    Assert.That(Calls(function, "rt_str_left"), Has.Count.EqualTo(1));
    Assert.That(Calls(function, "rt_str_len"), Has.Count.EqualTo(2));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void Slice_GivenLengthInLaterBlock_WhenRun_ThenSourceIsStillConsumedAtSliceSite() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var leftFn = Runtime(module, "rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32);
    var observeFn = Runtime(module, "observe", IrType.Void);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var count = new IrArgument(IrType.I32, 1, "count");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, count]));
    var entry = function.CreateBlock("entry");
    var tail = function.CreateBlock("tail");
    var entryBuilder = new IrBuilder(entry);
    var slice = entryBuilder.Call(IrType.Ptr, leftFn, source, count);
    var observed = entryBuilder.Call(IrType.Void, observeFn);
    entryBuilder.Br(tail);
    var tailBuilder = new IrBuilder(tail);
    tailBuilder.Ret(tailBuilder.Call(IrType.I32, lengthFn, slice));

    Assert.That(StringAllocationElimination.Run(module), Is.EqualTo(1));

    var sourceLength = Calls(function, "rt_str_len").Single();
    Assert.That(sourceLength.Parent, Is.SameAs(entry));
    var instructions = entry.Instructions.ToList();
    Assert.That(instructions.IndexOf(sourceLength), Is.LessThan(instructions.IndexOf(observed)));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  [Test]
  public void NestedSlices_GivenOnlyFinalLengthUse_WhenRun_ThenEachTemporaryIsEliminated() {
    var module = new IrModule("test");
    var lengthFn = Runtime(module, "rt_str_len", IrType.I32, IrType.Ptr);
    var leftFn = Runtime(module, "rt_str_left", IrType.Ptr, IrType.Ptr, IrType.I32);
    var rightFn = Runtime(module, "rt_str_right", IrType.Ptr, IrType.Ptr, IrType.I32);
    var source = new IrArgument(IrType.Ptr, 0, "source");
    var firstCount = new IrArgument(IrType.I32, 1, "firstCount");
    var secondCount = new IrArgument(IrType.I32, 2, "secondCount");
    var function = module.AddFunction(new IrFunction("f", IrType.I32, [source, firstCount, secondCount]));
    var block = function.CreateBlock("entry");
    var builder = new IrBuilder(block);
    var inner = builder.Call(IrType.Ptr, leftFn, source, firstCount);
    var outer = builder.Call(IrType.Ptr, rightFn, inner, secondCount);
    builder.Ret(builder.Call(IrType.I32, lengthFn, outer));

    Assert.That(StringAllocationElimination.Run(module), Is.EqualTo(2));
    Assert.That(Calls(function, "rt_str_left"), Is.Empty);
    Assert.That(Calls(function, "rt_str_right"), Is.Empty);
    Assert.That(Calls(function, "rt_str_len").Single().GetOperand(1), Is.SameAs(source));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  private static IrFunction Runtime(IrModule module, string name, IrType result, params IrType[] parameters)
    => module.AddFunction(new IrFunction(name, result,
      parameters.Select((type, index) => new IrArgument(type, index)).ToArray()));

  private static List<IrCall> Calls(IrFunction function, string name)
    => function.AllInstructions.OfType<IrCall>()
      .Where(call => call.Callee is IrFunction { Name: var called } && called == name)
      .ToList();
}
