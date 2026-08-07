using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// O0012 — float demotion. PowerBASIC types a bare variable name SINGLE, so DOS-era counters are
/// floating point by accident; when one is provably integral and bounded, the x87 round trip goes.
///
/// The declines matter more than the acceptance here. Integer arithmetic WRAPS where float arithmetic
/// saturates, so a counter whose range is not pinned down would trade one wrong answer for another -
/// which is why the init, the step and the limit must all be integral constants, and the step must
/// move toward the limit.
/// </summary>
[TestFixture]
public sealed class FloatDemotionTests {

  private static IrFunction Lowered(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var fn = module!.Functions.First(f => f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    Mem2Reg.Run(fn);
    return fn;
  }

  private static int FloatPhis(IrFunction fn) => fn.AllInstructions.OfType<IrPhi>().Count(p => p.Type.IsIeeeFloat);

  [Test]
  public void Counter_GivenASingleCountingUpToAConstant_ThenItBecomesAnInteger() {
    var fn = Lowered("""
      DIM i AS SINGLE
      DIM t AS INTEGER
      FOR i = 1 TO 10
        t = t + 1
      NEXT i
      PRINT t
      """);
    Assume.That(FloatPhis(fn), Is.GreaterThan(0), "the counter should start out as a float phi");

    Assert.That(FloatDemotion.Run(fn), Is.EqualTo(1));
    Assert.That(FloatPhis(fn), Is.Zero, "the x87 round trip is gone");
  }

  /// <summary>A fractional step is not integral, so the counter is not an integer at all.</summary>
  [Test]
  public void Counter_GivenAFractionalStep_ThenItStaysAFloat() {
    var fn = Lowered("""
      DIM i AS SINGLE
      DIM t AS INTEGER
      FOR i = 1 TO 10 STEP 0.5
        t = t + 1
      NEXT i
      PRINT t
      """);

    Assert.That(FloatDemotion.Run(fn), Is.Zero);
    Assert.That(FloatPhis(fn), Is.GreaterThan(0));
  }

  /// <summary>
  /// A step moving AWAY from the limit never terminates. Both forms hang, but only the integer one
  /// wraps on the way - so it is refused rather than reasoned about.
  /// </summary>
  [Test]
  public void Counter_GivenAStepMovingAwayFromTheLimit_ThenItIsRefused() {
    var fn = new IrFunction("f", IrType.Void);
    var entry = fn.AddBlock(new IrBasicBlock("entry"));
    var head = fn.AddBlock(new IrBasicBlock("head"));
    var body = fn.AddBlock(new IrBasicBlock("body"));
    var exit = fn.AddBlock(new IrBasicBlock("exit"));
    entry.Append(new IrBr(head));

    var i = head.AppendPhi(new IrPhi(IrType.F32) { Name = "i" });
    head.Append(new IrCondBr(head.Append(new IrCmp(IrCmpPred.Folt, i, new IrConstantFloat(IrType.F32, 10))), body, exit));
    var next = body.Append(new IrBinary(IrBinaryOp.FSub, i, new IrConstantFloat(IrType.F32, 1)));   // counts DOWN
    body.Append(new IrBr(head));
    exit.Append(new IrRet());
    i.AddIncoming(new IrConstantFloat(IrType.F32, 1), entry);
    i.AddIncoming(next, body);

    Assert.That(FloatDemotion.Run(fn), Is.Zero, "a step away from the limit is not bounded");
  }

  /// <summary>A counter handed to something that wants a float would escape as an integer.</summary>
  [Test]
  public void Counter_GivenItIsPrintedAsAFloat_ThenItStaysAFloat() {
    var fn = Lowered("""
      DIM i AS SINGLE
      FOR i = 1 TO 10
        PRINT i
      NEXT i
      """);

    Assert.That(FloatDemotion.Run(fn), Is.Zero, "the print entry takes a float");
  }

  [Test]
  public void Function_GivenAnArmedErrorHandler_ThenItIsSkipped() {
    var fn = Lowered("""
      DIM i AS SINGLE
      DIM t AS INTEGER
      FOR i = 1 TO 10
        t = t + 1
      NEXT i
      PRINT t
      """);
    fn.HasErrorHandler = true;

    Assert.That(FloatDemotion.Run(fn), Is.Zero);
  }
}
