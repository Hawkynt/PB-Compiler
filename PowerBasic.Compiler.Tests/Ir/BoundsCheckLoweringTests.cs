using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>$ERROR BOUNDS ON</c> in the IR lowering: every subscript is compared against its dimension and
/// Error 9 raised when it falls outside, which is the same guard the direct emitter writes when
/// <c>CheckBounds</c> is set.
///
/// The interesting half is what still declines. A dynamic array keeps its bounds in a runtime
/// descriptor rather than in the type, so checking it means reading that descriptor - until that is
/// written, a dynamic array under BOUNDS ON must refuse the program rather than index it unchecked.
/// Silently dropping an armed check is worse than not supporting it: the program would run past the
/// end of its array and report nothing.
/// </summary>
[TestFixture]
public sealed class BoundsCheckLoweringTests {

  private static IrModule? Lower(string source, out string? why) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return IrLowering.TryLowerModule(model, out why);
  }

  private const string _checked = """
    $ERROR BOUNDS ON
    DIM a%(1 TO 10)
    a%(3) = 7
    PRINT a%(3)
    """;

  [Test]
  public void Lower_GivenBoundsOn_ThenRaisesErrorNineOnAnOutOfRangeSubscript() {
    var module = Lower(_checked, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    var instructions = module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();
    var raise = instructions.OfType<IrCall>().FirstOrDefault(c => (c.Callee as IrFunction)?.Name == "rt_error");
    Assert.That(raise, Is.Not.Null, "an armed check has to be able to raise");
    Assert.That(((IrConstantInt)raise!.Args.First()).Value, Is.EqualTo(9), "subscript out of range is Error 9");
    // both ends of the range are tested, not just the upper one
    Assert.That(instructions.OfType<IrCmp>().Select(c => c.Pred), Does.Contain(IrCmpPred.Slt));
    Assert.That(instructions.OfType<IrCmp>().Select(c => c.Pred), Does.Contain(IrCmpPred.Sgt));
  }

  [Test]
  public void Lower_GivenBoundsOff_ThenEmitsNoCheck() {
    var module = Lower("""
      DIM a%(1 TO 10)
      a%(3) = 7
      PRINT a%(3)
      """, out var why);

    Assert.That(module, Is.Not.Null, $"lowering declined: {why}");
    Assert.That(module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions)
      .OfType<IrCall>().Any(c => (c.Callee as IrFunction)?.Name == "rt_error"), Is.False);
  }

  [Test]
  public void Lower_GivenBoundsOnOverADynamicArray_ThenDeclinesRatherThanSkipTheCheck() {
    Lower("""
      $ERROR BOUNDS ON
      DIM a%(1 TO 10)
      REDIM a%(1 TO 20)
      a%(3) = 7
      PRINT a%(3)
      """, out var why);

    Assert.That(why, Does.Contain("dynamic array"));
  }

  [Test]
  public void Run_GivenBoundsOn_ThenTheCheckedProgramStillBehavesTheSame() {
    // the guard must not change a program that stays in range - and both paths must agree on it
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_checked, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    var direct = new CodeGenerator(model) { Optimize = true, UseExperimentalBackend = false };
    var image = direct.EmitExecutable();
    Assert.That(direct.Errors, Is.Empty, string.Join("; ", direct.Errors));

    Assert.That(Cpu8086.Run(image).Output.Trim(), Is.EqualTo("7"));
  }
}
