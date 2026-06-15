using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 cross-block common-subexpression elimination: a value computed before an IF is
/// inherited into the (dominated) branches, so a recomputation inside a branch reloads
/// instead. The byte-identical contract is the differential harness; these pin that the
/// reuse fires across the IF and that a barrier inside a branch still declines.
/// </summary>
[TestFixture]
public sealed class CrossBlockCseTests {

  private static Pb36CommonSubexpr.Result Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return Pb36CommonSubexpr.Analyze(model.MainBody, model);
  }

  [Test]
  public void Analyze_GivenValueReusedInsideIf_ElidesAcrossTheBranch() {
    // y% * 320 + x% (a 16-bit modular tree) computed before the IF and recomputed in the branch
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n b% = y% * 320 + x%\nEND IF");

    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "the cross-block recomputation should reload a CSE slot");
    Assert.That(result.Marks.Values.Any(m => m.IsDefine), Is.True);
    Assert.That(result.Marks.Values.Any(m => !m.IsDefine), Is.True);
  }

  [Test]
  public void Analyze_GivenBarrierInsideIf_DoesNotElide() {
    // a CALL in the branch is a barrier; the recomputation follows it, so no cross-block reuse
    var result = Analyze("DECLARE SUB noop()\ny% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n CALL noop()\n b% = y% * 320 + x%\nEND IF\n\nSUB noop()\nEND SUB");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }
}
