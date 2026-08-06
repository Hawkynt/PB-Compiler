using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>FOR loops with a runtime (non-constant) STEP.</summary>
[TestFixture]
public sealed class RuntimeStepForTests {

  private static IrFunction? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35));
  }

  private static IrFunction? Lower(string source, Dialect dialect) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, dialect));
  }

  [Test]
  public void RuntimeStep_LowersWithADirectionTest() {
    var fn = Lower("d% = -1\ns% = 0\nFOR i% = 10 TO 1 STEP d%\n  s% = s% + i%\nNEXT i%\nEND");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    var text = IrPrinter.Print(fn!);
    Assert.That(text, Does.Contain("icmp sge i16"));   // step >= 0 direction test
    Assert.That(text, Does.Contain("for.head"));
  }

  [Test]
  public void RuntimeStep_StaysVerifiableThroughTheFullPipeline() {
    var fn = Lower("d% = 2\ns% = 0\nFOR i% = 0 TO 9 STEP d%\n  s% = s% + i%\nNEXT i%\nEND")!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>(), Is.Empty);   // fully promoted
  }

  [Test]
  public void ConstantStep_StillUsesTheSimpleDirectionPredicate() {
    // a constant negative step keeps the single sge comparison (no runtime sign test)
    var fn = Lower("s% = 0\nFOR i% = 10 TO 1 STEP -1\n  s% = s% + i%\nNEXT i%\nEND")!;

    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp sge i16"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ConstantStep_GivenAnUnsignedCounter_UsesTheCoercedStepDirection() {
    var fn = Lower("FOR i?? = 2 TO 0 STEP -1\nNEXT i??\nEND", Dialect.Pb36)!;

    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp ule u16"));
    Assert.That(text, Does.Contain("add u16"));
    Assert.That(text, Does.Contain("65535"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ConstantStep_GivenAnUnsignedQwordCounter_UsesAscendingDirectionForTheAllOnesPattern() {
    var fn = Lower("DIM i AS QWORD\nFOR i = 2 TO 0 STEP -1\nNEXT i\nEND", Dialect.Pb36)!;

    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp ule u64"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }

  [Test]
  public void ConstantStep_GivenZeroForAnUnsignedCounter_StillLowersAsAnAscendingLoop() {
    var fn = Lower("FOR i?? = 0 TO 1 STEP 0\nNEXT i??\nEND", Dialect.Pb36)!;

    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp ule u16"));
    Assert.That(text, Does.Contain("add u16"));
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
  }
}
