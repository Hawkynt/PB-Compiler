using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// BASIC <b>rounds</b> a real on its way into an integer variable - <c>n% = 2.7</c> is 3 - while a C
/// cast and LLVM's <c>fptosi</c> both truncate. The IR therefore has to say which it means, so
/// <see cref="IrCastOp.FPToSIRound"/> is a separate operation from <see cref="IrCastOp.FPToSI"/>: the
/// two disagree on every value with a fraction, which is the kind of difference that shows up as a
/// wrong number in program output rather than as a crash.
///
/// The rounding is to nearest with ties to even, which is what the x87 control word is left at and
/// what <c>llvm.rint</c> follows under the default mode - so the native and LLVM paths agree without
/// either of them naming a mode.
/// </summary>
[TestFixture]
public sealed class FloatToIntegerRoundingTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  private static IEnumerable<IrCast> Casts(IrModule module)
    => module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).OfType<IrCast>();

  [Test]
  public void Lower_GivenAFloatAssignedToAnInteger_ThenUsesTheRoundingConversion() {
    var module = Lower("""
      DIM s AS SINGLE
      DIM n AS INTEGER
      s = 2.7
      n = s
      PRINT n
      """);

    Assert.That(Casts(module).Select(c => c.Op), Does.Contain(IrCastOp.FPToSIRound));
    Assert.That(Casts(module).Select(c => c.Op), Does.Not.Contain(IrCastOp.FPToSI),
      "an assignment rounds; nothing here truncates");
  }

  [Test]
  public void Lower_GivenFix_ThenKeepsTheTruncatingConversion() {
    // FIX and INT are the operations that really do truncate, and they must not be confused with the
    // assignment conversion
    var module = Lower("""
      DIM s AS SINGLE
      s = 2.7
      PRINT FIX(s)
      """);

    Assert.That(Casts(module).Select(c => c.Op), Does.Contain(IrCastOp.FPToSI));
  }

  [TestCase("CINT")]
  [TestCase("CLNG")]
  public void Lower_GivenAnExplicitConversionIntrinsic_ThenItIsTheSameRoundingConversion(string intrinsic) {
    var module = Lower($"""
      DIM s AS SINGLE
      s = 2.5
      PRINT {intrinsic}(s)
      """);

    Assert.That(Casts(module).Select(c => c.Op), Does.Contain(IrCastOp.FPToSIRound));
  }

  [TestCase(2.7, 3L)]
  [TestCase(2.2, 2L)]
  [TestCase(-2.7, -3L)]
  [TestCase(2.5, 2L, TestName = "a tie rounds to the EVEN neighbour, not away from zero")]
  [TestCase(3.5, 4L, TestName = "the other tie rounds to the even neighbour too")]
  public void Fold_GivenAConstant_ThenRoundsToNearestTiesToEven(double value, long expected) {
    var cast = new IrCast(IrCastOp.FPToSIRound, new IrConstantFloat(IrType.F64, value), IrType.I32);

    var folded = IrConstFold.TryFold(cast);

    Assert.That(folded, Is.InstanceOf<IrConstantInt>());
    Assert.That(((IrConstantInt)folded!).Value, Is.EqualTo(expected));
  }

  [Test]
  public void Emit_GivenTheRoundingConversion_ThenLlvmRoundsBeforeItConverts() {
    var module = Lower("""
      DIM s AS SINGLE
      DIM n AS INTEGER
      s = 2.7
      n = s
      PRINT n
      """);

    var ll = LlvmEmitter.Emit(module);

    Assert.That(ll, Does.Contain("@llvm.rint."), "fptosi alone would truncate");
    Assert.That(ll, Does.Contain("fptosi"));
    Assert.That(ll.IndexOf("@llvm.rint.", StringComparison.Ordinal),
      Is.LessThan(ll.LastIndexOf("fptosi", StringComparison.Ordinal)), "round first, then convert");
  }
}
