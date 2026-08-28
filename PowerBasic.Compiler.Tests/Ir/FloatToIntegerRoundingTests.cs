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

  #region the UNSIGNED half

  private const string _unsignedAssignment = """
    DIM d AS DOUBLE
    DIM b AS BYTE
    DIM w AS WORD
    DIM u AS DWORD
    d = VAL("3.5")
    b = d : w = d : u = d
    PRINT b; w; u
    """;

  /// <summary>
  /// An UNSIGNED target rounds too. Genuine PBC 3.5 answers <c>3.5</c> with 4 for a BYTE, a WORD and a
  /// DWORD exactly as it does for an INTEGER (<c>tests/diff/DIFF117.BAS</c> is the oracle gate), so the
  /// conversion the lowering names has to be the rounding one - and it was <c>FPToUI</c>, the
  /// truncating one, which the x86-16 selector answered with a rounding <c>FISTP</c> anyway while C
  /// and LLVM did what the opcode said.
  /// </summary>
  [Test]
  public void Lower_GivenAFloatAssignedToAnUnsignedInteger_ThenUsesTheRoundingConversion() {
    var module = Lower(_unsignedAssignment);

    Assert.That(Casts(module).Select(c => c.Op), Does.Contain(IrCastOp.FPToUIRound));
    Assert.That(Casts(module).Select(c => c.Op), Does.Not.Contain(IrCastOp.FPToUI),
      "an assignment rounds; the truncating conversion is a different operation");
  }

  [Test]
  public void Emit_GivenTheUnsignedRoundingConversion_ThenBothEmittersRoundBeforeTheyConvert() {
    var module = Lower(_unsignedAssignment);

    var ll = LlvmEmitter.Emit(module);
    var c = CEmitter.Emit(module);

    Assert.Multiple(() => {
      Assert.That(ll, Does.Contain("@llvm.rint."), "fptoui alone would truncate");
      Assert.That(ll, Does.Contain("fptoui"));
      Assert.That(ll.IndexOf("@llvm.rint.", StringComparison.Ordinal),
        Is.LessThan(ll.LastIndexOf("fptoui", StringComparison.Ordinal)), "round first, then convert");
      Assert.That(c, Does.Contain("(uint8_t)llrint("), "a C cast alone would truncate");
      Assert.That(c, Does.Contain("(uint16_t)llrint("));
      Assert.That(c, Does.Contain("(uint32_t)llrint("));
    });
  }

  /// <summary>
  /// And the prototype the C back end writes for a runtime entry taking an unsigned value has to be
  /// the one <c>runtime/pbc_rt.h</c> declares. It was not - <c>IrType.Integer</c> defaults to signed,
  /// so <c>rt_print_u8</c> was declared taking an <c>i8</c> and re-declared in the emitted C as
  /// <c>int8_t</c>, which is a conflicting type the C compiler REJECTS. Every program printing a
  /// BYTE, WORD or DWORD emitted a translation unit that would not build, and no battery program with
  /// a golden output had one in it.
  /// </summary>
  [Test]
  public void Emit_GivenAnUnsignedRuntimeArgument_ThenTheCPrototypeMatchesThePortableRuntimeHeader() {
    var c = CEmitter.Emit(Lower(_unsignedAssignment));

    Assert.Multiple(() => {
      Assert.That(c, Does.Contain("extern void rt_print_u8(uint8_t p0);"));
      Assert.That(c, Does.Contain("extern void rt_print_u16(uint16_t p0);"));
      Assert.That(c, Does.Contain("extern void rt_print_u32(uint32_t p0);"));
    });
  }

  /// <summary>
  /// The unsigned rounding conversion is deliberately NOT const-folded, exactly as the truncating
  /// <see cref="IrCastOp.FPToUI"/> it replaced was not. Folding it is correct arithmetic, but it
  /// removes the cast that currently stops <c>IrBasicWriter</c> rendering DIFF05, DIFF58 and DIFF61 -
  /// and rendering those three exposes a separate gap in how the writer materializes UNSIGNED
  /// arithmetic into declared pb35 variables. Pinned so the coupling is visible: the fold belongs
  /// with the fix for that, not ahead of it.
  /// </summary>
  [Test]
  public void Fold_GivenAnUnsignedConstant_ThenIsLeftForTheBackEndAsTheTruncatingOneAlwaysWas() {
    var rounding = new IrCast(IrCastOp.FPToUIRound, new IrConstantFloat(IrType.F64, 3.5), IrType.U16);
    var truncating = new IrCast(IrCastOp.FPToUI, new IrConstantFloat(IrType.F64, 3.5), IrType.U16);

    Assert.Multiple(() => {
      Assert.That(IrConstFold.TryFold(rounding), Is.Null);
      Assert.That(IrConstFold.TryFold(truncating), Is.Null, "the opcode it replaced did not fold either");
    });
  }

  #endregion
}
