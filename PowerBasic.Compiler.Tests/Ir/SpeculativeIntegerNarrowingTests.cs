using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0309 — one scalar range guard versions a canonical loop so several 32-bit operations can run
/// at word width, while the original loop remains the exact fallback for values outside the guard.
/// </summary>
[TestFixture]
public sealed class SpeculativeIntegerNarrowingTests {

  [Test]
  public void Loop_GivenOneInvariantWideScalarUsedSeveralTimes_WhenNarrowed_ThenOneGuardSelectsANarrowFastClone() {
    var value = new IrArgument(IrType.I32, 0, "value");
    var fn = new IrFunction("f", IrType.I16, [value]);
    var (entry, header, body, exit, counter) = CreateLoop(fn);

    var scaled = body.Append(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(IrType.I32, 1)));
    var above = body.Append(new IrCmp(IrCmpPred.Sgt, scaled, new IrConstantInt(IrType.I32, 10)));
    var below = body.Append(new IrCmp(IrCmpPred.Slt, scaled, new IrConstantInt(IrType.I32, 20)));
    var both = body.Append(new IrBinary(IrBinaryOp.And, above, below));
    CloseLoop(body, header, counter, both);
    exit.Append(new IrRet(counter));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(SpeculativeIntegerNarrowing.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var chooser = entry.Terminator as IrCondBr;
    Assert.That(chooser, Is.Not.Null);
    Assert.That(chooser!.IfFalse, Is.SameAs(header), "the original wide loop is the slow fallback");
    Assert.That(chooser.IfTrue.Label, Is.EqualTo("narrow.loop"));

    var guardPredicates = entry.Instructions.OfType<IrCmp>().Select(cmp => cmp.Pred).ToList();
    Assert.That(guardPredicates, Is.EquivalentTo(new[] { IrCmpPred.Sge, IrCmpPred.Sle }),
      "a signed LONG needs only the two word-bound checks in the preheader");

    var fastBody = fn.Blocks.Single(block => block.Label == "narrow.body");
    Assert.That(fastBody.Instructions.OfType<IrBinary>(),
      Has.Some.Matches<IrBinary>(binary => binary.Op == IrBinaryOp.Mul && binary.Type.Bits == 16));
    var narrowComparisons = fastBody.Instructions.OfType<IrCmp>()
      .Where(cmp => cmp.Pred is IrCmpPred.Sgt or IrCmpPred.Slt)
      .ToList();
    Assert.That(narrowComparisons.Count, Is.GreaterThanOrEqualTo(2));
    Assert.That(narrowComparisons,
      Has.All.Matches<IrCmp>(cmp => cmp.Lhs.Type.Bits == 16 && cmp.Rhs.Type.Bits == 16));

    Assert.That(scaled.Parent, Is.SameAs(body), "the wide multiply must still exist for guard failures");
    Assert.That(scaled.Type.Bits, Is.EqualTo(32));

    var joined = exit.Phis.Single();
    Assert.That(joined.IncomingBlocks, Has.Count.EqualTo(2),
      "versioning adds a second loop definition, so the direct post-loop counter use needs an SSA join");
    Assert.That(((IrRet)exit.Terminator!).Value, Is.SameAs(joined));
  }

