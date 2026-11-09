using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Tests the proposed whole-row cache against measured optimized PAINT VRAM
/// access traces. This is a feasibility study, not a cycle-accurate 8086
/// timing oracle or an implementation of an as-yet-unproved fast path.
/// </summary>
[TestFixture]
public sealed class PlanarPaintRowCacheAnalysisTests {
  private static Cpu8086 Trace(string drawing) {
    var source = "SCREEN 12\n" + drawing + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "ROW-CACHE.BAS", Dialect.Pb36),
      "ROW-CACHE.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = true, Container = DosContainer.Exe };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(exe, tracePlanarAccess: true);
  }

  /// <summary>
  /// The proposed 80-byte row cache contains one union mask byte per eight
  /// pixels. Computing it costs two Read Mode 1 VRAM reads per byte, or 160
  /// reads for the whole 640-pixel row. A write to that row requires
  /// invalidation. This estimator grants the proposed cache a generous
  /// advantage: row fills are charged only when accessed, it ignores the
  /// extra 80-byte writes to conventional memory, and it never charges
  /// setup costs for new GC register values.
  /// </summary>
  private static (int CurrentReads, int RowReads, int RowBuilds, int Invalidations, int DistinctByteReads)
      Evaluate(IReadOnlyList<(ushort Offset, bool IsWrite)> access) {
    var validRows = new HashSet<int>();
    var visited = new HashSet<int>();
    var reads = 0;
    var builds = 0;
    var invalidations = 0;
    foreach (var (offset, isWrite) in access) {
      var row = offset / 80;
      if (isWrite) {
        if (validRows.Remove(row))
          ++invalidations;
        continue;
      }
      ++reads;
      visited.Add(offset);
      if (validRows.Add(row))
        ++builds;
    }
    return (reads, 160 * builds, builds, invalidations, visited.Count);
  }

  [TestCase("thin", """
    LINE (10,20)-(35,26),2,B
    PAINT (11,21),13,2
    PRINT POINT(11,21);POINT(34,25)
    """)]
  [TestCase("medium", """
    LINE (10,20)-(245,32),2,B
    PAINT (11,21),13,2
    PRINT POINT(11,21);POINT(244,31)
    """)]
  [TestCase("wide", """
    LINE (4,20)-(635,30),2,B
    PAINT (5,21),13,2
    PRINT POINT(5,21);POINT(634,29)
    """)]
  [TestCase("three chambers", """
    LINE (4,15)-(400,38),2,B
    LINE (35,17)-(45,36),2,B
    LINE (100,17)-(108,36),2,B
    LINE (200,17)-(210,36),2,B
    PAINT (12,19),13,2
    PRINT POINT(12,19);POINT(80,19);POINT(300,19)
    """)]
  public void Analyze_GivenRealPaintMemoryTrace_ThenReportEightyByteRowCacheCosts(
      string scenario, string drawing) {
    var cpu = Trace(drawing);
    var (actual, rowReads, builds, invalidations, unique) = Evaluate(cpu.PlanarAccessTrace);
    Assert.Multiple(() => {
      Assert.That(actual, Is.GreaterThan(0));
      Assert.That(builds, Is.GreaterThan(0));
      Assert.That(actual % 2, Is.Zero,
        "each Read Mode 1 miss compares border and paint colors exactly once");
      Assert.That(rowReads, Is.GreaterThanOrEqualTo(160));
    });
    TestContext.Progress.WriteLine(
      $"PAINT row-cache trace [{scenario}]: current-mode1-VRAM-reads={actual}; "
      + $"full-80-byte-row-VRAM-reads={rowReads}; row-builds={builds}; "
      + $"row-invalidations={invalidations}; distinct-offsets={unique}; "
      + $"observed-VGA-port-writes={cpu.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF)}; "
      + $"emulated-dispatches={cpu.ExecutedInstructions}");
  }
}
