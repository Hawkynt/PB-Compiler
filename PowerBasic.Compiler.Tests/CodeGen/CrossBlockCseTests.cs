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

  private static OptCommonSubexpr.Result Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return OptCommonSubexpr.Analyze(model.MainBody, model);
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
  public void Analyze_GivenValueReusedAfterIf_FlowsPastTheMerge() {
    // y%*320+x% computed before the IF and reused AFTER it; the branch writes only b%,
    // so the value survives the merge and the post-IF recomputation reloads the slot
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n b% = 1\nEND IF\nc% = y% * 320 + x%");

    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "the value should flow past the IF merge and reload after it");
    Assert.That(result.Marks.Values.Any(m => m.IsDefine), Is.True);
    Assert.That(result.Marks.Values.Any(m => !m.IsDefine), Is.True);
  }

  [Test]
  public void Analyze_GivenBranchWritesAnInput_DoesNotFlowPastTheMerge() {
    // the cached subtree is y%*320 (input y%); the branch writes y%, so the value is
    // invalidated at the merge and the post-IF recomputation is fresh - no reload
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n y% = 9\nEND IF\nc% = y% * 320 + x%");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenValueReusedInsideForLoop_ReloadsThroughThePreheader() {
    // y%*320+x% computed before the FOR; the body writes only b% (and the counter), so
    // every iteration reloads the pre-loop slot instead of recomputing
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nFOR i% = 1 TO 10\n b% = y% * 320 + x%\nNEXT");
    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "the loop-body recomputation should reload the pre-loop slot");
    Assert.That(result.Marks.Values.Any(m => !m.IsDefine), Is.True);
  }

  [Test]
  public void Analyze_GivenValueReusedAfterLoop_SurvivesTheLoop() {
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nFOR i% = 1 TO 10\n b% = i%\nNEXT\nc% = y% * 320 + x%");
    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "an untouched value survives past the loop");
  }

  [Test]
  public void Analyze_GivenLoopWritesAnInput_DoesNotReuse() {
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nFOR i% = 1 TO 10\n y% = y% + 1\nNEXT\nc% = y% * 320 + x%");
    Assert.That(result.SlotCount, Is.EqualTo(0), "a body write to an input kills the value for the body AND past the loop");
  }

  [Test]
  public void Analyze_GivenCounterIsAnInput_DoesNotReuseInsideLoop() {
    // the cached tree reads i%, which the loop increments - no reuse
    var result = Analyze("i% = 2\nx% = 3\na% = i% * 320 + x%\nFOR i% = 1 TO 10\n b% = i% * 320 + x%\nNEXT");
    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenNestedControlInBranch_DoesNotFlowPastTheMerge() {
    // a nested IF inside the branch could write inputs CollectWrites never sees, so the
    // conservative merge clears the cache; the post-IF recomputation does not reload
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n IF x% > 0 THEN x% = 9\nEND IF\nc% = y% * 320 + x%");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenValueReusedAfterSelect_FlowsPastTheMerge() {
    // a SELECT join behaves like an IF merge: y%*320 (input y%) computed before the SELECT,
    // the arms write only b%, so it survives and the post-SELECT recomputation reloads
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nSELECT CASE x%\nCASE 1\n b% = 1\nCASE ELSE\n b% = 2\nEND SELECT\nc% = y% * 320 + x%");

    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "the value should flow past the SELECT merge and reload after it");
    Assert.That(result.Marks.Values.Any(m => !m.IsDefine), Is.True);
  }

  [Test]
  public void Analyze_GivenSelectArmWritesAnInput_DoesNotFlowPastTheMerge() {
    // an arm overwrites y%, an input of the cached y%*320, so the value is invalidated at
    // the merge and the post-SELECT recomputation is fresh
    var result = Analyze("y% = 2\nx% = 3\na% = y% * 320 + x%\nSELECT CASE x%\nCASE 1\n y% = 9\nCASE ELSE\n b% = 2\nEND SELECT\nc% = y% * 320 + x%");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenBarrierInsideIf_DoesNotElide() {
    // a CALL in the branch is a barrier; the recomputation follows it, so no cross-block reuse
    var result = Analyze("DECLARE SUB noop()\ny% = 2\nx% = 3\na% = y% * 320 + x%\nIF a% > 100 THEN\n CALL noop()\n b% = y% * 320 + x%\nEND IF\n\nSUB noop()\nEND SUB");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }
}
