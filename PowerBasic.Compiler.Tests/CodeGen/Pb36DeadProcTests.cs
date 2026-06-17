using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O22 dead procedure elimination (<see cref="Pb36DeadProc"/>): an unreferenced
/// SUB/FUNCTION is unreachable code and is not emitted under optimization. The analysis
/// keeps every call target (incl. CODEPTR references) and every lambda; everything else is
/// dropped. pb35/unoptimized output is unchanged (the pass is gated on Optimize).
/// </summary>
[TestFixture]
public sealed class Pb36DeadProcTests {

  private static SemanticModel Bind(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static byte[] Emit(string source, bool optimize) {
    var model = Bind(source);
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  [Test]
  public void Live_GivenCalledAndUncalledProcedures_ThenOnlyCalledIsLive() {
    var model = Bind("""
      DECLARE FUNCTION Used%()
      DECLARE FUNCTION Dead%()
      PRINT Used%()
      FUNCTION Used%()
        Used% = 1
      END FUNCTION
      FUNCTION Dead%()
        Dead% = 2
      END FUNCTION
      """);
    var live = Pb36DeadProc.Live(model);
    Assert.That(live.Contains(model.Procedures["Used"]), Is.True, "a called procedure is live");
    Assert.That(live.Contains(model.Procedures["Dead"]), Is.False, "an uncalled procedure is dead");
  }

  [Test]
  public void Live_GivenAddressTakenProcedure_ThenKeptLive() {
    // CODEPTR is recorded as a reference, so an address-taken (never directly called)
    // procedure must stay live - its pointer could be invoked via CALL DWORD.
    var model = Bind("""
      DECLARE FUNCTION F%()
      DIM p AS INTEGER
      p = CODEPTR(F)
      FUNCTION F%()
        F% = 7
      END FUNCTION
      """);
    Assert.That(Pb36DeadProc.Live(model).Contains(model.Procedures["F"]), Is.True,
      "an address-taken procedure must not be eliminated");
  }

  [Test]
  public void Emit_GivenUnusedProcedureUnderOptimize_ThenAddsNoBytes() {
    const string none = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      """;
    const string withDead = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      FUNCTION Dead%()
        Dead% = 2 * 2 + 1
      END FUNCTION
      """;
    Assert.That(Emit(withDead, optimize: true).Length, Is.EqualTo(Emit(none, optimize: true).Length),
      "the unreferenced FUNCTION should contribute zero bytes once eliminated");
  }

  [Test]
  public void Emit_GivenUnusedProcedureWithoutOptimize_ThenStillEmitted() {
    // without optimization the dead procedure stays (the pass is gated on Optimize),
    // so the image is strictly larger than the procedure-free version.
    const string none = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      """;
    const string withDead = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      FUNCTION Dead%()
        Dead% = 2 * 2 + 1
      END FUNCTION
      """;
    Assert.That(Emit(withDead, optimize: false).Length, Is.GreaterThan(Emit(none, optimize: false).Length),
      "unoptimized output must still contain the procedure (golden-gate behaviour preserved)");
  }

  [Test]
  public void Execute_GivenUnusedProcedureUnderOptimize_ThenProgramRunsCorrectly() {
    const string source = """
      DIM x AS INTEGER
      x = 6
      PRINT x * 7
      FUNCTION Dead%()
        Dead% = 999
      END FUNCTION
      """;
    var exe = Emit(source, optimize: true);
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo(" 42\n"),
      "eliminating the dead procedure must not change observable behaviour");
  }
}
