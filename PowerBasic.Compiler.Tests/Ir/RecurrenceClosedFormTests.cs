using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0134 — closed forms for loop-carried recurrences. An accumulator that only adds a constant is
/// <c>start + step * trips</c>, and the loop does not have to run to find that out.
///
/// The declines carry the argument. Two's-complement addition is associative across wrapping, so an
/// integer accumulator reaches the same value however many times it overflows; floating point rounds
/// at every step, and a sum of forty roundings is not one multiplication. And an accumulator the body
/// READS has observable intermediate values, so only its final one is being replaced here.
/// </summary>
[TestFixture]
public sealed class RecurrenceClosedFormTests {

  private static IrFunction Lowered(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);
    // IntegerRecovery leaves the float-shaped arithmetic it replaced standing - PB integer arithmetic
    // lowers through the x87, and recovery adds the integer form beside it rather than deleting the
    // old one. Until DCE removes that shadow, the accumulator still has a reader inside the loop and
    // the pass correctly declines. The pipeline runs both; a test that skipped DCE measured the
    // leftovers.
    IntegerRecovery.Run(fn);
    Dce.Run(fn);
    return fn;
  }

  /// <summary>
  /// A trip count well past the unroller's cap, which is the point: unrolling replaces the loop with
  /// copies and is capped because the copies are the cost. A closed form does not care how many.
  /// </summary>
  [Test]
  public void Accumulator_GivenItOnlyAddsAConstant_ThenItsFinalValueIsComputed() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      t = 0
      FOR i = 1 TO 1000
        t = t + 3
      NEXT i
      PRINT t
      """);

    Assert.That(RecurrenceClosedForm.Run(fn), Is.EqualTo(1));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  /// <summary>
  /// A FLOAT accumulator is left alone. Each addition rounds; replacing forty roundings with one
  /// multiplication changes the answer, which is a different result rather than a faster one.
  /// </summary>
  [Test]
  public void Accumulator_GivenItIsFloatingPoint_ThenItIsLeftAlone() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(head));

    var i = head.AppendPhi(new IrPhi(IrType.I16) { Name = "i" });
    var t = head.AppendPhi(new IrPhi(IrType.F64) { Name = "t" });
    head.Append(new IrCondBr(head.Append(new IrCmp(IrCmpPred.Slt, i, new IrConstantInt(IrType.I16, 40))), body, exit));
    var nextI = body.Append(new IrBinary(IrBinaryOp.Add, i, new IrConstantInt(IrType.I16, 1)));
    var nextT = body.Append(new IrBinary(IrBinaryOp.FAdd, t, new IrConstantFloat(IrType.F64, 0.1)));
    body.Append(new IrBr(head));
    exit.Append(new IrRet());
    i.AddIncoming(new IrConstantInt(IrType.I16, 0), entry);
    i.AddIncoming(nextI, body);
    t.AddIncoming(new IrConstantFloat(IrType.F64, 0), entry);
    t.AddIncoming(nextT, body);

    Assert.That(RecurrenceClosedForm.Run(fn), Is.Zero, "each addition rounds; one multiply does not");
  }

  /// <summary>The body reads the running total, so the intermediate values are observable.</summary>
  [Test]
  public void Accumulator_GivenTheBodyReadsIt_ThenItIsLeftAlone() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      t = 0
      FOR i = 1 TO 1000
        t = t + 3
        PRINT t
      NEXT i
      """);

    Assert.That(RecurrenceClosedForm.Run(fn), Is.Zero);
  }

  /// <summary>A step that is not constant has no closed form of this shape.</summary>
  [Test]
  public void Accumulator_GivenTheStepVaries_ThenItIsLeftAlone() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      t = 0
      FOR i = 1 TO 1000
        t = t + i
      NEXT i
      PRINT t
      """);

    Assert.That(RecurrenceClosedForm.Run(fn), Is.Zero, "t = t + i is a series, not a constant step");
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var fn = Lowered("""
      DIM i AS INTEGER
      DIM t AS INTEGER
      FOR i = 1 TO 1000
        t = t + 3
      NEXT i
      PRINT t
      """);
    fn.HasErrorHandler = true;

    Assert.That(RecurrenceClosedForm.Run(fn), Is.Zero);
  }
}
