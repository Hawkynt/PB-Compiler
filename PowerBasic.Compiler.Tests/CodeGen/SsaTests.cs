using PowerBasic.Compiler.CodeGen.Ssa;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// SSA mid-end (docs/PB36.md): CFG construction, dominators/frontiers, SSA form
/// and SCCP. These pin the analysis in isolation; the behavioral contract
/// (byte-identical output) is enforced by the differential harness once SCCP is
/// wired into the emitter.
/// </summary>
[TestFixture]
public sealed class SsaTests {

  /// <summary>Parses a snippet and returns its executable top-level statements (the main body).</summary>
  private static IReadOnlyList<Statement> Body(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    return unit.Statements
      .Where(s => s is not (SubDecl or FunctionDecl or DeclareStmt or DefFnDecl))
      .ToList();
  }

  /// <summary>Binds a snippet and builds CFG + SSA over its main body.</summary>
  private static (SemanticModel Model, ControlFlowGraph Cfg, SsaForm? Ssa) BuildSsa(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var body = unit.Statements.Where(s => s is not (SubDecl or FunctionDecl or DeclareStmt or DefFnDecl)).ToList();
    var cfg = ControlFlowGraph.TryBuild(body)!;
    return (model, cfg, SsaForm.TryBuild(model, cfg));
  }

  #region CFG construction

  [Test]
  public void Cfg_GivenStraightLine_ThenSingleChainToExit() {
    var cfg = ControlFlowGraph.TryBuild(Body("x% = 1\ny% = 2\nz% = x% + y%"));
    Assert.That(cfg, Is.Not.Null);
    // entry holds all three statements, then an unconditional edge to exit
    Assert.That(cfg!.Entry.Statements, Has.Count.EqualTo(3));
    Assert.That(cfg.Entry.Condition, Is.Null);
    Assert.That(cfg.Entry.TrueSucc, Is.EqualTo(cfg.Exit));
  }

  [Test]
  public void Cfg_GivenIfElse_ThenDiamondWithMerge() {
    var cfg = ControlFlowGraph.TryBuild(Body("x% = 0\nIF a% THEN\n  x% = 1\nELSE\n  x% = 2\nEND IF\nPRINT x%"));
    Assert.That(cfg, Is.Not.Null);
    // entry branches; both arms reconverge at one merge block that dominates the PRINT
    Assert.That(cfg!.Entry.Condition, Is.Not.Null);
    var then = cfg.Entry.TrueSucc!;
    var els = cfg.Entry.FalseSucc!;
    Assert.That(then, Is.Not.EqualTo(els));
    var merge = then.TrueSucc!;
    Assert.That(els.TrueSucc, Is.EqualTo(merge), "both arms reconverge at the same merge block");
    Assert.That(merge.Predecessors, Has.Count.EqualTo(2));
  }

  [Test]
  public void Cfg_GivenExitSub_ThenNoFallThrough() {
    var cfg = ControlFlowGraph.TryBuild(Body("x% = 1\nIF a% THEN\n  EXIT SUB\nEND IF\nx% = 2"));
    Assert.That(cfg, Is.Not.Null);
    // the THEN arm flows straight to exit (EXIT SUB), not to the merge
    Assert.That(cfg!.Entry.TrueSucc!.TrueSucc, Is.EqualTo(cfg.Exit));
  }

  [Test]
  public void Cfg_GivenLoop_ThenBailsToNull() {
    Assert.That(ControlFlowGraph.TryBuild(Body("FOR i% = 1 TO 10\n  x% = i%\nNEXT i%")), Is.Null);
    Assert.That(ControlFlowGraph.TryBuild(Body("DO\n  x% = 1\nLOOP")), Is.Null);
  }

  [Test]
  public void Cfg_GivenUnstructuredFlow_ThenBailsToNull() {
    Assert.That(ControlFlowGraph.TryBuild(Body("x% = 1\nGOTO done\ndone:\nx% = 2")), Is.Null);
    Assert.That(ControlFlowGraph.TryBuild(Body("SELECT CASE x%\nCASE 1\n  y% = 1\nEND SELECT")), Is.Null);
  }

  [Test]
  public void Cfg_GivenEveryBlockReachableFromEntry_ThenConnected() {
    var cfg = ControlFlowGraph.TryBuild(Body("IF a% THEN\n  x% = 1\nELSEIF b% THEN\n  x% = 2\nELSE\n  x% = 3\nEND IF"));
    Assert.That(cfg, Is.Not.Null);
    var seen = new HashSet<BasicBlock>();
    var stack = new Stack<BasicBlock>();
    stack.Push(cfg!.Entry);
    while (stack.Count > 0) {
      var b = stack.Pop();
      if (!seen.Add(b))
        continue;
      foreach (var s in b.Successors)
        stack.Push(s);
    }
    Assert.That(seen, Does.Contain(cfg.Exit));
    // entry has no predecessors, exit has no successors
    Assert.That(cfg.Entry.Predecessors, Is.Empty);
    Assert.That(cfg.Exit.Successors, Is.Empty);
  }

