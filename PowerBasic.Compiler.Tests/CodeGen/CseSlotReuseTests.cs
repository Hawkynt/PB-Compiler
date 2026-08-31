using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

[TestFixture]
public sealed class CseSlotReuseTests {

  private static OptCommonSubexpr.Result Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    return OptCommonSubexpr.Analyze(model.MainBody, model);
  }

  [Test]
  public void Analyze_GivenIndependentRunsSeparatedByCall_WhenPacked_ThenOnePhysicalSlot() {
    var result = Analyze("""
      DECLARE SUB Barrier
      x% = 1
      y% = 2
      a% = x% * 7
      b% = x% * 7
      Barrier
      c% = y% * 9
      d% = y% * 9
      END
      SUB Barrier
      END SUB
      """);

    Assert.Multiple(() => {
      Assert.That(result.Marks, Has.Count.EqualTo(4), "both CSE pairs must still be recognized");
      Assert.That(result.SlotCount, Is.EqualTo(1), "the call kills the first run, so the second pair can reuse its slot");
      Assert.That(result.Marks.Values.Select(m => m.Slot), Is.All.EqualTo(0));
    });
  }

  [Test]
  public void Analyze_GivenDistinctValuesInOneRun_WhenPacked_ThenSlotsDoNotAlias() {
    var result = Analyze("""
      x% = 1
      y% = 2
      a% = x% * 7
      b% = x% * 7
      c% = y% * 9
      d% = y% * 9
      END
      """);

    Assert.Multiple(() => {
      Assert.That(result.Marks, Has.Count.EqualTo(4));
      Assert.That(result.SlotCount, Is.EqualTo(2), "values in the same run may overlap and keep distinct cells");
      Assert.That(result.Marks.Values.Select(m => m.Slot).Distinct().Count(), Is.EqualTo(2));
    });
  }

  [Test]
  public void Analyze_GivenScalarInvalidationInsideRun_WhenPacked_ThenDifferentKeyDoesNotRecycleSlot() {
    var result = Analyze("""
      x% = 1
      y% = 2
      a% = x% * 7
      b% = x% * 7
      x% = x% + 1
      c% = y% * 9
      d% = y% * 9
      END
      """);

    Assert.That(result.SlotCount, Is.EqualTo(2),
      "an incremental invalidation is not a whole-run lifetime proof; only hard barriers recycle cells");
  }

  [Test]
  public void Analyze_GivenBarrierInsideIfArm_WhenPacked_ThenOuterDominatingSlotIsNotRecycledInsideNestedRun() {
    var result = Analyze("""
      DECLARE SUB Barrier
      x% = 1
      y% = 2
      q% = 1
      a% = x% * 7
      IF q% THEN
        b% = x% * 7
        Barrier
        c% = y% * 9
        d% = y% * 9
      ELSE
        e% = x% * 7
      END IF
      END
      SUB Barrier
      END SUB
      """);

    Assert.That(result.SlotCount, Is.EqualTo(2),
      "a nested barrier must not recycle the slot of a dominating value that a sibling arm can still reload");
  }
}
