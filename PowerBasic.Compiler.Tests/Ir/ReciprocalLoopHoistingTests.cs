using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0338 guarded loop-hoisting legality tests.</summary>
[TestFixture]
public sealed class ReciprocalLoopHoistingTests {

  [Test]
  public void RepeatedDivision_GivenAnInnerConditionalUse_ThenLazyHoistingDoesNotSpeculateItToLoopEntry() {
    var condition = new IrArgument(IrType.I1, 0, "condition");
    var x = new IrArgument(IrType.F64, 1, "x");
    var y = new IrArgument(IrType.F64, 2, "y");
    var divisor = new IrArgument(IrType.F64, 3, "d");
    var fn = new IrFunction("conditionalLoop", IrType.Void, [condition, x, y, divisor]);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var header = fn.AddBlock(new IrBasicBlock("header"));
    var dispatch = fn.AddBlock(new IrBasicBlock("dispatch"));
    var hot = fn.AddBlock(new IrBasicBlock("hot"));
    var latch = fn.AddBlock(new IrBasicBlock("latch"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(header));

    var counter = header.AppendPhi(new IrPhi(IrType.I16));
    counter.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    var test = header.Append(new IrCmp(IrCmpPred.Slt, counter, new IrConstantInt(IrType.I16, 4)));
    header.Append(new IrCondBr(test, dispatch, exit));

    dispatch.Append(new IrCondBr(condition, hot, latch));
    var left = hot.Append(new IrBinary(IrBinaryOp.FDiv, x, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    var right = hot.Append(new IrBinary(IrBinaryOp.FDiv, y, divisor) {
      FastMathFlags = IrFastMathFlags.AllowReciprocal,
    });
    hot.Append(new IrBinary(IrBinaryOp.FAdd, left, right));
    hot.Append(new IrBr(latch));

    var next = latch.Append(new IrBinary(IrBinaryOp.Add, counter, new IrConstantInt(IrType.I16, 1)));
    latch.Append(new IrBr(header));
    counter.AddIncoming(next, latch);
    exit.Append(new IrRet());

    Assert.That(ReciprocalSequenceReuse.Run(fn), Is.EqualTo(2),
      "the two divisions may share a reciprocal inside the conditional arm, but that reciprocal must not be hoisted");

    var reciprocal = hot.Instructions.OfType<IrBinary>().Single(binary => binary.Op == IrBinaryOp.FDiv);
    Assert.Multiple(() => {
      Assert.That(reciprocal.Lhs, Is.TypeOf<IrConstantFloat>());
      Assert.That(((IrConstantFloat)reciprocal.Lhs).Value, Is.EqualTo(1.0));
      Assert.That(entry.Terminator, Is.TypeOf<IrBr>());
      Assert.That(fn.Blocks.Any(block => block.Label.StartsWith("recip.init", StringComparison.Ordinal)), Is.False);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }
}