  #endregion

  #region dominators & frontiers

  [Test]
  public void Dom_GivenStraightLine_ThenEachBlockDominatedByEntry() {
    var cfg = ControlFlowGraph.TryBuild(Body("x% = 1\ny% = 2"))!;
    var dom = DominatorTree.Build(cfg);
    Assert.That(dom.ImmediateDominatorOf(cfg.Entry), Is.EqualTo(cfg.Entry));
    Assert.That(dom.Dominates(cfg.Entry, cfg.Exit), Is.True);
    Assert.That(dom.ImmediateDominatorOf(cfg.Exit), Is.EqualTo(cfg.Entry));
  }

  [Test]
  public void Dom_GivenIfElseDiamond_ThenMergeImmediatelyDominatedByEntry() {
    var cfg = ControlFlowGraph.TryBuild(Body("x% = 0\nIF a% THEN\n  x% = 1\nELSE\n  x% = 2\nEND IF\nPRINT x%"))!;
    var dom = DominatorTree.Build(cfg);
    var then = cfg.Entry.TrueSucc!;
    var els = cfg.Entry.FalseSucc!;
    var merge = then.TrueSucc!;
    Assert.Multiple(() => {
      Assert.That(dom.ImmediateDominatorOf(then), Is.EqualTo(cfg.Entry));
      Assert.That(dom.ImmediateDominatorOf(els), Is.EqualTo(cfg.Entry));
      // neither arm dominates the merge, so its idom is the entry
      Assert.That(dom.ImmediateDominatorOf(merge), Is.EqualTo(cfg.Entry));
      Assert.That(dom.Dominates(then, merge), Is.False);
      Assert.That(dom.Dominates(cfg.Entry, merge), Is.True);
    });
  }

  [Test]
  public void Dom_GivenIfElseDiamond_ThenArmsHaveMergeInFrontier() {
    var cfg = ControlFlowGraph.TryBuild(Body("IF a% THEN\n  x% = 1\nELSE\n  x% = 2\nEND IF\nPRINT x%"))!;
    var dom = DominatorTree.Build(cfg);
    var then = cfg.Entry.TrueSucc!;
    var els = cfg.Entry.FalseSucc!;
    var merge = then.TrueSucc!;
    Assert.Multiple(() => {
      Assert.That(dom.FrontierOf(then), Does.Contain(merge));
      Assert.That(dom.FrontierOf(els), Does.Contain(merge));
      Assert.That(dom.FrontierOf(cfg.Entry), Is.Empty);
    });
  }

  #endregion

  #region SCCP

  [Test]
  public void Sccp_GivenConstantChain_ThenProvesReadsConstant() {
    var (model, cfg, ssa) = BuildSsa("x% = 5\ny% = x% + 1\nPRINT y%");
    var proven = Sccp.Solve(model, ssa!);
    var yAssign = (AssignStmt)cfg.Entry.Statements[1];
    var xRead = (NameExpr)((BinaryExpr)yAssign.Value).Left;
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var yRead = (NameExpr)print.Items[0].Value!;
    Assert.Multiple(() => {
      Assert.That(proven.TryGetValue(xRead, out var xv) && xv == 5, "x% reads as 5");
      Assert.That(proven.TryGetValue(yRead, out var yv) && yv == 6, "y% = x% + 1 reads as 6");
    });
  }

  [Test]
  public void Sccp_GivenReassignmentOnBothArms_ThenMergeReadNotConstant() {
    // the condition is an opaque function call, so both arms stay live and the
    // merge sees 5 or 6 - not a constant
    var (model, cfg, ssa) = BuildSsa("DECLARE FUNCTION Cond%()\nx% = 5\nIF Cond% THEN\n  x% = 6\nEND IF\nPRINT x%");
    var proven = Sccp.Solve(model, ssa!);
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var xRead = (NameExpr)print.Items[0].Value!;
    Assert.That(proven.ContainsKey(xRead), Is.False, "x% is 5 or 6 at the merge - not a constant");
  }

  [Test]
  public void Sccp_GivenUninitializedConditionVariable_ThenKnownZeroPrunesArm() {
    // a% is never assigned, so PB zero-init makes (IF a%) always false: x% stays 5
    var (model, cfg, ssa) = BuildSsa("x% = 5\nIF a% THEN\n  x% = 6\nEND IF\nPRINT x%");
    var proven = Sccp.Solve(model, ssa!);
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var xRead = (NameExpr)print.Items[0].Value!;
    Assert.That(proven.TryGetValue(xRead, out var xv) && xv == 5, Is.True,
      "an uninitialized condition variable is provably zero, so the THEN is dead");
  }

