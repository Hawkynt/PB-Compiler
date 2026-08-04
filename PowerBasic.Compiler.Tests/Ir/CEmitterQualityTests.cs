using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// The C back end must read like hand-written code, not a literal transcription of the SSA form.
/// These are pure-text assertions on the emitted C - no host C compiler needed - pinning the
/// improvements that make it so: integer arithmetic recovered from PB's float promotion, phi
/// copies sequentialized to direct assignments (no int64 staging), fall-through instead of a
/// goto to the next block, dead labels dropped, and a single-use compare folded into its branch.
/// <see cref="CBackendTests"/> proves the same output still runs correctly.
/// </summary>
[TestFixture]
public sealed class CEmitterQualityTests {

  /// <summary>Lowers and optimizes exactly as <c>pbc --emit-c</c> does (Driver.cs), then emits C.</summary>
  private static string EmitC(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    var module = IrLowering.TryLowerModule(Binder.Bind(unit, Dialect.Pb35));
    Assert.That(module, Is.Not.Null, "outside the IR lowering subset");
    var pipeline = IrPassManager.Standard();
    pipeline.RunOnModule(module!);
    foreach (var f in module!.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    pipeline.RunOnModule(module);
    Inliner.Run(module);
    pipeline.RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    pipeline.RunOnModule(module);
    GlobalDce.Run(module);
    return CEmitter.Emit(module);
  }

  // The bound is read at run time on purpose. These tests are about the SHAPE a loop takes in the
  // emitted C - the guard, the phi copies, the fall-through - and a constant-trip loop is unrolled
  // away by the pipeline before the emitter ever sees one.
  private const string _counterLoop = """
    DIM i AS INTEGER, total AS LONG, n AS INTEGER
    INPUT n
    FOR i = 1 TO n
      total = total + i * i
    NEXT i
    PRINT total
    END
    """;

  [Test]
  public void Emit_GivenIntegerMultiplyStoredToInteger_WhenEmittedC_ThenNoFloatRoundTrip() {
    // PB computes a%*b% in floating point; IntegerRecovery turns a product stored back into an
    // integer into a native integer multiply, so the C reads `a * b`, not `(int)((float)a*(float)b)`
    var c = EmitC("""
      DIM a AS INTEGER, b AS INTEGER, p AS INTEGER
      a = 7 : b = 6
      p = a * b
      PRINT p
      END
      """);
    Assert.Multiple(() => {
      Assert.That(c, Does.Not.Contain("(float)"), "the integer product must not route through float");
      Assert.That(c, Does.Not.Contain("(double)"), "nor through double");
      Assert.That(c, Does.Contain("*"), "a native integer multiply survives");
    });
  }

  [Test]
  public void Emit_GivenInlinedIntegerFunctionWithMixedPrecisionTree_WhenEmittedC_ThenRecovered() {
    // AddSq% = a%*a% + b% computes the product in SINGLE and widens it to DOUBLE for the add;
    // IntegerRecovery must see through the fpext so the inlined body reads as integer arithmetic,
    // not a float round-trip. Every operand is the target width, so this stays inside the modular
    // form the direct x86-16 back end uses (and the oracle validates).
    var c = EmitC("""
      DECLARE FUNCTION AddSq%(BYVAL a%, BYVAL b%)
      DIM i AS INTEGER, s AS INTEGER
      FOR i = 1 TO 5
        s = s + AddSq%(i, 2)
      NEXT i
      PRINT s
      END
      FUNCTION AddSq%(BYVAL a%, BYVAL b%)
        AddSq% = a% * a% + b%
      END FUNCTION
      """);
    Assert.Multiple(() => {
      Assert.That(c, Does.Not.Contain("(float)"), "the inlined integer body must not route through float");
      Assert.That(c, Does.Not.Contain("(double)"), "nor through double");
    });
  }

  [Test]
  public void Emit_GivenLoop_WhenEmittedC_ThenPhiCopiesAreDirectNotInt64Staged() {
    // the loop-carried counter/accumulator are not a cycle, so their phi copies are plain
    // assignments - no `t0 = (int64_t)...; v = (int16_t)t0` staging
    var c = EmitC(_counterLoop);
    Assert.That(c, Does.Not.Contain("(int64_t)"), "acyclic phi copies must not stage through int64 temps");
  }

  [Test]
  public void Emit_GivenLoop_WhenEmittedC_ThenControlFlowFallsThroughAndFoldsTheCompare() {
    var c = EmitC(_counterLoop);
    Assert.Multiple(() => {
      // the entry falls straight into the body - no `goto` to the very next label
      Assert.That(c, Does.Not.Match(@"goto (L\w+);\s*\1:"), "a goto to the immediately following label is fall-through");
      // exactly one compare, folded into the loop guard, not materialized as an int8 bool temp
      Assert.That(c, Does.Contain("<="), "the loop guard compares directly");
      Assert.That(System.Text.RegularExpressions.Regex.Matches(c, @"int8_t v\d+;").Count, Is.Zero,
        "the single-use loop compare folds into the branch, leaving no i1 temp");
    });
  }
}
