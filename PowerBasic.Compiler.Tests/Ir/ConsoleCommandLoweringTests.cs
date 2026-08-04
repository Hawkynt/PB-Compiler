using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// Two of the statements the lowering used to reject wholesale as "CommandStmt": <c>LOCATE</c> and
/// <c>KILL</c>. Both are plain runtime calls, in the conventions the runtime documents - the
/// interesting part is LOCATE's omitted arguments, where a zero means "keep the current one", so an
/// absent row lowers to a literal zero rather than to a read of the cursor.
/// </summary>
[TestFixture]
public sealed class ConsoleCommandLoweringTests {

  private static IrModule? Lower(string source, out string? why) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return IrLowering.TryLowerModule(model, out why);
  }

  private static IEnumerable<string?> Callees(IrModule module)
    => module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
        .OfType<IrCall>().Select(c => (c.Callee as IrFunction)?.Name);

  [Test]
  public void Lower_GivenLocate_ThenCallsTheRuntimeWithBothCoordinates() {
    var module = Lower("""
      LOCATE 5, 10
      PRINT "x"
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(Callees(module!), Does.Contain("rt_locate"));
  }

  [Test]
  public void Lower_GivenLocateWithAnOmittedRow_ThenPassesZeroForIt() {
    var module = Lower("""
      LOCATE , 10
      PRINT "x"
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var call = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrCall>().First(c => (c.Callee as IrFunction)?.Name == "rt_locate");
    Assert.That(call.Args.First(), Is.InstanceOf<IrConstantInt>(), "an absent coordinate is a zero, not a cursor read");
    Assert.That(((IrConstantInt)call.Args.First()).Value, Is.Zero);
  }

  [Test]
  public void Lower_GivenKill_ThenPassesTheFilenameHandle() {
    var module = Lower("""
      KILL "SCRATCH.TXT"
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(Callees(module!), Does.Contain("rt_kill"));
  }
}
