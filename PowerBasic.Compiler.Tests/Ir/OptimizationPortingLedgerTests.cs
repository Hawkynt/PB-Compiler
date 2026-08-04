using System.Text;
using System.Text.RegularExpressions;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// How much of the optimization catalogue can move to the IR, and how much of it already has.
///
/// "Port the optimizations to the IR" needs a denominator before it means anything, and 421 - the
/// number of files in <c>docs/optimizations/</c> - is the wrong one. Each of those documents carries
/// a <b>Stage</b> field, and most of them say Emitter, Assembler, Register allocation, Layout,
/// Linker or Scheduler. Those are not IR work by any reading: they are decisions about which
/// instruction, which register, which encoding, which address. The retargetable path needs its own
/// versions of them in its own back end; it cannot inherit them.
///
/// What CAN move is the mid-end, whole-program and analysis work. This fixture reads the Stage of
/// every document and reports that split, so the porting effort has a real target and the progress
/// against it cannot drift silently.
/// </summary>
[TestFixture]
public sealed class OptimizationPortingLedgerTests {

  private static readonly string _docs = Path.GetFullPath(Path.Combine(
    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "docs", "optimizations"));

  /// <summary>Stages that name a machine-level decision - instruction, register, encoding, address.</summary>
  private static readonly Regex _machineLevel = new(
    "emitter|assembler|register alloc|layout|link|schedul|runtime|peephole|encoding",
    RegexOptions.IgnoreCase);

  /// <summary>Stages already expressed on the IR.</summary>
  private static readonly Regex _alreadyIr = new("^(IR|SSA)", RegexOptions.IgnoreCase);

  /// <summary>
  /// Documents carrying an <c>**IR**</c> row - an optimization actually ported, as opposed to one
  /// whose original Stage happened to be a mid-end. This is the ratchet: the Stage field describes
  /// where an optimization was FIRST written, and never changes; the IR row records where it now
  /// also lives, and only ever grows.
  /// </summary>
  private static IEnumerable<string> Ported() =>
    Directory.EnumerateFiles(_docs, "*.md")
      .Where(f => File.ReadLines(f).Any(l => Regex.IsMatch(l, @"^\|\s*\*\*IR\*\*\s*\|")))
      .Select(Path.GetFileNameWithoutExtension)
      .OrderBy(n => n, StringComparer.Ordinal)!;

  private static IEnumerable<(string Name, string Stage)> Stages() {
    foreach (var file in Directory.EnumerateFiles(_docs, "*.md").OrderBy(f => f, StringComparer.Ordinal)) {
      var stage = File.ReadLines(file)
        .Select(l => Regex.Match(l, @"^\|\s*\*\*Stage\*\*\s*\|\s*(?<s>[^|]+?)\s*\|"))
        .FirstOrDefault(m => m.Success)?.Groups["s"].Value;
      if (stage is { Length: > 0 })
        yield return (Path.GetFileNameWithoutExtension(file), stage);
    }
  }

  [Test]
  public void Catalogue_GivenEveryOptimization_ThenReportsWhatCanMoveToTheIr() {
    Assume.That(Directory.Exists(_docs), "no docs/optimizations present");
    var stages = Stages().ToList();
    Assume.That(stages, Is.Not.Empty);

    var machine = stages.Where(s => _machineLevel.IsMatch(s.Stage)).ToList();
    var portable = stages.Where(s => !_machineLevel.IsMatch(s.Stage)).ToList();
    var alreadyIr = portable.Where(s => _alreadyIr.IsMatch(s.Stage)).ToList();
    var toPort = portable.Except(alreadyIr).ToList();

    var report = new StringBuilder()
      .AppendLine($"documented optimizations with a Stage : {stages.Count}")
      .AppendLine($"  machine-level (not IR work at all)  : {machine.Count}")
      .AppendLine($"  portable to the IR                  : {portable.Count}")
      .AppendLine($"    already expressed on the IR       : {alreadyIr.Count}")
      .AppendLine($"    still to port                     : {toPort.Count}")
      .AppendLine("the remaining portable stages, by kind:");
    foreach (var (stage, count) in toPort.GroupBy(s => s.Stage).Select(g => (g.Key, g.Count()))
               .OrderByDescending(p => p.Item2).ThenBy(p => p.Key, StringComparer.Ordinal))
      report.AppendLine($"  {count,4}  {stage}");
    TestContext.Out.Write(report.ToString());

    var ported = Ported().ToList();
    report.AppendLine($"explicitly ported to the IR (an **IR** row in the doc): {ported.Count}");
    foreach (var name in ported)
      report.AppendLine($"    {name}");

    // Floors, not exact counts. The catalogue grows; what must not happen is the PORTABLE share
    // shrinking because a mid-end optimization was reclassified rather than moved, or the ported
    // count going backwards.
    Assert.That(ported.Count, Is.GreaterThanOrEqualTo(2),
      "fewer optimizations are recorded as ported:" + Environment.NewLine + report);
    Assert.That(portable.Count, Is.GreaterThanOrEqualTo(120), "fewer optimizations look portable than before:\n" + report);
    Assert.That(alreadyIr.Count, Is.GreaterThanOrEqualTo(20), "fewer optimizations are on the IR than before:\n" + report);
  }
}
