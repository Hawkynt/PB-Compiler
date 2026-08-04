using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Emit;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Emit;

/// <summary>
/// The IR rendered back to PowerBASIC, checked by <b>round trip</b>: source → IR → source → compile →
/// run, against the same program compiled directly.
///
/// Reading the generated BASIC proves nothing. The only question that matters is whether it computes
/// what the IR said, and the only way to answer it is to compile and run it. That is also what makes
/// this worth building at all: an optimization pass becomes checkable by rendering the IR before and
/// after and comparing what the two programs PRINT, rather than by asserting instruction counts.
///
/// Everything here goes through <see cref="Optimized"/>, so the IR under test is the post-mem2reg SSA
/// form with phis and constant folding - the shape the writer actually has to survive, not a
/// straight-line lowering that would never exercise SSA destruction.
/// </summary>
[TestFixture]
public sealed class IrBasicWriterTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static IrModule Optimized(string source) {
    var module = IrLowering.TryLowerModule(Bind(source), out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    IrPassManager.Standard().RunOnModule(module!);
    return module!;
  }

  private static string Run(string source) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|");
  }

  /// <summary>
  /// The round trip. The rendered function is called from a driver that prints its result, so the two
  /// programs are observably comparable even though the IR has no PRINT in it.
  /// </summary>
  private static void RoundTrips(string body, string call, string expected) {
    var original = $"{body}\nPRINT {call}\nEND\n";
    Assert.That(Run(original), Is.EqualTo(expected), "the original program does not do what the test claims");

    var module = Optimized(original);
    var fn = module.Functions.Single(f => !f.IsDeclaration && !f.Name.Equals("main", StringComparison.OrdinalIgnoreCase));
    var rendered = IrBasicWriter.Write(fn);

    var rebuilt = $"PRINT {call}\nEND\n{rendered}";
    Assert.That(Run(rebuilt), Is.EqualTo(expected),
      $"the BASIC rendered from the IR computes something else:\n{rendered}");
  }

  [Test]
  public void RoundTrip_GivenArithmetic_ThenTheRenderedFunctionComputesTheSame() =>
    RoundTrips("""
      FUNCTION Poly%(BYVAL x%)
        Poly% = x% * x% + 3 * x% - 2
      END FUNCTION
      """, "Poly%(5)", "38");

  [Test]
  public void RoundTrip_GivenABranch_ThenBothArmsSurvive() =>
    RoundTrips("""
      FUNCTION Clamp%(BYVAL x%)
        IF x% > 10 THEN
          Clamp% = 10
        ELSE
          Clamp% = x%
        END IF
      END FUNCTION
      """, "Clamp%(42); Clamp%(3)", "10  3");

  /// <summary>A loop is where SSA destruction earns its keep: the counter is a phi with two edges.</summary>
  [Test]
  public void RoundTrip_GivenALoop_ThenThePhisAreCopiedOnTheRightEdges() =>
    RoundTrips("""
      FUNCTION Total%(BYVAL n%)
        DIM s AS INTEGER
        DIM i AS INTEGER
        s = 0
        FOR i = 1 TO n%
          s = s + i
        NEXT i
        Total% = s
      END FUNCTION
      """, "Total%(10)", "55");

  /// <summary>Two phis in one block have to be copied simultaneously, not one after the other.</summary>
  [Test]
  public void RoundTrip_GivenTwoLoopCarriedValues_ThenNeitherClobbersTheOther() =>
    RoundTrips("""
      FUNCTION Fib%(BYVAL n%)
        DIM a AS INTEGER
        DIM b AS INTEGER
        DIM i AS INTEGER
        a = 0
        b = 1
        FOR i = 1 TO n%
          b = a + b
          a = b - a
        NEXT i
        Fib% = a
      END FUNCTION
      """, "Fib%(10)", "55");

  [Test]
  public void RoundTrip_GivenIntegerDivisionAndModulo_ThenBothKeepTheirSign() =>
    RoundTrips("""
      FUNCTION DivMod%(BYVAL a%, BYVAL b%)
        DivMod% = (a% \ b%) * 100 + (a% MOD b%)
      END FUNCTION
      """, "DivMod%(-17, 5)", "-302");   // -17 \ 5 = -3 (toward zero) and -17 MOD 5 = -2

  [Test]
  public void RoundTrip_GivenBitwiseOperators_ThenTheyRenderAsThemselves() =>
    RoundTrips("""
      FUNCTION Masked%(BYVAL x%)
        Masked% = (x% AND 12) OR (x% XOR 5)
      END FUNCTION
      """, "Masked%(10)", "15");

  [Test]
  public void RoundTrip_GivenAComparisonUsedAsAValue_ThenItKeepsBasicsTruthValue() =>
    RoundTrips("""
      FUNCTION Sign%(BYVAL x%)
        Sign% = (x% > 0) - (x% < 0)
      END FUNCTION
      """, "Sign%(7); Sign%(-7); Sign%(0)", "-1  1  0");   // BASIC's TRUE is -1, so this reads inverted

  [Test]
  public void RoundTrip_GivenALongResult_ThenTheWidthSurvives() =>
    RoundTrips("""
      FUNCTION Big&(BYVAL x%)
        Big& = x% * 100000&
      END FUNCTION
      """, "Big&(300)", "30000000");

  /// <summary>
  /// A construct the writer cannot render exactly must throw, not approximate. Emitting "close
  /// enough" BASIC would make a round-trip failure ambiguous between a bad pass and a bad rendering.
  /// </summary>
  [Test]
  public void Write_GivenAConstructItCannotRender_ThenItSaysSoRatherThanGuessing() {
    // an array is real storage: BASIC can name it, but recovering the DIM and the subscripts from
    // alloca-plus-GEP is work this writer has not done yet, so it says so
    var module = Optimized("""
      DIM a%(0 TO 9)
      a%(3) = 7
      PRINT a%(3)
      END
      """);

    var thrown = Assert.Throws<IrBasicWriterException>(
      () => IrBasicWriter.Write(module.FindFunction("main")!));
    Assert.That(thrown!.What, Is.Not.Empty, "the refusal has to name the construct");
  }
}
