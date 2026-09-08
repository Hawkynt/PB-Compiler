using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class DataTranspositionTests {

  [Test]
  public void O0327_UnitStrideInnerLoop_DoesNotTranspose() {
    var (fn, entry, array) = BuildNestedTraversal(rowInner: false);

    Assert.That(DataTransposition.Run(fn), Is.Zero);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.Contains(array), Is.True);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name?.Contains(".transpose", StringComparison.Ordinal) == true), Is.False);
  }

  [Test]
  public void O0327_StridedInnerLoop_InNestedLoop_Transposes() {
    var (fn, entry, _) = BuildNestedTraversal(rowInner: true);

    Assert.That(DataTransposition.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(entry.Instructions.OfType<IrAlloca>().Any(a => a.Name == "matrix.transpose"), Is.True);
  }

  private static (IrFunction Function, IrBasicBlock Entry, IrAlloca Array) BuildNestedTraversal(bool rowInner) {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.CreateBlock("entry");
    var outerHeader = fn.CreateBlock("outer.header");
    var outerBody = fn.CreateBlock("outer.body");
    var innerHeader = fn.CreateBlock("inner.header");
    var innerBody = fn.CreateBlock("inner.body");
    var innerLatch = fn.CreateBlock("inner.latch");
    var innerExit = fn.CreateBlock("inner.exit");
    var outerLatch = fn.CreateBlock("outer.latch");
    var exit = fn.CreateBlock("exit");

    var array = entry.Append(new IrAlloca(IrType.I16) { Count = 8 * 32, Name = "matrix" });
    entry.Append(new IrBr(outerHeader));

    var outer = outerHeader.AppendPhi(new IrPhi(IrType.I32));
    outer.AddIncoming(new IrConstantInt(IrType.I32, 0), entry);
    var outerLimit = new IrConstantInt(IrType.I32, rowInner ? 32 : 8);
    var outerTest = outerHeader.Append(new IrCmp(IrCmpPred.Slt, outer, outerLimit));
    outerHeader.Append(new IrCondBr(outerTest, outerBody, exit));
    outerBody.Append(new IrBr(innerHeader));

    var inner = innerHeader.AppendPhi(new IrPhi(IrType.I32));
    inner.AddIncoming(new IrConstantInt(IrType.I32, 0), outerBody);
    var innerLimit = new IrConstantInt(IrType.I32, rowInner ? 8 : 32);
    var innerTest = innerHeader.Append(new IrCmp(IrCmpPred.Slt, inner, innerLimit));
    innerHeader.Append(new IrCondBr(innerTest, innerBody, innerExit));

    var row = rowInner ? inner : outer;
    var column = rowInner ? outer : inner;
    var rowOffset = innerBody.Append(new IrBinary(IrBinaryOp.Mul, row, new IrConstantInt(IrType.I32, 32)));
    var index = innerBody.Append(new IrBinary(IrBinaryOp.Add, rowOffset, column));
    var pointer = innerBody.Append(new IrGep(array, index, IrType.I16));
    innerBody.Append(new IrStore(new IrConstantInt(IrType.I16, 1), pointer));
    innerBody.Append(new IrBr(innerLatch));

    var innerNext = innerLatch.Append(new IrBinary(IrBinaryOp.Add, inner, new IrConstantInt(IrType.I32, 1)));
    innerLatch.Append(new IrBr(innerHeader));
    inner.AddIncoming(innerNext, innerLatch);

    innerExit.Append(new IrBr(outerLatch));
    var outerNext = outerLatch.Append(new IrBinary(IrBinaryOp.Add, outer, new IrConstantInt(IrType.I32, 1)));
    outerLatch.Append(new IrBr(outerHeader));
    outer.AddIncoming(outerNext, outerLatch);

    exit.Append(new IrRet());
    return (fn, entry, array);
  }
}
