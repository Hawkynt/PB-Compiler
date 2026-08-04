using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Full unrolling of a constant-trip counted loop, on the IR - the first optimization ported from the
/// direct emitter to the retargetable path.
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

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
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

  /// <summary>A loop whose trip count is not known must be left alone rather than guessed at.</summary>
  [Test]
  public void Unroll_GivenARuntimeBound_ThenItDeclines() {
    var module = Lowered("""
      DIM i AS INTEGER
      DIM n AS INTEGER
      INPUT n
      FOR i = 1 TO n
        PRINT i
      NEXT i
      END
      """);

    Assert.That(Unroll(module), Is.Zero);
  }

  /// <summary>Too many iterations to be worth copying: correct to decline, and it must.</summary>
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
