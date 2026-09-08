using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0332 — range-aware compile-time lookup-table generation.</summary>
[TestFixture]
public sealed class LookupTableGenerationTests {

  [Test]
  public void PureByteFunction_GivenOneDynamicAndOneConstantCall_ThenItDoesNotSpendAFullTable() {
    var module = new IrModule("test");
    var transform = AddByteTransform(module, "transform");

    var input = new IrArgument(IrType.U8, 0, "input");
    var caller = module.AddFunction(new IrFunction("caller", IrType.U8, [input]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var dynamicCall = entry.Append(new IrCall(IrType.U8, transform, [input]));
    var constantCall = entry.Append(new IrCall(IrType.U8, transform, [new IrConstantInt(IrType.U8, 7)]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Xor, dynamicCall, constantCall))));

    Assert.That(LookupTableGeneration.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.Globals.Any(global => global.Name.StartsWith(".lut.transform", StringComparison.Ordinal)), Is.False);
      Assert.That(caller.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  [Test]
  public void PureWordFunction_GivenRepeatedCallsInAProvenSixteenValueRange_ThenItBuildsOnlyThatRange() {
    var module = new IrModule("test");
    var transform = AddWordTransform(module, "wordTransform");

    var input = new IrArgument(IrType.U16, 0, "input");
    var caller = module.AddFunction(new IrFunction("caller", IrType.U8, [input]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var masked = entry.Append(new IrBinary(IrBinaryOp.And, input, new IrConstantInt(IrType.U16, 15)));
    var bounded = entry.Append(new IrBinary(IrBinaryOp.Add, masked, new IrConstantInt(IrType.U16, 10)));
    var first = entry.Append(new IrCall(IrType.U8, transform, [bounded]));
    var second = entry.Append(new IrCall(IrType.U8, transform, [bounded]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Xor, first, second))));

    Assert.That(LookupTableGeneration.Run(module), Is.EqualTo(1));
    var table = module.Globals.Single(global => global.Name.StartsWith(".lut.wordTransform.", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(table.Count, Is.EqualTo(16));
      Assert.That(table.Bytes, Has.Length.EqualTo(16));
      Assert.That(table.Bytes![0], Is.EqualTo(WordTransform(10)));
      Assert.That(table.Bytes[15], Is.EqualTo(WordTransform(25)));
      Assert.That(caller.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(caller.AllInstructions.OfType<IrLoad>().Count(), Is.EqualTo(2));
      Assert.That(caller.AllInstructions.OfType<IrBinary>().Count(binary =>
        binary.Op == IrBinaryOp.Sub && binary.Rhs is IrConstantInt { Value: 10 }), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  [Test]
  public void PureByteFunction_GivenAConditionalAndPhi_ThenTheEvaluatorFollowsTheExecutedPath() {
    var module = new IrModule("test");
    var x = new IrArgument(IrType.U8, 0, "x");
    var transform = module.AddFunction(new IrFunction("branching", IrType.U8, [x]));
    var entry = transform.AddBlock(new IrBasicBlock("entry"));
    var low = transform.AddBlock(new IrBasicBlock("low"));
    var high = transform.AddBlock(new IrBasicBlock("high"));
    var exit = transform.AddBlock(new IrBasicBlock("exit"));

    var isLow = entry.Append(new IrCmp(IrCmpPred.Ult, x, new IrConstantInt(IrType.U8, 128)));
    entry.Append(new IrCondBr(isLow, low, high));
    var lowValue = low.Append(new IrBinary(IrBinaryOp.Add, x, new IrConstantInt(IrType.U8, 1)));
    low.Append(new IrBr(exit));
    var highValue = high.Append(new IrBinary(IrBinaryOp.Sub, x, new IrConstantInt(IrType.U8, 1)));
    high.Append(new IrBr(exit));

    var merged = exit.Append(new IrPhi(IrType.U8));
    merged.AddIncoming(lowValue, low);
    merged.AddIncoming(highValue, high);
    IrValue value = exit.Append(new IrBinary(IrBinaryOp.Xor, merged, new IrConstantInt(IrType.U8, 0x5a)));
    value = exit.Append(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.U8, 3)));
    value = exit.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U8, 7)));
    value = exit.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0xa5)));
    exit.Append(new IrRet(value));

    var input = new IrArgument(IrType.U8, 0, "input");
    var caller = module.AddFunction(new IrFunction("caller", IrType.U8, [input]));
    var callBlock = caller.AddBlock(new IrBasicBlock("entry"));
    var first = callBlock.Append(new IrCall(IrType.U8, transform, [input]));
    var second = callBlock.Append(new IrCall(IrType.U8, transform, [input]));
    callBlock.Append(new IrRet(callBlock.Append(new IrBinary(IrBinaryOp.Xor, first, second))));

