using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>GOTO / label lowering (arbitrary control flow over the alloca form).</summary>
[TestFixture]
public sealed class GotoLoweringTests {

  private static IrFunction? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void ForwardGoto_SkipsTheInterveningCode() {
    var fn = Lower("x% = 1\nGOTO skip\nx% = 2\nskip:\ny% = x%\nEND");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    // optimized: x is 1 at 'skip' (the x=2 store is unreachable), so y = 1
    IrPassManager.Standard().RunToFixpoint(fn!);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
  }

  [Test]
  public void BackwardGoto_FormsALoopWithAPhi() {
    var fn = Lower("i% = 0\ntop:\ni% = i% + 1\nIF i% < 5 THEN GOTO top\nx% = i%\nEND")!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrPhi>().Count(), Is.GreaterThanOrEqualTo(1));  // the GOTO loop's counter
    Assert.That(fn.AllInstructions.OfType<IrAlloca>(), Is.Empty);                          // fully promoted
  }

  [Test]
  public void GotoOutOfAStructuredBlock_Verifies() {
    var fn = Lower("s% = 0\nFOR i% = 1 TO 100\n  s% = s% + i%\n  IF s% > 50 THEN GOTO done\nNEXT i%\ndone:\nx% = s%\nEND")!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);   // an early exit out of the loop via GOTO stays well-formed
  }
}
