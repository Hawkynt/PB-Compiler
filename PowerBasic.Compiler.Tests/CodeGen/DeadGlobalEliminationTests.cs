using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O23 data tree-shaking: a module scalar global no
/// reachable code reads is dead - its slot and every pure store to it vanish, and a CODEPTR in
/// such a store no longer keeps its target procedure alive (the cascade). Address-taken
/// (VARPTR) and read globals are KEPT. The whole feature is gated on Optimize for a
/// self-contained main, so pb35/unoptimized output is unchanged.
/// </summary>
[TestFixture]
public sealed class DeadGlobalEliminationTests {

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

  // ---- integrated emission (image size) -------------------------------------

  // an anchor that keeps a real (full) image so the trivial-program lowering never applies,
  // letting the size delta isolate exactly the dead global / store / cascaded method.
  private const string Anchor = """
    DIM keep AS INTEGER
    keep = 5
    PRINT keep
    """;

  [Test]
  public void Emit_GivenDeadConstantGlobalUnderOptimize_ThenAddsNoBytes() {
    var none = Anchor;
    var withDeadGlobal = """
      DIM g AS INTEGER
      g = 42
      """ + "\n" + Anchor;
    Assert.That(Emit(withDeadGlobal, optimize: true).Length, Is.EqualTo(Emit(none, optimize: true).Length),
      "a dead global's slot and store contribute zero bytes under Optimize");
  }

  [Test]
  public void Emit_GivenCodeptrCascadeUnderOptimize_ThenAddsNoBytes() {
    var none = Anchor;
    var withCascade = """
      DECLARE FUNCTION F%()
      DIM gp AS WORD
      gp = CODEPTR(F)
      """ + "\n" + Anchor + "\n" + """
      FUNCTION F%()
        F% = 9
      END FUNCTION
      """;
    Assert.That(Emit(withCascade, optimize: true).Length, Is.EqualTo(Emit(none, optimize: true).Length),
      "the dead global, its CODEPTR store, AND the cascaded-dead F all contribute zero bytes");
  }

  [Test]
  public void Emit_GivenReadGlobalUnderOptimize_ThenLargerThanDeadGlobal() {
    var deadGlobal = """
      DIM g AS INTEGER
      g = 42
      """ + "\n" + Anchor;
    var readGlobal = """
      DIM g AS INTEGER
      g = 42
      PRINT g
      """ + "\n" + Anchor;
    Assert.That(Emit(readGlobal, optimize: true).Length, Is.GreaterThan(Emit(deadGlobal, optimize: true).Length),
      "a read global keeps its slot and store - larger than the eliminated case");
  }

  [Test]
  public void Emit_GivenDeadGlobalWithoutOptimize_ThenRetained() {
    var none = Anchor;
    var withDeadGlobal = """
      DIM g AS INTEGER
      g = 42
      """ + "\n" + Anchor;
    Assert.That(Emit(withDeadGlobal, optimize: false).Length, Is.GreaterThan(Emit(none, optimize: false).Length),
      "without optimization the dead global is retained (golden-gate behaviour)");
  }

  [Test]
  public void Execute_GivenDeadGlobalAndCascadedDeadMethodUnderOptimize_ThenProgramRunsCorrectly() {
    const string source = """
      DECLARE FUNCTION Unused%()
      DIM gp AS WORD
      DIM x AS INTEGER
      gp = CODEPTR(Unused)
      x = 6
      PRINT x * 7
      FUNCTION Unused%()
        Unused% = 123
      END FUNCTION
      """;
    var exe = Emit(source, optimize: true);
    Assert.That(DosBoxRunner.Normalize(DosBoxRunner.Run(exe)), Is.EqualTo(" 42\n"),
      "eliminating a dead global, its store, and the cascaded-dead method must not change behaviour");
  }
}