    Assert.That(LookupTableGeneration.Run(module), Is.EqualTo(1));
    var table = module.FindGlobal(".lut.branching");
    Assert.That(table, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(table!.Bytes![0], Is.EqualTo(BranchTransform(0)));
      Assert.That(table.Bytes[127], Is.EqualTo(BranchTransform(127)));
      Assert.That(table.Bytes[128], Is.EqualTo(BranchTransform(128)));
      Assert.That(table.Bytes[255], Is.EqualTo(BranchTransform(255)));
      Assert.That(caller.AllInstructions.OfType<IrCall>(), Is.Empty);
      Assert.That(IrVerifier.Verify(transform), Is.Empty);
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  [Test]
  public void PureByteFunction_GivenADivisionThatTrapsForOneInput_ThenItKeepsTheRuntimeCalls() {
    var module = new IrModule("test");
    var x = new IrArgument(IrType.U8, 0, "x");
    var transform = module.AddFunction(new IrFunction("mayTrap", IrType.U8, [x]));
    var body = transform.AddBlock(new IrBasicBlock("entry"));
    var divisor = body.Append(new IrBinary(IrBinaryOp.And, x, new IrConstantInt(IrType.U8, 1)));
    IrValue value = x;
    for (var i = 0; i < 4; ++i)
      value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0)));
    value = body.Append(new IrBinary(IrBinaryOp.UDiv, value, divisor));
    body.Append(new IrRet(value));

    var input = new IrArgument(IrType.U8, 0, "input");
    var caller = module.AddFunction(new IrFunction("caller", IrType.U8, [input]));
    var entry = caller.AddBlock(new IrBasicBlock("entry"));
    var first = entry.Append(new IrCall(IrType.U8, transform, [input]));
    var second = entry.Append(new IrCall(IrType.U8, transform, [input]));
    entry.Append(new IrRet(entry.Append(new IrBinary(IrBinaryOp.Xor, first, second))));

    Assert.That(LookupTableGeneration.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal(".lut.mayTrap"), Is.Null);
      Assert.That(caller.AllInstructions.OfType<IrCall>().Count(), Is.EqualTo(2));
      Assert.That(IrVerifier.Verify(caller), Is.Empty);
    });
  }

  private static IrFunction AddByteTransform(IrModule module, string name) {
    var x = new IrArgument(IrType.U8, 0, "x");
    var transform = module.AddFunction(new IrFunction(name, IrType.U8, [x]));
    var body = transform.AddBlock(new IrBasicBlock("entry"));
    IrValue value = body.Append(new IrBinary(IrBinaryOp.Mul, x, new IrConstantInt(IrType.U8, 3)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U8, 5)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0x33)));
    value = body.Append(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.U8, 7)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U8, 9)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U8, 0x55)));
    body.Append(new IrRet(value));
    return transform;
  }

  private static IrFunction AddWordTransform(IrModule module, string name) {
    var x = new IrArgument(IrType.U16, 0, "x");
    var transform = module.AddFunction(new IrFunction(name, IrType.U8, [x]));
    var body = transform.AddBlock(new IrBasicBlock("entry"));
    IrValue value = body.Append(new IrBinary(IrBinaryOp.Mul, x, new IrConstantInt(IrType.U16, 3)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U16, 5)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U16, 0x33)));
    value = body.Append(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.U16, 7)));
    value = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.U16, 9)));
    value = body.Append(new IrBinary(IrBinaryOp.Xor, value, new IrConstantInt(IrType.U16, 0x55)));
    value = body.Append(new IrCast(IrCastOp.Trunc, value, IrType.U8));
    body.Append(new IrRet(value));
    return transform;
  }

  private static byte WordTransform(ushort input) {
    var value = unchecked((ushort)(input * 3));
    value = unchecked((ushort)(value + 5));
    value ^= 0x33;
    value = unchecked((ushort)(value * 7));
    value = unchecked((ushort)(value + 9));
    value ^= 0x55;
    return unchecked((byte)value);
  }

  private static byte BranchTransform(byte input) {
    var value = input < 128 ? unchecked((byte)(input + 1)) : unchecked((byte)(input - 1));
    value ^= 0x5a;
    value = unchecked((byte)(value * 3));
    value = unchecked((byte)(value + 7));
    value ^= 0xa5;
    return value;
  }
}
