using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Lowering of the pure numeric intrinsics ABS and SGN (branchless, no runtime).</summary>
[TestFixture]
public sealed class IntrinsicLoweringTests {

  private static IrFunction Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
  }

  [Test]
  public void Abs_OfInteger_LowersBranchless() {
    var fn = Lower("n% = -3\nm% = ABS(n%)");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("ashr i16"));   // sign mask
    Assert.That(text, Does.Contain("xor i16"));
    Assert.That(text, Does.Contain("sub i16"));
  }

  [Test]
  public void Abs_OfSingle_ClearsTheSignBitViaBitcast() {
    var fn = Lower("x! = -2.5\ny! = ABS(x!)");

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("bitcast f32"));
    Assert.That(text, Does.Contain("and i32"));
  }

  [Test]
  public void Sgn_OfInteger_LowersToTwoComparisonsAndASubtract() {
    var fn = Lower("n% = -7\ns% = SGN(n%)");

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Contain("icmp sgt i16"));
    Assert.That(text, Does.Contain("icmp slt i16"));
  }

  [Test]
  public void Abs_OfConstant_FoldsThroughInstCombine() {
    // -5 is SINGLE-promoted (unary negate), so ABS takes the float path; bitcast
    // folding lets the whole clear-sign-bit sequence collapse to the constant.
    var fn = Lower("a% = ABS(-5)");
    InstCombine.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(IrPrinter.Print(fn), Does.Contain("store i16 5"));   // |-5| folded to 5
  }

  [Test]
  public void Sgn_OfNegativeConstant_FoldsToMinusOne() {
    var fn = Lower("a% = SGN(-42)");
    InstCombine.Run(fn);

    Assert.That(IrPrinter.Print(fn), Does.Contain("store i16 -1"));
  }
}