  [Test]
  public void Sccp_GivenProvablyTrueCondition_ThenDeadArmIgnoredAndMergeIsConstant() {
    // x% = 5 makes (x% > 0) always true, so the ELSE is dead and y% is provably 1
    var (model, cfg, ssa) = BuildSsa("x% = 5\nIF x% > 0 THEN\n  y% = 1\nELSE\n  y% = 2\nEND IF\nPRINT y%");
    var proven = Sccp.Solve(model, ssa!);
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var yRead = (NameExpr)print.Items[0].Value!;
    Assert.That(proven.TryGetValue(yRead, out var yv) && yv == 1, Is.True,
      "the conditional part of SCCP prunes the dead ELSE so y% is constant 1");
  }

  [Test]
  public void Sccp_GivenSelfIncrement_ThenFoldsToWrappedConstant() {
    var (model, cfg, ssa) = BuildSsa("x% = 30000\nx% = x% + 30000\nPRINT x%");
    var proven = Sccp.Solve(model, ssa!);
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var xRead = (NameExpr)print.Items[0].Value!;
    // 60000 wraps to -5536 in INTEGER, matching the runtime store
    Assert.That(proven.TryGetValue(xRead, out var xv) && xv == -5536, Is.True);
  }

  #endregion

  #region SSA construction

  [Test]
  public void Ssa_GivenStraightLineRead_ThenUseResolvesToTheAssignedValue() {
    var (_, cfg, ssa) = BuildSsa("x% = 5\ny% = x% + 1");
    Assert.That(ssa, Is.Not.Null);
    // the read of x% inside y%'s RHS resolves to the Assign whose RHS is 5
    var yAssign = (AssignStmt)cfg.Entry.Statements[1];
    var xRead = (NameExpr)((BinaryExpr)yAssign.Value).Left;
    var version = ssa!.UseVersions[xRead];
    Assert.Multiple(() => {
      Assert.That(version.Kind, Is.EqualTo(SsaDefKind.Assign));
      Assert.That(version.DefExpr, Is.InstanceOf<IntegerLiteralExpr>());
      Assert.That(((IntegerLiteralExpr)version.DefExpr!).Value, Is.EqualTo(5));
    });
  }

  [Test]
  public void Ssa_GivenIfElseReassignment_ThenMergeReadResolvesToPhi() {
    var (_, cfg, ssa) = BuildSsa("x% = 0\nIF a% THEN\n  x% = 1\nELSE\n  x% = 2\nEND IF\nPRINT x%");
    Assert.That(ssa, Is.Not.Null);
    // the merge block holds PRINT x%; that read must resolve to a phi with two inputs
    var print = cfg.Blocks.SelectMany(b => b.Statements).OfType<PrintStmt>().Single();
    var xRead = (NameExpr)print.Items[0].Value!;
    var version = ssa!.UseVersions[xRead];
    Assert.Multiple(() => {
      Assert.That(version.Kind, Is.EqualTo(SsaDefKind.Phi));
      Assert.That(version.PhiInputs, Has.Count.EqualTo(2));
    });
  }

  [Test]
  public void Ssa_GivenVariablePassedToCall_ThenNotTracked() {
    var (_, _, ssa) = BuildSsa("DECLARE SUB Foo(a%)\nx% = 5\nCALL Foo(x%)\ny% = 9");
    // x% escapes (BYREF), so it is never versioned; y% (only assigned) still is
    Assert.That(ssa, Is.Not.Null);
    Assert.That(ssa!.Values.Any(v => v.Variable.Name.Equals("x", System.StringComparison.OrdinalIgnoreCase)), Is.False,
      "a BYREF-passed variable must not be SSA-tracked");
    Assert.That(ssa.Values.Any(v => v.Variable.Name.Equals("y", System.StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test]
  public void Ssa_GivenChainedAssignments_ThenLatestVersionReaches() {
    var (_, cfg, ssa) = BuildSsa("x% = 1\nx% = 2\ny% = x%");
    Assert.That(ssa, Is.Not.Null);
    var yAssign = (AssignStmt)cfg.Entry.Statements[2];
    var xRead = (NameExpr)yAssign.Value;
    var version = ssa!.UseVersions[xRead];
    Assert.That(((IntegerLiteralExpr)version.DefExpr!).Value, Is.EqualTo(2), "the read sees the most recent assignment");
  }

  #endregion
}
