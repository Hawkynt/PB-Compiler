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
  private static Cpu8086 Trace(string drawing, bool adaptivePrefetch = false) {
    var source = "SCREEN 12\n" + drawing + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "ROW-CACHE.BAS", Dialect.Pb36),
      "ROW-CACHE.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) {
      Optimize = true,
      Container = DosContainer.Exe,
      EnableAdaptivePaintPrefetch = adaptivePrefetch,
    };
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(exe, tracePlanarAccess: true);
  }

  [TestCase("thin", 1259, """
    LINE (10,20)-(35,26),2,B
    PAINT (11,21),13,2
    PRINT POINT(11,21);POINT(34,25)
    """)]
  [TestCase("medium", 17207, """
    LINE (10,20)-(245,32),2,B
    PAINT (11,21),13,2
    PRINT POINT(11,21);POINT(244,31)
    """)]
  [TestCase("wide", 37051, """
    LINE (4,20)-(635,30),2,B
    PAINT (5,21),13,2
    PRINT POINT(5,21);POINT(634,29)
    """)]
  [TestCase("three chambers", 58075, """
    LINE (4,15)-(400,38),2,B
    LINE (35,17)-(45,36),2,B
    LINE (100,17)-(108,36),2,B
    LINE (200,17)-(210,36),2,B
    PAINT (12,19),13,2
    PRINT POINT(12,19);POINT(80,19);POINT(300,19)
    """)]
  public void Measure_GivenAdaptiveSpan_ThenComparePixelsPortsCodeShapeAndScratch(
      string scenario, int previousGcWrites, string drawing) {
    var baseline = Trace(drawing);
    var adaptive = Trace(drawing, adaptivePrefetch: true);
    var baselinePorts = baseline.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF);
    var adaptivePorts = adaptive.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF);
    Assert.Multiple(() => {
      Assert.That(adaptive.Output, Is.EqualTo(baseline.Output),
        "adaptive cache must preserve every observed pixel");
      Assert.That(baselinePorts, Is.EqualTo(previousGcWrites),
        "historical six-byte-cache baseline should be reproducible");
      if (scenario == "thin")
        Assert.That(adaptivePorts, Is.EqualTo(baselinePorts),
          "short spans must keep the six-byte cache without extra VGA setup");
      else
        Assert.That(adaptivePorts, Is.LessThan(baselinePorts),
          "prefetch must amortize VGA controller state transactions");
    });
    TestContext.Progress.WriteLine(
      $"PAINT adaptive [{scenario}]: 6-byte GC writes={baselinePorts}, "
      + $"adaptive GC writes={adaptivePorts}; "
      + $"6-byte instructions={baseline.ExecutedInstructions}, "
      + $"adaptive instructions={adaptive.ExecutedInstructions}; "
      + $"6-byte VRAM host reads={baseline.PlanarHostReads}, "
      + $"adaptive VRAM host reads={adaptive.PlanarHostReads}");
  }

  [TestCase(127, 36)]
  [TestCase(128, 36)]
  [TestCase(511, 36)]
  [TestCase(512, 36)]
  [TestCase(620, 471)]
  public void Execute_GivenAdaptiveBoundaryAndClippedRow_ThenPreservesReferencePixels(int width, int y) {
    const int x0 = 3;
    var x1 = x0 + width - 1;
    var y1 = y + 6;
    var body = $"""
      LINE ({x0},{y})-({x1},{y1}),5,B
      LINE ({x0 + 2},{y + 2})-({Math.Min(x1 - 2, x0 + 20)},{y + 3}),4,B
      PAINT ({x1 - 1},{y + 1}),12,5
      PRINT POINT({x0},{y});POINT({x0 + 1},{y + 1});POINT({x1 - 1},{y + 1});
      PRINT POINT({x1},{y});POINT({x0 + 5},{y + 2});POINT({x1 - 2},{y1 - 1});
      PRINT POINT({x0 - 1},{y});POINT({x1 + 1},{y1})
      """;
    var baseline = Trace(body);
    var adaptive = Trace(body, adaptivePrefetch: true);
    Assert.Multiple(() => {
      Assert.That(adaptive.Output, Is.EqualTo(baseline.Output),
        "conditional 32/80-byte prefetch must preserve the original topology");
      Assert.That(adaptive.VideoBiosCalls, Is.EqualTo(baseline.VideoBiosCalls));
    });
  }

  /// <summary>
  /// Model one physical mask-buffer slot of exactly <paramref name="chunkBytes"/>
  /// bytes, not an impossible collection of cached rows. Any read outside
  /// its single aligned window reloads the window. Any write into that
  /// window invalidates it. Read Mode 1 needs two source VRAM reads per byte
  /// (border and paint color); additional conventional-memory copies,
  /// control-port transactions and host instruction costs are not included.
  /// Thus this is a cache-read feasibility envelope, not a CPU timing result.
  /// </summary>
  private static (int VramReads, int Refills, int Invalidations)
      EstimateSingleBuffer(IReadOnlyList<(ushort Offset, bool IsWrite)> access, int chunkBytes) {
    var cachedStart = -1;
    var cachedEnd = -1;
    var loads = 0;
    var invalidations = 0;
    var reads = 0;
    foreach (var (offset, isWrite) in access) {
      if (isWrite) {
        if (offset >= cachedStart && offset < cachedEnd && cachedStart >= 0) {
          cachedStart = -1;
          cachedEnd = -1;
          ++invalidations;
        }
        continue;
      }
      if (offset >= cachedStart && offset < cachedEnd)
        continue;
      var rowStart = (offset / 80) * 80;
      var start = rowStart + ((offset - rowStart) / chunkBytes) * chunkBytes;
      var end = Math.Min(start + chunkBytes, rowStart + 80);
      cachedStart = start;
      cachedEnd = end;
      ++loads;
      reads += 2 * (end - start);
    }
    return (reads, loads, invalidations);
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
    var actual = cpu.PlanarAccessTrace.Count(x => !x.IsWrite);
    var bytes = cpu.PlanarAccessTrace.Where(x => !x.IsWrite)
      .Select(x => x.Offset).Distinct().Count();
    Assert.Multiple(() => {
      Assert.That(actual, Is.GreaterThan(0));
      Assert.That(actual % 2, Is.Zero,
        "each Read Mode 1 miss compares border and paint colors exactly once");
    });
    TestContext.Progress.WriteLine(
      $"PAINT row-cache trace [{scenario}]: existing-6-byte-cache reads={actual}, "
      + $"VRAM unique byte addresses={bytes}, "
      + $"observed VGA GC writes={cpu.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF)}");
    foreach (var chunkBytes in new[] { 8, 16, 32, 80 }) {
      var estimate = EstimateSingleBuffer(cpu.PlanarAccessTrace, chunkBytes);
      Assert.That(estimate.Refills, Is.GreaterThan(0));
      TestContext.Progress.WriteLine(
        $"PAINT row-cache trace [{scenario}]: single-slot {chunkBytes}-byte buffer: "
        + $"{estimate.VramReads} prefetched VRAM reads, {estimate.Refills} refills, "
        + $"{estimate.Invalidations} invalidations, "
        + $"read ratio={(double)estimate.VramReads / actual:F2}x");
    }
  }
}
