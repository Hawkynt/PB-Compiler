using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Loop unrolling on the IR: O0007 full unrolling for small constant-trip loops and O0063 Duff-style
/// factor-four unrolling for canonical unit-stride loops with a run-time trip count.
///
/// Two things have to be true of it, and only one is about the IR. It has to actually unroll (a pass
/// that quietly declines everything passes any behavioural test), and the program has to still print
/// the same thing. The second is the one that matters, and it is checked by rendering the IR back to
/// BASIC and running it - unrolling changes the code by definition, so no assertion about the code
/// could mean anything.
/// </summary>
[TestFixture]
public sealed class LoopUnrollTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Lowered(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    foreach (var fn in module!.Functions)
      if (!fn.IsDeclaration)
        Mem2Reg.Run(fn);
    return module;
  }

  private static int Unroll(IrModule module) {
    var count = 0;
    foreach (var fn in module.Functions)
      if (!fn.IsDeclaration)
        count += LoopUnroll.Run(fn);
    return count;
  }

  private static string Run(string source, bool optimize = true) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = optimize };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>Unrolls, then renders and runs - the output must be what the program always printed.</summary>
  private static void UnrollsAndStillPrintsTheSame(string source) {
    var expected = Run(source);
    var module = Lowered(source);
    Assert.That(Unroll(module), Is.GreaterThan(0), "nothing was unrolled, so this proves nothing");
    Assert.That(Run(IrBasicWriter.Write(module)), Is.EqualTo(expected));
  }

  [Test]
  public void Unroll_GivenAConstantTripLoop_ThenTheBodyIsCopiedAndTheLoopIsGone() {
    var module = Lowered("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 4
        s = s + i
      NEXT i
      PRINT s
      END
      """);
    var before = module.FindFunction("main")!.Blocks.Count;

    Assert.That(Unroll(module), Is.EqualTo(1));

    var main = module.FindFunction("main")!;
    Assert.That(main.Blocks.Any(b => b.Label.StartsWith("unroll", StringComparison.Ordinal)), "the body has to be copied");
    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrPhi>(), Is.Empty,
      "a fully unrolled loop has no loop-carried value left");
    Assert.That(main.Blocks.Count, Is.Not.EqualTo(before));
  }

  [Test]
  public void Unroll_GivenAnAccumulator_ThenTheProgramStillPrintsTheSame() =>
    UnrollsAndStillPrintsTheSame("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 5
        s = s + i * i
      NEXT i
      PRINT s
      END
      """);

  /// <summary>
  /// O0066: the counter value of every fully-unrolled copy is known before cloning. The unroller has
  /// to seed that literal directly rather than threading the previous copy's increment instruction,
  /// otherwise later copies only become constant after another optimization sweep.
  /// </summary>
  [Test]
  public void Unroll_GivenCounterDerivedArithmetic_ThenEveryCopyReceivesCounterLiteral() {
    var module = Lowered("""
      DIM i AS INTEGER
      FOR i = 1 TO 7 STEP 2
        PRINT i * i
      NEXT i
      END
      """);

    Assert.That(Unroll(module), Is.EqualTo(1));

    var bodies = module.FindFunction("main")!.Blocks
      .Where(b => b.Label.StartsWith("unroll", StringComparison.Ordinal)
        && b.Label.Contains("body", StringComparison.Ordinal))
      .ToList();

    Assert.That(bodies, Has.Count.EqualTo(4));
    // PRINT promotes the product, so the multiply itself is an FMul over casts rather than an
    // integer Mul over the counter. What O0066 guarantees is the value behind those casts: each copy
    // already sees its own counter as a literal, before InstCombine or SCCP run.
    Assert.That(
      bodies.Select(b => b.Instructions.SelectMany(i => i.Operands).OfType<IrConstantInt>()
        .Select(c => c.Value).Distinct().Single()),
      Is.EqualTo(new long[] { 1, 3, 5, 7 }),
      "every copy must see the induction variable as a literal before InstCombine/SCCP run");
  }

  [Test]
  public void Unroll_GivenOutputInTheBody_ThenEveryIterationStillPrints() =>
    UnrollsAndStillPrintsTheSame("""
      DIM i AS INTEGER
      FOR i = 1 TO 4
        PRINT "i="; i
      NEXT i
      END
      """);

  [Test]
  public void Unroll_GivenAStep_ThenTheCounterProgressionIsPreserved() =>
    UnrollsAndStillPrintsTheSame("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 0 TO 9 STEP 3
        s = s + i
        PRINT i; s
      NEXT i
      END
      """);

  [Test]
  public void Unroll_GivenADescendingLoop_ThenItRunsTheSameNumberOfTimes() =>
    UnrollsAndStillPrintsTheSame("""
      DIM i AS INTEGER
      FOR i = 5 TO 1 STEP -1
        PRINT i;
      NEXT i
      PRINT
      END
      """);

  [Test]
  public void Unroll_GivenTwoLoopCarriedValues_ThenBothAdvanceTogether() =>
    UnrollsAndStillPrintsTheSame("""
      DIM i AS INTEGER
      DIM a AS INTEGER
      DIM b AS INTEGER
      a = 0
      b = 1
      FOR i = 1 TO 6
        b = a + b
        a = b - a
      NEXT i
      PRINT a; b
      END
      """);

  /// <summary>
  /// O0132, whole-loop compile-time evaluation - which nobody wrote a pass for. It falls out of
  /// unrolling composing with the constant propagation and dead-code elimination that were already
  /// there: the counter becomes a constant in each copy, the arithmetic folds, and the copies go. A
  /// ported optimization that ENABLES another is the compounding the IR path was supposed to get, so
  /// it is worth pinning rather than noticing once.
  /// </summary>
  [Test]
  public void Unroll_GivenAConstantLoop_ThenThePipelineEvaluatesTheWholeThingAtCompileTime() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 5
        s = s + i
      NEXT i
      PRINT s
      END
      """), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.FindFunction("main")!;
    Assert.That(main.Blocks.SelectMany(b => b.Instructions).OfType<IrBinary>(), Is.Empty,
      "the whole loop folds - no arithmetic should be left");
    var printed = main.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>()
      .First(c => (c.Callee as IrFunction)?.Name == "rt_print_i16");
    Assert.That(((IrConstantInt)printed.Args.First()).Value, Is.EqualTo(15), "1+2+3+4+5");
  }

  /// <summary>
  /// O0063: every remainder and the zero-trip case, through the prologue-plus-unrolled-main shape.
  /// The assertion is about that shape rather than a computed-jump dispatch: the loop keeps one
  /// header per cycle, which is what the loop passes after this one can reason about.
  /// </summary>
  [Test]
  public void Unroll_GivenARuntimeBound_ThenAPrologueAndFourfoldMainLoopCoverEveryRemainder() {
    const string source = """
      DECLARE SUB Emit(BYVAL n AS INTEGER)
      CALL Emit(0)
      CALL Emit(1)
      CALL Emit(2)
      CALL Emit(3)
      CALL Emit(4)
      CALL Emit(5)
      END
      SUB Emit(BYVAL n AS INTEGER)
        DIM i AS INTEGER
        DIM s AS INTEGER
        s = 0
        FOR i = 1 TO n
          s = s * 10 + i
        NEXT i
        PRINT s
      END SUB
      """;
    var expected = Run(source, optimize: false);
    var module = Lowered(source);

    Assert.That(Unroll(module), Is.EqualTo(1));

    var emit = module.Functions.Single(f => f.Name.Equals("Emit", StringComparison.OrdinalIgnoreCase));
    var guard = emit.Blocks.Single(b => b.Label == "unroll4.guard");
    Assert.That(guard.Terminator, Is.InstanceOf<IrCondBr>(), "a zero-trip loop has to be able to skip everything");
    for (var copy = 0; copy < 4; ++copy)
      Assert.That(emit.Blocks.Any(b => b.Label.StartsWith($"unroll4.c{copy}.", StringComparison.Ordinal)),
        Is.True, $"the main loop is missing body copy {copy}");

    // the prologue is the ORIGINAL loop, bounded by the remainder, and falls into the main loop when
    // it runs out. Both cycles have exactly one entry, which is the property the rest of the
    // pipeline depends on - a Duff dispatch into shared copies would give the cycle four.
    var main = emit.Blocks.Single(b => b.Label == "unroll4.main");
    var prologue = emit.Blocks.Single(b => b.Terminator is IrCondBr onward && ReferenceEquals(onward.IfFalse, main));
    Assert.That(prologue.Predecessors.Count(), Is.EqualTo(2), "the prologue loop must stay single-entry");
    Assert.That(main.Predecessors.Count(), Is.EqualTo(2), "the main loop must stay single-entry");
    Assert.That(IrVerifier.Verify(emit), Is.Empty);
    Assert.That(Run(IrBasicWriter.Write(module), optimize: false), Is.EqualTo(expected));
  }

  /// <summary>
  /// The point of the shape: both cycles are reducible, so LICM still sees a preheader and hoists
  /// what does not depend on the counter. A shared computed entry would leave it in every copy.
  /// </summary>
  [Test]
  public void Unroll_GivenARuntimeBoundAndInvariantWork_ThenTheInvariantStillLeavesTheLoop() {
    var module = IrLowering.TryLowerModule(Bind("""
      DIM i AS INTEGER, n AS INTEGER, k AS INTEGER, t AS INTEGER
      INPUT n
      INPUT k
      t = 0
      FOR i = 1 TO n
        t = t + k * 7
      NEXT i
      PRINT t
      END
      """), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);

    var main = module!.FindFunction("main")!;
    Assert.That(main.Blocks.Any(b => b.Label.StartsWith("unroll4.", StringComparison.Ordinal)),
      Is.True, "the runtime loop has to have been unrolled for this to prove anything");
    var bodies = main.Blocks.Where(b => b.Label.Contains("body", StringComparison.Ordinal)).ToList();
    Assert.That(bodies, Is.Not.Empty, "the loops have to survive for this to mean anything");
    Assert.That(bodies.SelectMany(b => b.Instructions).OfType<IrBinary>()
      .Count(i => i.Op is IrBinaryOp.Mul or IrBinaryOp.FMul), Is.Zero,
      "k * 7 does not depend on the counter and belongs outside both loops");
  }

  [Test]
  public void Unroll_GivenADescendingRuntimeBound_ThenTheUnrolledLoopPreservesTheDirection() {
    const string source = """
      DECLARE SUB Emit(BYVAL n AS INTEGER)
      CALL Emit(7)
      CALL Emit(5)
      CALL Emit(4)
      CALL Emit(3)
      CALL Emit(2)
      CALL Emit(1)
      END
      SUB Emit(BYVAL n AS INTEGER)
        DIM i AS INTEGER
        DIM s AS INTEGER
        s = 0
        FOR i = 5 TO n STEP -1
          s = s * 10 + i
        NEXT i
        PRINT s
      END SUB
      """;
    var expected = Run(source, optimize: false);
    var module = Lowered(source);

    Assert.That(Unroll(module), Is.EqualTo(1));

    var emit = module.Functions.Single(f => f.Name.Equals("Emit", StringComparison.OrdinalIgnoreCase));
    Assert.That(IrVerifier.Verify(emit), Is.Empty);
    Assert.That(Run(IrBasicWriter.Write(module), optimize: false), Is.EqualTo(expected));
  }

  /// <summary>
  /// A modular step larger than one can jump across the comparison boundary and wrap back into the
  /// accepted range. O0063 deliberately leaves that shape scalar until a trip-count proof models it.
  /// </summary>
  [Test]
  public void Unroll_GivenARuntimeNonUnitStep_ThenItDeclines() {
    var module = Lowered("""
      DECLARE SUB Emit(BYVAL n AS INTEGER)
      CALL Emit(9)
      END
      SUB Emit(BYVAL n AS INTEGER)
        DIM i AS INTEGER
        FOR i = 1 TO n STEP 2
          PRINT i
        NEXT i
      END SUB
      """);

    Assert.That(Unroll(module), Is.Zero);
  }

  /// <summary>Too many constant iterations to be worth copying: correct to decline, and it must.</summary>
  [Test]
  public void Unroll_GivenALongLoop_ThenItDeclinesRatherThanExplode() {
    var module = Lowered("""
      DIM i AS INTEGER
      DIM s AS INTEGER
      s = 0
      FOR i = 1 TO 1000
        s = s + i
      NEXT i
      PRINT s
      END
      """);

    Assert.That(Unroll(module), Is.Zero);
  }
}
