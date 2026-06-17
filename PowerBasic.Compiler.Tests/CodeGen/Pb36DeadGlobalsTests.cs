using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O23 data tree-shaking (<see cref="Pb36DeadGlobals"/>): a module scalar global no
/// reachable code reads is dead - its slot and every pure store to it vanish, and a CODEPTR in
/// such a store no longer keeps its target procedure alive (the cascade). Address-taken
/// (VARPTR) and read globals are KEPT. The whole feature is gated on Optimize for a
/// self-contained main, so pb35/unoptimized output is unchanged.
/// </summary>
[TestFixture]
public sealed class Pb36DeadGlobalsTests {

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

  // self-contained main: every non-nested procedure is fully owned; checking off unless a test
  // explicitly turns on $ERROR (then NumericCheckingPossible-style behaviour is exercised here).
  private static Pb36DeadGlobals.Result Shake(SemanticModel model) {
    var checking = model.MetaStatements.Any(m => m.Command.Equals("ERROR", System.StringComparison.OrdinalIgnoreCase)
      && m.Arguments.Count >= 2
      && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "BOUNDS" or "ALL"
      && m.Arguments[^1].Text.Equals("ON", System.StringComparison.OrdinalIgnoreCase));
    return Pb36DeadGlobals.Analyze(model, _ => true, checking);
  }

  // ---- the live-set analysis -----------------------------------------------

  [Test]
  public void Analyze_GivenGlobalAssignedConstantNeverRead_ThenGlobalAndStoreAreDead() {
    var model = Bind("""
      DIM g AS INTEGER
      g = 42
      PRINT 1
      """);
    var result = Shake(model);
    var g = model.ModuleVariables["g"];
    Assert.That(result.DeadGlobals, Does.Contain(g), "a constant-only, never-read global is dead");
    Assert.That(result.DeadStores, Has.Count.EqualTo(1), "its single store is dead too");
  }

  [Test]
  public void Analyze_GivenGlobalThatIsRead_ThenKept() {
    var model = Bind("""
      DIM g AS INTEGER
      g = 42
      PRINT g
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["g"]), "a read global stays live");
    Assert.That(result.DeadStores, Is.Empty);
  }

  [Test]
  public void Analyze_GivenVarptrOfNeverReadGlobal_ThenKept() {
    // its address escapes - a conservative read; the global must survive (guard).
    var model = Bind("""
      DIM g AS INTEGER
      DIM p AS WORD
      g = 7
      p = VARPTR(g)
      PRINT p
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["g"]), "VARPTR'd global is kept");
  }

  [Test]
  public void Analyze_GivenCodeptrStoredInDeadGlobal_ThenGlobalStoreAndTargetCascadeDead() {
    // the exact cascade: F is referenced ONLY by CODEPTR in a never-read global's store, so the
    // global, its store, AND F are all dead - which only the fixpoint (not one-shot) reveals.
    var model = Bind("""
      DECLARE FUNCTION F%()
      DIM gp AS WORD
      gp = CODEPTR(F)
      PRINT 1
      FUNCTION F%()
        F% = 9
      END FUNCTION
      """);
    var result = Shake(model);
    var gp = model.ModuleVariables["gp"];
    Assert.That(result.DeadGlobals, Does.Contain(gp), "the never-read pointer global is dead");
    Assert.That(result.DeadStores, Has.Count.EqualTo(1), "the CODEPTR store is dead");
    Assert.That(result.LiveProcedures, Does.Not.Contain(model.Procedures["F"]),
      "F was kept alive only by a CODEPTR in a now-dead store - it cascades to dead");
  }

  [Test]
  public void Analyze_GivenCodeptrInReadGlobal_ThenTargetKeptLive() {
    // gp IS read (passed to CALL DWORD), so its CODEPTR edge is real and F stays live.
    var model = Bind("""
      DECLARE FUNCTION F%()
      DIM gp AS WORD
      gp = CODEPTR(F)
      PRINT gp
      FUNCTION F%()
        F% = 9
      END FUNCTION
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["gp"]));
    Assert.That(result.LiveProcedures, Does.Contain(model.Procedures["F"]), "a read pointer keeps its target live");
  }

  [Test]
  public void Analyze_GivenSharedGlobalNeverRead_ThenKept() {
    var model = Bind("""
      DIM g AS INTEGER
      SHARED s AS INTEGER
      g = 1
      s = 2
      PRINT 1
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Contain(model.ModuleVariables["g"]), "the private global is dead");
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["s"]), "a SHARED global is kept (visible elsewhere)");
  }

  [Test]
  public void Analyze_GivenWriteWithSideEffectingRhs_ThenKept() {
    // the store's RHS calls a function - removing the store would drop the side effect; keep it.
    var model = Bind("""
      DECLARE FUNCTION Eff%()
      DIM g AS INTEGER
      g = Eff%()
      PRINT 1
      FUNCTION Eff%()
        PRINT "x"
        Eff% = 0
      END FUNCTION
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["g"]),
      "a global whose write has a side-effecting RHS is kept");
  }

  [Test]
  public void Analyze_GivenArithmeticWriteUnderErrorOverflow_ThenKept() {
    // $ERROR OVERFLOW ON makes `o% = a% + a%` trappable (Error 6) - dropping the store would
    // skip the trap, so o% (and a%, read by the RHS) must be kept even though never read.
    var model = Bind("""
      $ERROR OVERFLOW ON
      DIM a AS INTEGER
      DIM o AS INTEGER
      a = 30000
      o = a + a
      PRINT 1
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["o"]),
      "a trap-capable arithmetic store under $ERROR OVERFLOW is kept");
    Assert.That(result.DeadGlobals, Does.Not.Contain(model.ModuleVariables["a"]),
      "the operand of a kept arithmetic store is read, so it is kept too");
  }

  [Test]
  public void Analyze_GivenDottedNameGlobalThatIsRead_ThenKept() {
    // a dotted variable name (Max.X) binds on a MemberExpr, not a NameExpr - the read in the
    // PRINT must still be seen, or its slot would wrongly vanish while a reference dangles.
    var model = Bind("""
      Max.X = 319
      Max.Y = 199
      PRINT Max.X + Max.Y
      """);
    var result = Shake(model);
    Assert.That(result.DeadGlobals, Is.Empty, "a read dotted-name global is kept");
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
