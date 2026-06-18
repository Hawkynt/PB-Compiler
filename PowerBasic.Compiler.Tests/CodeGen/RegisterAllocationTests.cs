using PowerBasic.Compiler.CodeGen.Ssa;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Graph-coloring register allocator over the scalar interference graph
/// (docs/PB36.md O5). Analysis only - it computes a sound variable->register
/// assignment a later emitter increment consumes; nothing here changes codegen.
/// </summary>
[TestFixture]
public sealed class RegisterAllocationTests {

  private static RegisterAllocation Alloc(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    var body = unit.Statements.Where(s => s is not (SubDecl or FunctionDecl or DeclareStmt or DefFnDecl)).ToList();
    var cfg = ControlFlowGraph.TryBuild(body)!;
    Assert.That(cfg, Is.Not.Null);
    return RegisterAllocation.Compute(ScalarLiveness.Compute(cfg, model));
  }

  private static VariableSymbol Var(RegisterAllocation a, string name) =>
    a.Assignment.Keys.Concat(a.Spilled).Single(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

  [Test]
  public void Compute_GivenInterferingPair_ThenDifferentRegisters() {
    // a% and b% are both live before the first PRINT, so they must not share a register
    var a = Alloc("a% = 1\nb% = 2\nPRINT a%\nPRINT b%");
    Assert.That(a.RegisterOf(Var(a, "a")), Is.Not.Null);
    Assert.That(a.RegisterOf(Var(a, "b")), Is.Not.Null);
    Assert.That(a.RegisterOf(Var(a, "a")), Is.Not.EqualTo(a.RegisterOf(Var(a, "b"))));
  }

  [Test]
  public void Compute_GivenDisjointPair_ThenMayShareOneRegister() {
    // a% dies before b% is defined: with only two registers free they can both take SI
    var a = Alloc("a% = 1\nPRINT a%\nb% = 2\nPRINT b%");
    Assert.That(a.RegisterOf(Var(a, "a")), Is.EqualTo(AllocReg.Si));
    Assert.That(a.RegisterOf(Var(a, "b")), Is.EqualTo(AllocReg.Si));
  }

  [Test]
  public void Compute_GivenCallCrossingVariable_ThenNotRegisterResident() {
    // x% is live across a CALL (clobbers all GP registers), so it is never given a register
    var a = Alloc("DECLARE SUB s()\nx% = 5\ns\nPRINT x%");
    Assert.That(a.Assignment.Keys.Any(v => string.Equals(v.Name, "x", StringComparison.OrdinalIgnoreCase)), Is.False);
  }

  [Test]
  public void Compute_GivenThreeMutuallyLiveVariables_ThenOneSpillsWithTwoRegisters() {
    // a%, b%, c% are all live at the final expression: three-way interference, only SI/DI free -> one spills
    var a = Alloc("a% = 1\nb% = 2\nc% = 3\nPRINT a% + b% + c%");
    var colored = new[] { "a", "b", "c" }.Count(n => a.RegisterOf(Var(a, n)) != null);
    Assert.That(colored, Is.EqualTo(2), "two of three mutually-interfering variables get a register");
    Assert.That(a.Spilled, Has.Count.EqualTo(1));
  }

  [Test]
  public void Compute_GivenLongVariable_ThenNotAllocatedToIndexRegister() {
    // a 4-byte LONG does not fit a 16-bit index register on the 8086 - left in memory
    var a = Alloc("n& = 5\nPRINT n&");
    Assert.That(a.Assignment, Is.Empty);
  }
}
