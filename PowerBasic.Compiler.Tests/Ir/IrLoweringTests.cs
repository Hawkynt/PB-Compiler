using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Bound-AST → IR lowering (alloca/load/store form). Every lowered function must
/// verify, and the supported subset must lower while unsupported constructs decline.
/// </summary>
[TestFixture]
public sealed class IrLoweringTests {

  private static IrFunction? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb35), "TEST.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    return IrLowering.TryLowerMainBody(model);
  }

  [Test]
  public void Lower_GivenStraightLineArithmetic_ProducesVerifiableAllocaForm() {
    // PB promotes INTEGER+INTEGER to SINGLE (overflow avoidance), so the faithful
    // lowering computes the add in f32 and converts back to INTEGER for the store.
    var fn = Lower("x% = 1\ny% = 2\nz% = x% + y%");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(IrPrinter.Print(fn!), Is.EqualTo(
      "define void @main() {\n" +
      "entry:\n" +
      "  %x = alloca i16\n" +
      "  %y = alloca i16\n" +
      "  %z = alloca i16\n" +
      "  store i16 1, ptr %x\n" +
      "  store i16 2, ptr %y\n" +
      "  %0 = load i16, ptr %x\n" +
      "  %1 = sitofp i16 %0 to f80\n" +
      "  %2 = load i16, ptr %y\n" +
      "  %3 = sitofp i16 %2 to f80\n" +
      "  %4 = fadd f80 %1, %3\n" +
      // The add runs at the x87's own width - PB computes a float expression in the register and
      // lets the declared type pick only the formatter. And the close is the ROUNDING conversion,
      // not a truncating one: BASIC rounds a real on its way into an integer variable, so
      // z% = x% + y% ends with fptosi.round
      "  %5 = fptosi.round f80 %4 to i16\n" +
      "  store i16 %5, ptr %z\n" +
      "  ret void\n" +
      "}\n"));
  }

  [Test]
  public void Lower_GivenIfElse_BuildsVerifiableDiamond() {
    var fn = Lower("x% = 5\nIF x% > 3 THEN\n  y% = 1\nELSE\n  y% = 2\nEND IF");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    var text = IrPrinter.Print(fn!);
    Assert.That(text, Does.Contain("icmp sgt i16"));
    Assert.That(text, Does.Contain("br i1"));
    Assert.That(text, Does.Contain("if.then"));
    Assert.That(text, Does.Contain("if.end"));
  }

  [Test]
  public void Lower_GivenForLoop_BuildsVerifiableLoopWithHeaderBodyIncExit() {
    var fn = Lower("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    var text = IrPrinter.Print(fn!);
    Assert.That(text, Does.Contain("for.head"));
    Assert.That(text, Does.Contain("for.body"));
    Assert.That(text, Does.Contain("for.inc"));
    Assert.That(text, Does.Contain("for.exit"));
    Assert.That(text, Does.Contain("icmp sle i16"));    // ascending counter, signed limit test
  }

  [Test]
  public void Lower_GivenDoWhile_BuildsVerifiablePreTestLoop() {
    var fn = Lower("i% = 0\nDO WHILE i% < 10\n  i% = i% + 1\nLOOP");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    var text = IrPrinter.Print(fn!);
    Assert.That(text, Does.Contain("do.head"));
    Assert.That(text, Does.Contain("do.body"));
    Assert.That(text, Does.Contain("do.exit"));
  }

  [Test]
  public void Lower_GivenComparisonValue_SignExtendsToMinusOneOrZero() {
    var fn = Lower("a% = 3\nb% = 4\nc% = (a% < b%)");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(IrPrinter.Print(fn!), Does.Contain("sext i1"));   // BASIC relational -> -1/0
  }

  [Test]
  public void Lower_GivenMixedWidthAdd_InsertsWideningCast() {
    var fn = Lower("a% = 1\nb& = 2\nc& = a% + b&");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(IrPrinter.Print(fn!), Does.Contain("sext i16"));  // INTEGER widened to LONG
  }

  [Test]
  /// <summary>
  /// Float arithmetic runs at the x87's own width and narrows on the STORE, which is PowerBASIC's own
  /// rule: the declared type picks the formatter, it does not round the value. So <c>x! * 2.0</c>
  /// multiplies in f80 and the <c>fptrunc</c> appears where the result meets <c>y!</c>.
  /// </summary>
  public void Lower_GivenFloatArithmetic_UsesFloatOpsAtRegisterWidth() {
    var fn = Lower("x! = 1.5\ny! = x! * 2.0");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    var ir = IrPrinter.Print(fn!);
    Assert.That(ir, Does.Contain("fmul f80"), "the multiply happens at the register's width");
    Assert.That(ir, Does.Contain("fptrunc f80"), "and rounds where it is stored into a SINGLE");
  }

  [Test]
  public void Lower_GivenAnUnsuffixedSingleLiteral_ThenPreservesItsSinglePrecisionBits() {
    var fn = Lower("x# = 0.1");

    Assert.That(fn, Is.Not.Null);
    var widened = fn!.AllInstructions.OfType<IrCast>().Single(c => c.Op == IrCastOp.FPExt);
    Assert.That(widened.Value, Is.InstanceOf<IrConstantFloat>());
    Assert.That(((IrConstantFloat)widened.Value).Value, Is.EqualTo((double)(float)0.1));
  }

  [Test]
  public void Lower_GivenAnExplicitDoubleLiteral_ThenDoesNotNarrowItToSinglePrecision() {
    var fn = Lower("x# = 0.1#");

    Assert.That(fn, Is.Not.Null);
    var stored = fn!.AllInstructions.OfType<IrStore>().Single().Value;
    Assert.That(stored, Is.InstanceOf<IrConstantFloat>());
    Assert.That(((IrConstantFloat)stored).Value, Is.EqualTo(0.1));
    Assert.That(((IrConstantFloat)stored).Value, Is.Not.EqualTo((double)(float)0.1));
  }

  [Test]
  public void Lower_GivenUnsupportedStatement_DeclinesWithNull() {
    Assert.That(Lower("PRINT \"hi\""), Is.Null);          // I/O not in the subset yet
    Assert.That(Lower("s$ = \"hi\""), Is.Null);           // strings not in the subset yet
  }
}
