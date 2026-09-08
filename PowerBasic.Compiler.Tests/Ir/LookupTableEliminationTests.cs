using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>O0333 — lookup-table elimination formula recovery.</summary>
[TestFixture]
public sealed class LookupTableEliminationTests {

  [TestCase(IrBinaryOp.And, 0x5a)]
  [TestCase(IrBinaryOp.Or, 0xa5)]
  public void BitwiseMaskTable_GivenAnExactFormula_ThenTheLoadBecomesOneOperation(IrBinaryOp op, int mask) {
    var module = new IrModule("test");
    var table = module.AddGlobal(new IrGlobalVariable("mask", IrType.U8) {
      Bytes = [.. Enumerable.Range(0, 256).Select(index => Apply(op, index, mask))],
      Count = 256,
      IsZeroInitialized = false,
    });
    var index = new IrArgument(IrType.U8, 0, "index");
    var fn = module.AddFunction(new IrFunction("read", IrType.U8, [index]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(table, index, IrType.U8));
    var load = entry.Append(new IrLoad(IrType.U8, at));
    var ret = entry.Append(new IrRet(load));

    Assert.That(LookupTableElimination.Run(module), Is.EqualTo(1));
    var binary = fn.AllInstructions.OfType<IrBinary>().Single();
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("mask"), Is.Null);
      Assert.That(binary.Op, Is.EqualTo(op));
      Assert.That(ret.Value, Is.SameAs(binary));
      Assert.That(fn.AllInstructions.OfType<IrLoad>(), Is.Empty);
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  [Test]
  public void BitwiseMaskTable_GivenOneMismatchingEntry_ThenTheTableRemains() {
    var bytes = Enumerable.Range(0, 256).Select(index => (byte)(index & 0x5a)).ToArray();
    bytes[17] ^= 1;
    var module = new IrModule("test");
    var table = module.AddGlobal(new IrGlobalVariable("almostMask", IrType.U8) {
      Bytes = bytes,
      Count = 256,
      IsZeroInitialized = false,
    });
    var index = new IrArgument(IrType.U8, 0, "index");
    var fn = module.AddFunction(new IrFunction("read", IrType.U8, [index]));
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var at = entry.Append(new IrGep(table, index, IrType.U8));
    var load = entry.Append(new IrLoad(IrType.U8, at));
    entry.Append(new IrRet(load));

    Assert.That(LookupTableElimination.Run(module), Is.Zero);
    Assert.Multiple(() => {
      Assert.That(module.FindGlobal("almostMask"), Is.SameAs(table));
      Assert.That(fn.AllInstructions.OfType<IrLoad>().Single(), Is.SameAs(load));
      Assert.That(IrVerifier.Verify(fn), Is.Empty);
    });
  }

  private static byte Apply(IrBinaryOp op, int index, int mask) => op switch {
    IrBinaryOp.And => (byte)(index & mask),
    IrBinaryOp.Or => (byte)(index | mask),
    _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Only bitwise mask formulas are tested here"),
  };
}
