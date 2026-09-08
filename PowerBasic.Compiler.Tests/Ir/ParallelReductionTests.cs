using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0312 — reduction recognition for hosted parallel-loop lowering. The tests deliberately exercise
/// the proof boundary: associative integer folds are descriptors; floats, non-associative operators
/// and observable running totals are not.
/// </summary>
[TestFixture]
public sealed class ParallelReductionTests {

  [TestCase(IrBinaryOp.Add, 0L)]
  [TestCase(IrBinaryOp.Mul, 1L)]
  [TestCase(IrBinaryOp.And, -1L)]
  [TestCase(IrBinaryOp.Or, 0L)]
  [TestCase(IrBinaryOp.Xor, 0L)]
  public void Reduction_GivenAnAssociativeIntegerOperator_ThenItIsRecognized(IrBinaryOp op, long identity) {
    var fixture = Build(op);

    var reductions = ParallelReduction.Analyze(fixture.Fn);

    Assert.That(reductions, Has.Count.EqualTo(1));
    var reduction = reductions[0];
    Assert.Multiple(() => {
      Assert.That(reduction.Accumulator, Is.SameAs(fixture.Accumulator));
      Assert.That(reduction.Update, Is.SameAs(fixture.Update));
      Assert.That(reduction.Input, Is.SameAs(fixture.Input));
      Assert.That(reduction.Identity.Value, Is.EqualTo(identity));
      Assert.That(reduction.Trips, Is.EqualTo(64));
      Assert.That(IrVerifier.Verify(fixture.Fn), Is.Empty);
    });
  }

  [Test]
  public void Reduction_GivenTheAccumulatorIsTheRightOperand_ThenItIsRecognized() {
    var fixture = Build(IrBinaryOp.Xor, commute: true);

    var reduction = ParallelReduction.Analyze(fixture.Fn).Single();

    Assert.That(reduction.Input, Is.SameAs(fixture.Input));
  }

  [Test]
  public void Reduction_GivenAnUnsignedAnd_ThenItsIdentityIsTheAllOnesPattern() {
    var fixture = Build(IrBinaryOp.And, type: IrType.U16);

    var reduction = ParallelReduction.Analyze(fixture.Fn).Single();

    Assert.That(reduction.Identity.ZeroExtended, Is.EqualTo(0xffffUL));
  }

  [TestCase(IrBinaryOp.Sub)]
  [TestCase(IrBinaryOp.SDiv)]
  [TestCase(IrBinaryOp.SRem)]
  [TestCase(IrBinaryOp.Shl)]
  public void Reduction_GivenANonAssociativeOperator_ThenItIsRejected(IrBinaryOp op) {
    var fixture = Build(op);

    Assert.That(ParallelReduction.Analyze(fixture.Fn), Is.Empty);
  }

  [Test]
  public void Reduction_GivenFloatingPointAddition_ThenItIsRejected() {
    var fixture = Build(IrBinaryOp.FAdd, type: IrType.F64);

    Assert.That(ParallelReduction.Analyze(fixture.Fn), Is.Empty,
      "parallel regrouping changes floating-point rounding without an explicit relaxed-FP contract");
  }

  [Test]
  public void Reduction_GivenTheRunningAccumulatorIsReadInsideTheLoop_ThenItIsRejected() {
    var fixture = Build(IrBinaryOp.Add, observeAccumulator: true);

    Assert.That(ParallelReduction.Analyze(fixture.Fn), Is.Empty);
  }

  [Test]
  public void Reduction_GivenItsInputDependsOnTheAccumulator_ThenItIsRejected() {
    var fixture = Build(IrBinaryOp.Add, inputDependsOnAccumulator: true);

    Assert.That(ParallelReduction.Analyze(fixture.Fn), Is.Empty);
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenAnalysisDeclines() {
    var fixture = Build(IrBinaryOp.Add);
    fixture.Fn.HasErrorHandler = true;

    Assert.That(ParallelReduction.Analyze(fixture.Fn), Is.Empty);
  }

  [Test]
  public void Analyze_GivenNull_ThenItThrows() {
    Assert.That(() => ParallelReduction.Analyze(null!), Throws.ArgumentNullException);
  }

  private static Fixture Build(IrBinaryOp op, IrType? type = null, bool commute = false,
      bool observeAccumulator = false, bool inputDependsOnAccumulator = false) {
    type ??= IrType.I16;
    var fn = new IrFunction("reduction", type);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var header = fn.AddBlock(new IrBasicBlock("header"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var accumulator = header.AppendPhi(new IrPhi(type) { Name = "acc" });
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 64)));
    header.Append(new IrCondBr(test, body, exit));

    var nextCounter = body.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    IrValue input = type.IsFloat
      ? new IrConstantFloat(type, 1.25)
      : body.Append(new IrBinary(IrBinaryOp.Xor, counter, new IrConstantInt(type, 7)));
    if (inputDependsOnAccumulator)
      input = body.Append(new IrBinary(type.IsFloat ? IrBinaryOp.FAdd : IrBinaryOp.Add, accumulator, input));
    if (observeAccumulator)
      body.Append(new IrBinary(type.IsFloat ? IrBinaryOp.FAdd : IrBinaryOp.Add, accumulator,
        type.IsFloat ? new IrConstantFloat(type, 2) : new IrConstantInt(type, 2)));

    var update = body.Append(commute
      ? new IrBinary(op, input, accumulator)
      : new IrBinary(op, accumulator, input));
    body.Append(new IrBr(header));
    exit.Append(new IrRet(accumulator));

    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    counter.AddIncoming(nextCounter, body);
    accumulator.AddIncoming(type.IsFloat ? new IrConstantFloat(type, 0) : new IrConstantInt(type, 0), entry);
    accumulator.AddIncoming(update, body);

    return new(fn, accumulator, update, input);
  }

  private sealed record Fixture(IrFunction Fn, IrPhi Accumulator, IrBinary Update, IrValue Input);
}
