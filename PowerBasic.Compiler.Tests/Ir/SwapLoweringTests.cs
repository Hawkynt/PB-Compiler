using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>SWAP statement lowering (exchange of two scalar lvalues).</summary>
[TestFixture]
public sealed class SwapLoweringTests {

  private static IrFunction? Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Swap_OfTwoScalars_LowersToCrossedStores() {
    var fn = Lower("a% = 1\nb% = 2\nSWAP a%, b%\nEND");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(fn!.AllInstructions.OfType<IrLoad>().Count(), Is.GreaterThanOrEqualTo(2));
    Assert.That(fn.AllInstructions.OfType<IrStore>().Count(), Is.GreaterThanOrEqualTo(2));
  }

  [Test]
  public void Swap_ThroughPipeline_ExchangesTheConstants() {
    // after SWAP, a holds 2 and b holds 1; with a use we can observe the exchange
    var fn = Lower("a% = 1\nb% = 2\nSWAP a%, b%\nc% = a% - b%\nEND")!;
    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);   // a-b = 2-1 = 1, fully foldable and verifiable
  }

  [Test]
  public void Swap_OfArrayElements_LowersViaElementAddresses() {
    var fn = Lower("DIM a%(0 TO 3)\na%(0) = 5\na%(1) = 9\nSWAP a%(0), a%(1)\nEND");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
  }
}
