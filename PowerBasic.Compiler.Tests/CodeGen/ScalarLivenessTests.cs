using PowerBasic.Compiler.CodeGen.Ssa;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Scalar live-variable analysis (docs/PB36.md O5 prerequisite): the per-variable
/// live ranges, interference graph and call-crossing flags a register allocator
/// needs to keep a hot scalar in SI/DI across an arbitrary region. Analysis only -
/// no codegen effect - so these pin it in isolation.
/// </summary>
[TestFixture]
public sealed class ScalarLivenessTests {

  private static ScalarLiveness Build(string source) => BuildWithCfg(source).Live;

  private static (ScalarLiveness Live, ControlFlowGraph Cfg) BuildWithCfg(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var body = unit.Statements.Where(s => s is not (SubDecl or FunctionDecl or DeclareStmt or DefFnDecl)).ToList();
    var cfg = ControlFlowGraph.TryBuild(body)!;
    Assert.That(cfg, Is.Not.Null, "the snippet must build a CFG");
    return (ScalarLiveness.Compute(cfg, model), cfg);
  }

  private static VariableSymbol Var(ScalarLiveness live, string name) =>
    live.Variables.Single(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

  [Test]
  public void Variables_GivenPlainScalars_ThenAllTracked() {
    var live = Build("a% = 1\nb% = 2\nc% = a% + b%\nPRINT c%");
    Assert.That(live.Variables.Select(v => v.Name.ToLowerInvariant()),
      Is.EquivalentTo(new[] { "a", "b", "c" }));
  }

  [Test]
  public void Variables_GivenByrefCallArg_ThenEscapedNotTracked() {
    // a% is passed to a SUB (BYREF by default) -> its address escapes -> not a residency candidate
    var live = Build("DECLARE SUB s(x%)\na% = 1\nb% = 2\ns a%\nPRINT b%");
    var names = live.Variables.Select(v => v.Name.ToLowerInvariant()).ToList();
    Assert.That(names, Does.Not.Contain("a"), "an escaping BYREF argument is excluded");
    Assert.That(names, Does.Contain("b"), "a plain scalar stays tracked");
  }

  [Test]
  public void Variables_GivenForCounter_ThenCounterExcluded() {
    // the FOR counter is written implicitly by the loop - the general allocator leaves it to the FOR path
    var live = Build("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i%\nNEXT i%\nPRINT s%");
    var names = live.Variables.Select(v => v.Name.ToLowerInvariant()).ToList();
    Assert.That(names, Does.Not.Contain("i"), "the FOR counter is not a general-allocator candidate");
    Assert.That(names, Does.Contain("s"), "the accumulator is tracked");
  }

  [Test]
  public void Interferes_GivenOverlappingRanges_ThenTrue() {
    // both a% and b% are live before the first PRINT (b% is used afterwards), so they overlap
    var live = Build("a% = 1\nb% = 2\nPRINT a%\nPRINT b%");
    Assert.That(live.Interferes(Var(live, "a"), Var(live, "b")), Is.True);
  }

  [Test]
  public void Interferes_GivenDisjointRanges_ThenFalse() {
    // a% dies at its PRINT before b% is even defined - the ranges never overlap
    var live = Build("a% = 1\nPRINT a%\nb% = 2\nPRINT b%");
    Assert.That(live.Interferes(Var(live, "a"), Var(live, "b")), Is.False);
  }

  [Test]
  public void CrossesCall_GivenLiveAcrossCall_ThenTrue() {
    // x% is defined before the call and read after it - its value must survive the call's clobber
    var live = Build("DECLARE SUB s()\nx% = 5\ns\nPRINT x%");
    Assert.That(live.CrossesCall(Var(live, "x")), Is.True);
  }

  [Test]
  public void CrossesCall_GivenDeadBeforeCall_ThenFalse() {
    // y% is consumed before the call, so nothing of it crosses the clobber
    var live = Build("DECLARE SUB s()\ny% = 5\nPRINT y%\ns");
    Assert.That(live.CrossesCall(Var(live, "y")), Is.False);
  }

  [Test]
  public void LiveOut_GivenDefThenLaterUse_ThenLiveAcrossInterveningBlocks() {
    // x% defined in entry, used after an IF: it is live out of the entry block
    var (live, cfg) = BuildWithCfg("x% = 7\nIF a% THEN\n  PRINT 1\nEND IF\nPRINT x%");
    Assert.That(live.LiveOut(cfg.Entry).Select(v => v.Name.ToLowerInvariant()), Does.Contain("x"));
  }
}
