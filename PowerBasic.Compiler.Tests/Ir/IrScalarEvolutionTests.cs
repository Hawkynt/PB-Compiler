using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class IrScalarEvolutionTests {

  [Test]
  public void Analyze_GivenCanonicalIncreasingLoop_ThenFindsAddRecurrenceAndExactTrips() {
    var function = BuildLoop(
      name: "increasing",
      start: 0,
      step: 1,
      predicate: IrCmpPred.Slt,
      limit: 4,
      continueOnTrue: true);
    var analyses = new IrAnalysisManager(function);
    var loops = analyses.Get(IrAnalyses.Loops);
    var evolution = analyses.Get(IrAnalyses.ScalarEvolution);
    var loop = loops.Loops.Single();
    var counter = loop.Header.Phis.Single();
    var recurrence = evolution.RecurrenceFor(counter);

    Assert.Multiple(() => {
      Assert.That(recurrence, Is.Not.Null);
      Assert.That(((IrConstantInt)recurrence!.Start).Value, Is.EqualTo(0));
      Assert.That(((IrConstantInt)recurrence.Step).Value, Is.EqualTo(1));
      Assert.That(evolution.ExactTripCount(loop), Is.EqualTo(4));
      Assert.That(evolution.RecurrencesFor(loop), Has.Count.EqualTo(1));
    });
  }

  [Test]
  public void Analyze_GivenDescendingLoop_ThenModelsNegativeStepAtMachineWidth() {
    var function = BuildLoop(
      name: "descending",
      start: 3,
      step: -1,
      predicate: IrCmpPred.Sge,
      limit: 0,
      continueOnTrue: true);
    var analyses = new IrAnalysisManager(function);
    var loop = analyses.Get(IrAnalyses.Loops).Loops.Single();
    var evolution = analyses.Get(IrAnalyses.ScalarEvolution);

    Assert.That(evolution.ExactTripCount(loop), Is.EqualTo(4));
  }

  [Test]
  public void Analyze_GivenExitOnTrue_ThenNegatesTheHeaderPredicateForTripCounting() {
    var function = BuildLoop(
      name: "falseContinues",
      start: 0,
      step: 1,
      predicate: IrCmpPred.Sge,
      limit: 4,
      continueOnTrue: false);
    var analyses = new IrAnalysisManager(function);
    var loop = analyses.Get(IrAnalyses.Loops).Loops.Single();
    var evolution = analyses.Get(IrAnalyses.ScalarEvolution);

    Assert.That(evolution.ExactTripCount(loop), Is.EqualTo(4));
  }

  [Test]
  public void Analyze_GivenInvariantNonConstantStep_ThenKeepsRecurrenceButDeclinesExactTrips() {
    var step = new IrArgument(IrType.I16, 0, "step");
    var function = new IrFunction("dynamicStep", IrType.Void, [step]);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var header = function.AddBlock(new IrBasicBlock("header"));
    var body = function.AddBlock(new IrBasicBlock("body"));
    var exit = function.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 8)));
    header.Append(new IrCondBr(test, body, exit));
    var next = body.Append(new IrBinary(IrBinaryOp.Add, counter, step));
    body.Append(new IrBr(header));
    counter.AddIncoming(next, body);
    exit.Append(new IrRet());

    var analyses = new IrAnalysisManager(function);
    var loop = analyses.Get(IrAnalyses.Loops).Loops.Single();
    var evolution = analyses.Get(IrAnalyses.ScalarEvolution);

    Assert.Multiple(() => {
      Assert.That(evolution.RecurrenceFor(counter), Is.Not.Null);
      Assert.That(evolution.ExactTripCount(loop), Is.Null);
      Assert.That(IrScalarEvolution.IsLoopInvariant(step, loop), Is.True);
    });
  }

  private static IrFunction BuildLoop(
      string name, long start, long step, IrCmpPred predicate, long limit, bool continueOnTrue) {
    var function = new IrFunction(name, IrType.Void);
    var entry = function.AddBlock(new IrBasicBlock("entry"));
    var header = function.AddBlock(new IrBasicBlock("header"));
    var body = function.AddBlock(new IrBasicBlock("body"));
    var exit = function.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    counter.AddIncoming(new IrConstantInt(IrType.I16, start), entry);
    var test = header.Append(new IrCmp(predicate, counter, new IrConstantInt(IrType.I16, limit)));
    header.Append(continueOnTrue
      ? new IrCondBr(test, body, exit)
      : new IrCondBr(test, exit, body));

    var next = body.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, step)));
    body.Append(new IrBr(header));
    counter.AddIncoming(next, body);
    exit.Append(new IrRet());
    return function;
  }
}
