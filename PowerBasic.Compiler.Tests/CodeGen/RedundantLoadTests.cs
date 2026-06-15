using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// pb36 redundant-load elimination: a repeated array-element read <c>a%(i%)</c> with no
/// intervening write to the array or to an index name reloads the first read's value
/// instead of recomputing the address and re-reading memory. The byte-identical contract
/// is the differential harness; these pin that the reuse fires and that every write that
/// could change the value (the array, an index, a barrier) correctly declines it.
/// </summary>
[TestFixture]
public sealed class RedundantLoadTests {

  private static Pb36CommonSubexpr.Result Analyze(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "TEST.BAS", Dialect.Pb36), "TEST.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return Pb36CommonSubexpr.Analyze(model.MainBody, model);
  }

  [Test]
  public void Analyze_GivenRepeatedArrayRead_ReloadsTheValue() {
    var result = Analyze("DIM a%(10)\ni% = 3\nx% = a%(i%)\ny% = a%(i%)");

    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1), "the second read should reload a slot");
    Assert.That(result.Marks.Values.Any(m => m.IsDefine), Is.True);
    Assert.That(result.Marks.Values.Any(m => !m.IsDefine), Is.True);
  }

  [Test]
  public void Analyze_GivenArrayWrittenBetweenReads_DoesNotReload() {
    // a%(j%) = 5 could alias a%(i%) (j% may equal i%), so the read is invalidated
    var result = Analyze("DIM a%(10)\ni% = 3\nj% = 4\nx% = a%(i%)\na%(j%) = 5\ny% = a%(i%)");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenIndexWrittenBetweenReads_DoesNotReload() {
    var result = Analyze("DIM a%(10)\ni% = 3\nx% = a%(i%)\ni% = 4\ny% = a%(i%)");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }

  [Test]
  public void Analyze_GivenWriteToDifferentArray_KeepsTheReadLive() {
    // a write to b% cannot change a%, so a%(i%) survives and reloads
    var result = Analyze("DIM a%(10)\nDIM b%(10)\ni% = 3\nj% = 4\nx% = a%(i%)\nb%(j%) = 5\ny% = a%(i%)");

    Assert.That(result.SlotCount, Is.GreaterThanOrEqualTo(1));
  }

  [Test]
  public void Analyze_GivenCallBetweenReads_DoesNotReload() {
    // a CALL is a barrier - it could modify the array (e.g. a SHARED array)
    var result = Analyze("DECLARE SUB noop()\nDIM a%(10)\ni% = 3\nx% = a%(i%)\nCALL noop()\ny% = a%(i%)\n\nSUB noop()\nEND SUB");

    Assert.That(result.SlotCount, Is.EqualTo(0));
  }
}
