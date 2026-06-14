using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>Lowering of the END statement (program termination).</summary>
[TestFixture]
public sealed class EndStmtLoweringTests {

  private static IrFunction? LowerMain(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35));
  }

  [Test]
  public void Lower_ProgramEndingInEnd_LowersAndReturns() {
    var fn = LowerMain("x% = 1\ny% = x% + 1\nEND");

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    Assert.That(fn!.Entry!.Terminator, Is.InstanceOf<IrRet>());
  }

  [Test]
  public void Lower_EndMidProgram_MakesFollowingCodeUnreachableAndDropped() {
    var fn = LowerMain("x% = 1\nEND\ny% = 2");   // y% = 2 is after END -> dead

    Assert.That(fn, Is.Not.Null);
    Assert.That(IrVerifier.Verify(fn!), Is.Empty);
    // exactly one terminator (the END's ret); the trailing assignment was not emitted
    Assert.That(fn!.AllInstructions.OfType<IrRet>().Count(), Is.EqualTo(1));
  }
}
