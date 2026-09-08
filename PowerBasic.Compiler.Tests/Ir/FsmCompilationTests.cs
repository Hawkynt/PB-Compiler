using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0336 — finite-state-machine compilation for dense byte classifiers.</summary>
[TestFixture]
public sealed class FsmCompilationTests {

  [Test]
  public void ClassificationChain_GivenThreeDenseAsciiClasses_ThenItBecomesAClassTableAndCompactDispatch() {
    var module = new IrModule("test");
    var c = new IrArgument(IrType.I8, 0, "c");
    var fn = module.AddFunction(new IrFunction("classify", IrType.I16, [c]));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var upperTest = fn.AddBlock(new IrBasicBlock("upper.test"));
    var lowerTest = fn.AddBlock(new IrBasicBlock("lower.test"));
    var digit = fn.AddBlock(new IrBasicBlock("digit"));
    var upper = fn.AddBlock(new IrBasicBlock("upper"));
    var lower = fn.AddBlock(new IrBasicBlock("lower"));
    var other = fn.AddBlock(new IrBasicBlock("other"));

    AppendRangeTest(head, c, '0', '9', digit, upperTest);
    AppendRangeTest(upperTest, c, 'A', 'Z', upper, lowerTest);
    AppendRangeTest(lowerTest, c, 'a', 'z', lower, other);
    digit.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    upper.Append(new IrRet(new IrConstantInt(IrType.I16, 2)));
    lower.Append(new IrRet(new IrConstantInt(IrType.I16, 3)));
    other.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));
    EnableSpeed(module);

    Assert.That(SwitchFormation.Run(fn), Is.EqualTo(2), "recover one switch, then compile that classifier table");
    Dce.Run(fn);

    var table = module.Globals.Single(global => global.Name.StartsWith(".fsm.classify.", StringComparison.Ordinal));
    var dispatch = (IrSwitch)head.Terminator!;
    Assert.Multiple(() => {
      Assert.That(table.ValueType.SameStorage(IrType.U8), Is.True);
      Assert.That(table.Bytes, Has.Length.EqualTo(256));
      Assert.That(dispatch.Condition, Is.InstanceOf<IrLoad>());
      Assert.That(dispatch.Cases, Has.Count.EqualTo(3), "four semantic classes need only three explicit class ids");
      Assert.That(TargetFor(table, dispatch, '0'), Is.SameAs(digit));
      Assert.That(TargetFor(table, dispatch, 'A'), Is.SameAs(upper));
      Assert.That(TargetFor(table, dispatch, 'z'), Is.SameAs(lower));
      Assert.That(TargetFor(table, dispatch, 0), Is.SameAs(other));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void ByteSwitch_GivenSignedCaseSpellings_ThenEveryTableEntryUsesTheEightBitPattern() {
    var module = new IrModule("test");
    var c = new IrArgument(IrType.I8, 0, "c");
    var fn = module.AddFunction(new IrFunction("signed", IrType.I16, [c]));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var low = fn.AddBlock(new IrBasicBlock("low"));
    var negative = fn.AddBlock(new IrBasicBlock("negative"));
    var other = fn.AddBlock(new IrBasicBlock("other"));
    var source = new IrSwitch(c, other);
    for (var value = 0; value < 16; ++value)
      source.AddCase(value, low);
    for (var value = -16; value < 0; ++value)
      source.AddCase(value, negative);
    head.Append(source);
    low.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    negative.Append(new IrRet(new IrConstantInt(IrType.I16, 2)));
    other.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));
    EnableSpeed(module);

    Assert.That(FsmCompilation.Run(module, fn), Is.EqualTo(1));

    var table = module.Globals.Single();
    var dispatch = (IrSwitch)head.Terminator!;
    Assert.Multiple(() => {
      Assert.That(TargetFor(table, dispatch, 0), Is.SameAs(low));
      Assert.That(TargetFor(table, dispatch, 127), Is.SameAs(other));
      Assert.That(TargetFor(table, dispatch, 240), Is.SameAs(negative), "240 is the i8 pattern for -16");
      Assert.That(TargetFor(table, dispatch, 255), Is.SameAs(negative), "255 is the i8 pattern for -1");
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void ByteSwitch_GivenSparseCases_ThenA256ByteTableIsNotProfitable() {
    var module = new IrModule("test");
    var c = new IrArgument(IrType.U8, 0, "c");
    var fn = module.AddFunction(new IrFunction("sparse", IrType.I16, [c]));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var hit = fn.AddBlock(new IrBasicBlock("hit"));
    var other = fn.AddBlock(new IrBasicBlock("other"));
    var source = new IrSwitch(c, other);
    source.AddCase(1, hit);
    source.AddCase(7, hit);
    source.AddCase(42, hit);
    head.Append(source);
    hit.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    other.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));
    EnableSpeed(module);

    Assert.Multiple(() => {
      Assert.That(FsmCompilation.Run(module, fn), Is.Zero);
      Assert.That(module.Globals, Is.Empty);
      Assert.That(head.Terminator, Is.SameAs(source));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void ByteSwitch_GivenSizeObjective_ThenDenseClassifierDoesNotGrowStaticData() {
    var module = new IrModule("test");
    var c = new IrArgument(IrType.U8, 0, "c");
    var fn = module.AddFunction(new IrFunction("size", IrType.I16, [c]));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var hit = fn.AddBlock(new IrBasicBlock("hit"));
    var other = fn.AddBlock(new IrBasicBlock("other"));
    var source = new IrSwitch(c, other);
    for (var value = 0; value < 32; ++value)
      source.AddCase(value, hit);
    head.Append(source);
    hit.Append(new IrRet(new IrConstantInt(IrType.I16, 1)));
    other.Append(new IrRet(new IrConstantInt(IrType.I16, 0)));

    Assert.Multiple(() => {
      Assert.That(module.OptimizeForSpeed, Is.False);
      Assert.That(FsmCompilation.Run(module, fn), Is.Zero);
      Assert.That(module.Globals, Is.Empty);
      Assert.That(head.Terminator, Is.SameAs(source));
    });
  }

  private static void EnableSpeed(IrModule module)
    => new IrPassManager { OptimizeForSpeed = true }.RunOnModule(module);

  private static void AppendRangeTest(IrBasicBlock block, IrValue value, char lo, char hi,
      IrBasicBlock hit, IrBasicBlock miss) {
    var lower = block.Append(new IrCmp(IrCmpPred.Sge, value, new IrConstantInt(value.Type, lo)));
    var upper = block.Append(new IrCmp(IrCmpPred.Sle, value, new IrConstantInt(value.Type, hi)));
    var inside = block.Append(new IrBinary(IrBinaryOp.And, lower, upper));
    block.Append(new IrCondBr(inside, hit, miss));
  }

  private static IrBasicBlock TargetFor(IrGlobalVariable table, IrSwitch dispatch, int input)
    => dispatch.TargetFor(table.Bytes![input]);
}
