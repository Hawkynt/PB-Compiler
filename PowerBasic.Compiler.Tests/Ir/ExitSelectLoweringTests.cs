using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>EXIT SELECT</c> jumps to the end of the SELECT block. The lowering models it with the same exit
/// stack the loops use - but a SELECT is not a loop, so it carries no continue target, and a bare
/// <c>EXIT LOOP</c> written inside one has to step over it and leave the enclosing loop instead.
/// </summary>
[TestFixture]
public sealed class ExitSelectLoweringTests {

  private static IrModule Lower(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var module = IrLowering.TryLowerModule(model, out var why);
    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    return module!;
  }

  [Test]
  public void Lower_GivenExitSelect_ThenBranchesToTheEndOfTheBlock() {
    var module = Lower("""
      DIM n AS INTEGER
      n = 2
      SELECT CASE n
        CASE 1
          PRINT "one"
        CASE 2
          EXIT SELECT
          PRINT "unreachable"
      END SELECT
      PRINT n
      """);

    var targets = module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrBr>().Select(br => br.Target.Label).ToList();
    Assert.That(targets, Has.Some.StartsWith("sel.end"));
  }

  [Test]
  public void Lower_GivenExitLoopInsideASelect_ThenLeavesTheLoopNotTheSelect() {
    var module = Lower("""
      DIM i AS INTEGER
      FOR i = 1 TO 3
        SELECT CASE i
          CASE 2
            EXIT LOOP
        END SELECT
      NEXT i
      PRINT i
      """);

    var blocks = module.Functions.SelectMany(f => f.Blocks).ToList();
    var inSelect = blocks.First(b => b.Label.StartsWith("sel.case", StringComparison.Ordinal));
    Assert.That(((IrBr)inSelect.Terminator!).Target.Label, Does.StartWith("for.exit"),
      "a SELECT sits on the exit stack but is not a loop");
  }
}
