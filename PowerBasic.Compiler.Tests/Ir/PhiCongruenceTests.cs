using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0111 — two loop-carried values that advance in lockstep are one value written twice.
///
/// The interesting property is that it has to start OPTIMISTICALLY: a loop phi's incoming value on the
/// latch edge is derived from the phi itself, so a pessimistic proof is circular and concludes nothing.
/// Assuming congruence and splitting on disagreement is what lets the cyclic case be proved at all,
/// and it is why GVN - which numbers an instruction from its operands - skips phis entirely.
/// </summary>
[TestFixture]
public sealed class PhiCongruenceTests {

  /// <summary>A loop with two counters started and stepped identically.</summary>
  private static (IrFunction Fn, IrPhi First, IrPhi Second) TwinCounters(long secondStart, long secondStep) {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));

    entry.Append(new IrBr(head));
    var i = head.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var j = head.AppendPhi(new IrPhi(IrType.I16) { Name = "j" });
    head.Append(new IrCondBr(head.Append(new IrCmp(IrCmpPred.Slt, i, new IrConstantInt(IrType.I16, 10))), body, exit));

    var nextI = body.Append(new IrBinary(IrBinaryOp.Add, i, new IrConstantInt(IrType.I16, 1)));
    var nextJ = body.Append(new IrBinary(IrBinaryOp.Add, j, new IrConstantInt(IrType.I16, secondStep)));
    body.Append(new IrBr(head));
    exit.Append(new IrRet());

    i.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    i.AddIncoming(nextI, body);
    j.AddIncoming(new IrConstantInt(IrType.I16, secondStart), entry);
    j.AddIncoming(nextJ, body);
    return (fn, i, j);
  }

  [Test]
  public void Phis_GivenTwoCountersInLockstep_ThenOneIsEliminated() {
    var (fn, first, second) = TwinCounters(secondStart: 0, secondStep: 1);

    Assert.That(PhiCongruence.Run(fn), Is.EqualTo(1));
    Assert.That(second.Parent, Is.Null, "the second counter is the first");
    Assert.That(first.Parent, Is.Not.Null);
  }

  /// <summary>
  /// Equal steps from different starting values are the offset case, not a distinct induction: the
  /// second counter is the first plus a constant, so it is folded away and rebuilt where it is read.
  /// Only a different STEP keeps two counters apart - see the test below.
  /// </summary>
  [Test]
  public void Phis_GivenDifferentStartingValues_ThenTheOffsetOneIsFolded() {
    var (fn, first, second) = TwinCounters(secondStart: 100, secondStep: 1);

    Assert.That(PhiCongruence.Run(fn), Is.EqualTo(1));
    Assert.That(second.Parent, Is.Null);
    Assert.That(first.Parent, Is.Not.Null);
  }

  [Test]
  public void Phis_GivenDifferentSteps_ThenBothSurvive() {
    var (fn, _, second) = TwinCounters(secondStart: 0, secondStep: 2);

    Assert.That(PhiCongruence.Run(fn), Is.Zero);
    Assert.That(second.Parent, Is.Not.Null);
  }

  /// <summary>
  /// The case a pessimistic analysis cannot reach: each phi's latch value is the OTHER phi, so neither
  /// can be proved equal without first assuming it. Starting from "all congruent" and splitting only on
  /// evidence gets there; starting from "none congruent" never does.
  /// </summary>
  [Test]
  public void Phis_GivenTheyCarryEachOther_ThenTheCycleIsStillProved() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(head));

    var a = head.AppendPhi(new IrPhi(IrType.I16) { Name = "a" });
    var b = head.AppendPhi(new IrPhi(IrType.I16) { Name = "b" });
    head.Append(new IrCondBr(head.Append(new IrCmp(IrCmpPred.Slt, a, new IrConstantInt(IrType.I16, 4))), head, exit));
    exit.Append(new IrRet());

    var zero = new IrConstantInt(IrType.I16, 0);
    a.AddIncoming(zero, entry);
    a.AddIncoming(b, head);                      // a takes b's value round the loop
    b.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    b.AddIncoming(a, head);                      // and b takes a's

    Assert.That(PhiCongruence.Run(fn), Is.EqualTo(1));
    Assert.That(b.Parent, Is.Null);
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var (fn, _, second) = TwinCounters(secondStart: 0, secondStep: 1);
    fn.HasErrorHandler = true;

    Assert.That(PhiCongruence.Run(fn), Is.Zero);
    Assert.That(second.Parent, Is.Not.Null);
  }
}
