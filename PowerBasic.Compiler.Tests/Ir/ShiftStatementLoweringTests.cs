using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>SHIFT LEFT v, n</c> / <c>SHIFT RIGHT v, n</c> - a shift written as a statement, updating the
/// variable in place. The right shift is <b>logical</b>: the direct emitter uses <c>SHR</c> whatever
/// the variable's signedness, so a negative INTEGER shifts its sign bit along like any other bit
/// rather than smearing it. Lowering it as an arithmetic shift would give a different number for
/// every negative value.
/// </summary>
[TestFixture]
public sealed class ShiftStatementLoweringTests {

  private static IrModule? Lower(string source, out string? why) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return IrLowering.TryLowerModule(model, out why);
  }

  [TestCase("LEFT", IrBinaryOp.Shl)]
  [TestCase("RIGHT", IrBinaryOp.LShr)]
  public void Lower_GivenAShiftStatement_ThenUpdatesTheVariableWithThatOperation(string direction, IrBinaryOp op) {
    var module = Lower($"""
      DIM n AS INTEGER
      n = 40
      SHIFT {direction} n, 2
      PRINT n
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var ops = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrBinary>().Select(b => b.Op).ToList();
    Assert.That(ops, Does.Contain(op));
    Assert.That(ops, Does.Not.Contain(IrBinaryOp.AShr), "the right shift is logical, not arithmetic");
  }

  [TestCase("LEFT")]
  [TestCase("RIGHT")]
  public void Lower_GivenRotateByAConstant_ThenWritesItAsTwoShiftsOred(string direction) {
    // no IR operation carries a rotate, so it becomes the two shifts that make one - exact, because
    // both halves are modular in the variable's own width
    var module = Lower($"""
      DIM n AS INTEGER
      n = 40
      ROTATE {direction} n, 2
      PRINT n
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var ops = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrBinary>().Select(b => b.Op).ToList();
    Assert.That(ops, Does.Contain(IrBinaryOp.Shl));
    Assert.That(ops, Does.Contain(IrBinaryOp.LShr));
    Assert.That(ops, Does.Contain(IrBinaryOp.Or));
  }

  [Test]
  public void Lower_GivenRotateByARuntimeCount_ThenDeclines() {
    // the complementary shift would be by the whole width, which is undefined in the IR - and on the
    // hardware differs between the 8086 (no masking) and later parts (masked to five bits)
    Lower("""
      DIM n AS INTEGER
      DIM k AS INTEGER
      n = 40
      READ k
      DATA 2
      ROTATE LEFT n, k
      PRINT n
      """, out var why);

    Assert.That(why, Does.Contain("runtime count"));
  }
}
