using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <see cref="Compiler.Ir.Passes.KnownBitsSimplify"/> in the middle end every back end shares: what an
/// operand's known bits settle is folded before any target sees it. The operand comes from INPUT, so
/// nothing is constant - only its bits are known.
/// </summary>
[TestFixture]
public sealed class KnownBitsSimplifyTests {

  private static IrFunction Main(string body) {
    var source = "INPUT n%\n" + body;
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var compiled = IrBackendModule.TryCompile(model, new IrBackendOptions { Target = IrBackendTarget.C, Optimize = true, RecoverIntegerArithmetic = true }, out var declined);
    Assert.That(compiled, Is.Not.Null, declined);
    return compiled!.Module.FindFunction("main")!;
  }

  private static IEnumerable<IrBinary> Binaries(IrFunction function, IrBinaryOp op)
    => function.AllInstructions.OfType<IrBinary>().Where(binary => binary.Op == op);

  [Test]
  public void Fold_GivenAMaskSelectingOnlyKnownZeroBits_ThenTheAndIsZero() {
    var main = Main("PRINT (n% * 4) AND 3\n");

    // the product itself stays: its overflow check still has to raise error 6 for a large n
    Assert.That(Binaries(main, IrBinaryOp.And).Where(and => and.Rhs is IrConstantInt { Value: 3 }), Is.Empty,
      "the low two bits of n * 4 are zero");
  }

  [Test]
  public void Fold_GivenAMaskKeepingEveryPossiblySetBit_ThenTheAndIsItsOperand() {
    var main = Main("PRINT (n% AND 7) AND 15\n");

    Assert.That(Binaries(main, IrBinaryOp.And).Select(and => ((IrConstantInt)and.Rhs).Value), Is.EquivalentTo(new long[] { 7 }));
  }

  [Test]
  public void Fold_GivenASignedRemainderOfANonNegativeValue_ThenItIsAnAnd() {
    var main = Main("PRINT (n% AND 7) MOD 16\n");

    Assert.That(Binaries(main, IrBinaryOp.SRem), Is.Empty);
  }

  [Test]
  public void Fold_GivenAComparisonAKnownBitContradicts_ThenTheBranchIsGone() {
    var main = Main("IF (n% AND 12) = 5 THEN PRINT \"never\"\nPRINT \"end\"\n");

    Assert.That(main.AllInstructions.OfType<IrCondBr>(), Is.Empty);
  }

  [Test]
  public void Fold_GivenBitsTheAnalysisCannotSettle_ThenNothingChanges() {
    var main = Main("PRINT (n% * 3) AND 1\nIF (n% AND 12) = 4 THEN PRINT \"maybe\"\n");

    Assert.Multiple(() => {
      Assert.That(Binaries(main, IrBinaryOp.And).Count(), Is.EqualTo(2));
      Assert.That(main.AllInstructions.OfType<IrCondBr>(), Is.Not.Empty);
    });
  }
}
