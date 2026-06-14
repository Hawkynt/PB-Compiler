using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// InstCombine boolean canonicalization: collapsing the "widen an i1 then compare to
/// zero" shape that every BASIC condition lowers to.
/// </summary>
[TestFixture]
public sealed class BoolCanonTests {

  [Test]
  public void WidenedBoolNeZero_CollapsesToTheBool() {
    // (sext i1 %c to i16) != 0  ->  %c
    var a = new IrArgument(IrType.I16, 0, "a");
    var fn = new IrFunction("f", IrType.I1, [a]);
    var b = new IrBuilder(fn.CreateBlock("entry"));
    var c = b.Cmp(IrCmpPred.Slt, a, IrBuilder.ConstInt(IrType.I16, 10));
    var widened = b.SExt(c, IrType.I16);
    b.Ret(b.Cmp(IrCmpPred.Ne, widened, IrBuilder.ConstInt(IrType.I16, 0)));

    InstCombine.Run(fn);
    Dce.Run(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrCast>(), Is.Empty);          // the widen is gone
    var cmps = fn.AllInstructions.OfType<IrCmp>().ToList();
    Assert.That(cmps, Has.Count.EqualTo(1));                             // only the slt remains
    Assert.That(cmps[0].Pred, Is.EqualTo(IrCmpPred.Slt));
  }

  [Test]
  public void LoweredWhileCondition_CollapsesToASingleComparison() {
    var unit = Parser.Parse(Lexer.Tokenize("i% = 0\nWHILE i% < 10\n  i% = i% + 1\nWEND\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var fn = IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;

    IrPassManager.Standard().RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    // the header should test the counter directly, not via a widen + compare-to-zero
    var text = IrPrinter.Print(fn);
    Assert.That(text, Does.Not.Contain("sext i1"));
    Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "icmp ").Count, Is.EqualTo(1));
  }
}
