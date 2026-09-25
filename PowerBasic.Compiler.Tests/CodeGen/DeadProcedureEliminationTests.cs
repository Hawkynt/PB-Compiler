using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O22: a procedure nothing reachable calls is not emitted under Optimize - transitively, so a
/// chain of procedures that only call each other goes as a whole - while pb35/unoptimized output
/// keeps every procedure. The IR's <c>GlobalDce</c> decides it on the call graph.
/// </summary>
[TestFixture]
public sealed class DeadProcedureEliminationTests {

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
  public void Emit_GivenTransitiveDeadChainUnderOptimize_ThenAddsNoBytes() {
    const string none = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      """;
    const string withDeadChain = """
      DIM x AS INTEGER
      x = 5
      PRINT x
      DECLARE SUB C()
      DECLARE SUB D()
      SUB C()
        D
      END SUB
      SUB D()
        PRINT 1
      END SUB
      """;
    Assert.That(Emit(withDeadChain, optimize: true).Length, Is.EqualTo(Emit(none, optimize: true).Length),
      "an uncalled procedure and everything only it reaches contribute zero bytes");
  }

  [Test]
  public void Emit_GivenUnusedProcedureWithoutOptimize_ThenStillEmitted() {
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
      "without optimization the procedure stays (golden-gate behaviour preserved)");
  }

  [Test]
  public void Execute_GivenDeadProcedureUnderOptimize_ThenProgramRunsCorrectly() {
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
      "eliminating dead procedures must not change observable behaviour");
  }
}