  [Test]
  public void Loop_GivenNarrowOperandsButAPossiblyOverflowingResult_WhenVersioned_ThenThatOperationStaysWide() {
    var value = new IrArgument(IrType.I32, 0, "value");
    var fn = new IrFunction("f", IrType.I16, [value]);
    var (_, header, body, exit, counter) = CreateLoop(fn);

    var above = body.Append(new IrCmp(IrCmpPred.Sgt, value, new IrConstantInt(IrType.I32, 10)));
    var below = body.Append(new IrCmp(IrCmpPred.Slt, value, new IrConstantInt(IrType.I32, 20)));
    var repeated = body.Append(new IrBinary(IrBinaryOp.And, above, below));

    // -32768..32767 plus one reaches 32768. The guard proves the operand narrow, not the result.
    var plusOne = body.Append(new IrBinary(IrBinaryOp.Add, value, new IrConstantInt(IrType.I32, 1)));
    var positive = body.Append(new IrCmp(IrCmpPred.Sgt, plusOne, new IrConstantInt(IrType.I32, 0)));
    var condition = body.Append(new IrBinary(IrBinaryOp.And, repeated, positive));
    CloseLoop(body, header, counter, condition);
    exit.Append(new IrRet(counter));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(SpeculativeIntegerNarrowing.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    var fastBody = fn.Blocks.Single(block => block.Label == "narrow.body");
    Assert.That(fastBody.Instructions.OfType<IrBinary>(),
      Has.Some.Matches<IrBinary>(binary =>
        binary.Op == IrBinaryOp.Add
        && binary.Type.Bits == 32
        && binary.Rhs is IrConstantInt { Value: 1 }),
      "narrowing an add that may produce 32768 would change wrap/sign-extension semantics");
  }

  [Test]
  public void Loop_GivenTheUnknownWideValueIsLoadedInside_WhenNarrowingIsConsidered_ThenNoDataScanIsInvented() {
    var pointer = new IrArgument(IrType.Ptr, 0, "pointer");
    var fn = new IrFunction("f", IrType.I16, [pointer]);
    var (entry, header, body, exit, counter) = CreateLoop(fn);

    var loaded = body.Append(new IrLoad(IrType.I32, pointer));
    var above = body.Append(new IrCmp(IrCmpPred.Sgt, loaded, new IrConstantInt(IrType.I32, 10)));
    var below = body.Append(new IrCmp(IrCmpPred.Slt, loaded, new IrConstantInt(IrType.I32, 20)));
    var both = body.Append(new IrBinary(IrBinaryOp.And, above, below));
    CloseLoop(body, header, counter, both);
    exit.Append(new IrRet(counter));

    Assert.That(IrVerifier.Verify(fn), Is.Empty);

    Assert.That(SpeculativeIntegerNarrowing.Run(fn), Is.Zero);
    Assert.That(entry.Terminator, Is.TypeOf<IrBr>(),
      "checking every loaded element would cost a scan; a loop-local load is not a cheap guard root");
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_WhenNarrowingIsConsidered_ThenItIsSkipped() {
    var value = new IrArgument(IrType.I32, 0, "value");
    var fn = new IrFunction("f", IrType.I16, [value]);
    var (_, header, body, exit, counter) = CreateLoop(fn);

    var above = body.Append(new IrCmp(IrCmpPred.Sgt, value, new IrConstantInt(IrType.I32, 10)));
    var below = body.Append(new IrCmp(IrCmpPred.Slt, value, new IrConstantInt(IrType.I32, 20)));
    var both = body.Append(new IrBinary(IrBinaryOp.And, above, below));
    CloseLoop(body, header, counter, both);
    exit.Append(new IrRet(counter));
    fn.HasErrorHandler = true;

    Assert.That(SpeculativeIntegerNarrowing.Run(fn), Is.Zero);
  }

  private static (IrBasicBlock Entry, IrBasicBlock Header, IrBasicBlock Body, IrBasicBlock Exit, IrPhi Counter)
      CreateLoop(IrFunction fn) {
    var entry = fn.CreateBlock("entry");
    var header = fn.CreateBlock("loop");
    var body = fn.CreateBlock("body");
    var exit = fn.CreateBlock("exit");

    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    var keepGoing = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 40)));
    header.Append(new IrCondBr(keepGoing, body, exit));
    return (entry, header, body, exit, counter);
  }

  private static void CloseLoop(IrBasicBlock body, IrBasicBlock header, IrPhi counter, IrValue condition) {
    var step = body.Append(new IrSelect(
      condition,
      new IrConstantInt(IrType.I16, 2),
      new IrConstantInt(IrType.I16, 1)));
    var next = body.Append(new IrBinary(IrBinaryOp.Add, counter, step));
    body.Append(new IrBr(header));
    counter.AddIncoming(next, body);
  }
}
