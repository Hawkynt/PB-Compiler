using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

[TestFixture]
public sealed class AggregateRawBitFloatingEqualityTests {

  private static IrModule Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var model = Binder.Bind(unit, Dialect.Pb35);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(IrVerifier.Verify(module!), Is.Empty);
    return module!;
  }

  [Test]
  public void Run_GivenSingleRecordEquality_WhenScalarized_ThenItComparesRawI32Bits() {
    // Given whole-record byte equality whose complete layout is one IEEE binary32 field.
    var module = Lower("""
      TYPE Sample
        Value AS SINGLE
      END TYPE
      PRINT EqualBits%(1!, 2!)
      END
      FUNCTION EqualBits%(BYVAL x AS SINGLE, BYVAL y AS SINGLE)
        DIM a AS Sample
        DIM b AS Sample
        a.Value = x
        b.Value = y
        IF a = b THEN EqualBits% = 1 ELSE EqualBits% = 0
      END FUNCTION
      """);
    var function = module.Functions.Single(f => f.Name.Equals("EqualBits", StringComparison.OrdinalIgnoreCase));

    // When aggregate block equality is scalarized before the cleanup passes.
    var changes = AggregateBlockScalarization.Run(function);

    // Then the float values are reinterpreted, never numerically compared. This preserves the
    // distinction between +0/-0 and between distinct NaN payloads that rt_mem_compare observed.
    var bitcasts = function.AllInstructions.OfType<IrCast>()
      .Where(cast => cast.Op == IrCastOp.BitCast)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(function.AllInstructions.OfType<IrCall>()
        .Any(call => call.Callee is IrFunction { Name: "rt_mem_compare" }), Is.False);
      Assert.That(bitcasts, Has.Count.EqualTo(2));
      Assert.That(bitcasts.All(cast => cast.Value.Type == IrType.F32 && cast.Type == IrType.U32), Is.True);
      Assert.That(function.AllInstructions.OfType<IrCmp>()
        .Any(cmp => cmp.Pred == IrCmpPred.Eq && cmp.Lhs.Type == IrType.U32 && cmp.Rhs.Type == IrType.U32), Is.True);
      Assert.That(function.AllInstructions.OfType<IrCmp>()
        .Any(cmp => cmp.Pred is IrCmpPred.Foeq or IrCmpPred.Fone), Is.False);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Run_GivenDoubleRecordEquality_WhenScalarized_ThenItComparesRawI64Bits() {
    // Given the same byte-equality shape at IEEE binary64 width.
    var module = Lower("""
      TYPE Sample
        Value AS DOUBLE
      END TYPE
      PRINT EqualBits%(1#, 2#)
      END
      FUNCTION EqualBits%(BYVAL x AS DOUBLE, BYVAL y AS DOUBLE)
        DIM a AS Sample
        DIM b AS Sample
        a.Value = x
        b.Value = y
        IF a = b THEN EqualBits% = 1 ELSE EqualBits% = 0
      END FUNCTION
      """);
    var function = module.Functions.Single(f => f.Name.Equals("EqualBits", StringComparison.OrdinalIgnoreCase));

    // When scalarized.
    Assert.That(AggregateBlockScalarization.Run(function), Is.EqualTo(1));

    // Then binary64 is likewise compared as its exact 64-bit representation.
    var bitcasts = function.AllInstructions.OfType<IrCast>()
      .Where(cast => cast.Op == IrCastOp.BitCast)
      .ToList();
    Assert.Multiple(() => {
      Assert.That(bitcasts, Has.Count.EqualTo(2));
      Assert.That(bitcasts.All(cast => cast.Value.Type == IrType.F64 && cast.Type == IrType.U64), Is.True);
      Assert.That(function.AllInstructions.OfType<IrCmp>()
        .Any(cmp => cmp.Pred == IrCmpPred.Eq && cmp.Lhs.Type == IrType.U64 && cmp.Rhs.Type == IrType.U64), Is.True);
      Assert.That(IrVerifier.Verify(function), Is.Empty);
    });
  }

  [Test]
  public void Pipeline_GivenMixedIntegerAndSingleRecordEquality_WhenOptimized_ThenBackingAndMemCompareDisappear() {
    // Given a complete mixed layout: ordinary integer equality and raw-bit SINGLE equality can be
    // conjoined without changing any byte observable to the original whole-record comparison.
    var module = Lower("""
      TYPE Sample
        Tag AS INTEGER
        Value AS SINGLE
      END TYPE
      PRINT EqualBits%(1%, 1!, 1%, 2!)
      END
      FUNCTION EqualBits%(BYVAL at%, BYVAL av AS SINGLE, BYVAL bt%, BYVAL bv AS SINGLE)
        DIM a AS Sample
        DIM b AS Sample
        a.Tag = at% : a.Value = av
        b.Tag = bt% : b.Value = bv
        IF a = b THEN EqualBits% = 1 ELSE EqualBits% = 0
      END FUNCTION
      """);
    var pipeline = IrPassManager.Standard();
    pipeline.VerifyEachPass = true;
    pipeline.RunOnModule(module);
    var function = module.Functions.Single(f => f.Name.Equals("EqualBits", StringComparison.OrdinalIgnoreCase));

    // Then O0059 feeds ordinary aggregate SROA/mem2reg instead of leaving byte-backed records solely
    // because one independent field happens to be floating-point.
    Assert.Multiple(() => {
      Assert.That(function.AllInstructions.OfType<IrCall>()
        .Any(call => call.Callee is IrFunction { Name: "rt_mem_compare" }), Is.False);
      Assert.That(function.AllInstructions.OfType<IrAlloca>()
        .Any(alloca => alloca.Allocated == IrType.I8 && alloca.Count == 6), Is.False);
      Assert.That(function.AllInstructions.OfType<IrCmp>()
        .Any(cmp => cmp.Pred is IrCmpPred.Foeq or IrCmpPred.Fone), Is.False);
      Assert.That(IrVerifier.Verify(module), Is.Empty);
    });
  }

  [Test]
  public void CEmitter_GivenRawFloatEqualityBitcasts_ThenItCopiesObjectRepresentations() {
    // Given optimized IR containing the raw f32 -> u32 casts introduced by O0059.
    var module = Lower("""
      TYPE Sample
        Value AS SINGLE
      END TYPE
      PRINT EqualBits%(1!, 2!)
      END
      FUNCTION EqualBits%(BYVAL x AS SINGLE, BYVAL y AS SINGLE)
        DIM a AS Sample
        DIM b AS Sample
        a.Value = x
        b.Value = y
        IF a = b THEN EqualBits% = 1 ELSE EqualBits% = 0
      END FUNCTION
      """);
    var function = module.Functions.Single(f => f.Name.Equals("EqualBits", StringComparison.OrdinalIgnoreCase));
    Assert.That(AggregateBlockScalarization.Run(function), Is.EqualTo(1));

    // When emitted as portable C99, BitCast must not become a numeric C cast. memcpy over an
    // addressable compound literal preserves the exact object representation and obeys aliasing rules.
    var c = CEmitter.Emit(module);

    Assert.Multiple(() => {
      Assert.That(c, Does.Contain("memcpy(&"));
      Assert.That(c, Does.Contain("&(float){ "));
      Assert.That(c, Does.Not.Contain(" = (int32_t)v"));
    });
  }
}
