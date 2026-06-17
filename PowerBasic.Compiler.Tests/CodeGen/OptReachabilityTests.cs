using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 O22 reachability (<see cref="OptReachability"/>): transitive dead-procedure
/// elimination rooted at the program's top-level "main". Covers the complete node walker
/// (the soundness foundation) and the call-graph trace. pb35/unoptimized output is
/// unchanged (the pass is gated on Optimize).
/// </summary>
[TestFixture]
public sealed class OptReachabilityTests {

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

  private static IReadOnlyList<string> NameRefs(IReadOnlyList<Statement> body)
    => [.. OptReachability.DescendantNodes(body).OfType<NameExpr>().Select(n => n.Name)];

  // ---- the complete node walker (soundness foundation) ----------------------

  [Test]
  public void DescendantNodes_GivenNestedAndTupleBearingStatements_ThenFindsEveryExpression() {
    // references buried in a nested IF body, a FOR header, and a LINE statement's coordinate
    // tuples must all be discovered - a missed one would let DCE drop live code.
    var model = Bind("""
      DIM a AS INTEGER, b AS INTEGER, c AS INTEGER, d AS INTEGER
      IF a THEN
        FOR b = c TO d
          PRINT b
        NEXT
      END IF
      LINE (a, b)-(c, d)
      """);
    var names = NameRefs(model.MainBody);
    foreach (var n in new[] { "a", "b", "c", "d" })
      Assert.That(names, Does.Contain(n), $"reference to {n} must be reachable through the walker");
  }

  [Test]
  public void DescendantNodes_GivenNestedProcedure_ThenDoesNotDescendIntoItsBody() {
    // a nested SUB is a separate procedure; the outer walk must not pull its body in
    var model = Bind("""
      DECLARE SUB Outer()
      Outer
      SUB Outer()
        DIM seenOuter AS INTEGER
        seenOuter = 1
        SUB Inner()
          DIM seenInner AS INTEGER
          seenInner = 2
        END SUB
      END SUB
      """);
    var outer = model.Procedures["Outer"];
    var names = NameRefs(outer.Body!);
    Assert.That(names, Does.Contain("seenOuter"));
    Assert.That(names, Does.Not.Contain("seenInner"), "the nested procedure's body is walked on its own, not as part of the outer");
  }

  // ---- transitive reachability ---------------------------------------------

  [Test]
  public void Live_GivenTransitiveDeadChain_ThenWholeChainIsDead() {
    // main calls A; A calls B (both live). C calls D, but nothing calls C - so C AND D are
    // dead, which only a transitive analysis (not "referenced anywhere") can see.
    var model = Bind("""
      DECLARE SUB A()
      DECLARE SUB B()
      DECLARE SUB C()
      DECLARE SUB D()
      A
      SUB A()
        B
      END SUB
      SUB B()
      END SUB
      SUB C()
        D
      END SUB
      SUB D()
      END SUB
      """);
    var live = OptReachability.LiveProcedures(model, model.MainBody);
    Assert.That(live.Contains(model.Procedures["A"]), Is.True);
    Assert.That(live.Contains(model.Procedures["B"]), Is.True);
    Assert.That(live.Contains(model.Procedures["C"]), Is.False, "C is never called - dead");
    Assert.That(live.Contains(model.Procedures["D"]), Is.False, "D is reached only from the dead C - transitively dead");
  }

  [Test]
  public void Live_GivenNestedFunctionInsideDeadProcedure_ThenBothDead() {
    // Dead is never called, so its body is never walked - the nested Inner it calls is
    // therefore never reached and is purged along with its dead-end container.
    var model = Bind("""
      DECLARE SUB Alive()
      Alive
      SUB Alive()
        PRINT 1
      END SUB
      DECLARE SUB Dead()
      SUB Dead()
        Inner
        SUB Inner()
          PRINT 2
        END SUB
      END SUB
      """);
    var live = OptReachability.LiveProcedures(model, model.MainBody);
    var inner = model.ProcedureList.First(p => p.IsNested && p.Name.Contains("Inner", StringComparison.OrdinalIgnoreCase));
    Assert.That(live.Contains(model.Procedures["Alive"]), Is.True);
    Assert.That(live.Contains(model.Procedures["Dead"]), Is.False, "Dead is never called");
    Assert.That(live.Contains(inner), Is.False, "a nested function inside a dead procedure is itself dead");
  }

  [Test]
  public void Live_GivenAddressTakenProcedure_ThenKeptLive() {
    var model = Bind("""
      DECLARE FUNCTION F%()
      DIM p AS INTEGER
      p = CODEPTR(F)
      FUNCTION F%()
        F% = 7
      END FUNCTION
      """);
    Assert.That(OptReachability.LiveProcedures(model, model.MainBody).Contains(model.Procedures["F"]), Is.True,
      "a CODEPTR'd procedure is reachable (its pointer could be called)");
  }

  // ---- integrated emission -------------------------------------------------

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
