using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The 16-bit LINE style rotates on *every visited pixel*; gaps do not
/// advance the VGA byte pointer or erase prior colors. Optimized byte masks
/// must match the original Bresenham/BIOS path in both drawing directions.
/// </summary>
[TestFixture]
public sealed class PlanarPatternedLineTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "STYLE.BAS", Dialect.Pb36),
      "STYLE.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var executable = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(executable);
  }

  [TestCase("&HAAAA", 0, 31, "$CPU 8086")]
  [TestCase("&H5555", 0, 31, "$CPU 8086")]
  [TestCase("&HF0F0", 3, 27, "$CPU 8086")]
  [TestCase("&H8000", 7, 16, "$CPU 8086")]
  [TestCase("&H0000", 0, 19, "$CPU 8086")]
  [TestCase("&HFFFF", 0, 39, "$CPU 8086")]
  [TestCase("&HAAAA", 31, 0, "$CPU 8086")]
  [TestCase("&H5555", 27, 3, "$CPU 8086")]
  [TestCase("&HF0F0", 31, 0, "$CPU 8086")]
  [TestCase("&H8000", 19, 7, "$CPU 8086")]
  [TestCase("&HAAAA", 4, 37, "$CPU 80386")]
  [TestCase("&H5555", 37, 4, "$CPU 80386")]
  public void Execute_GivenPatternedMode12Line_ThenMatchesUnoptimizedPixelOrder(
      string style, int from, int to, string cpu) {
    const int y = 17;
    var probes = string.Join(';', Enumerable.Range(0, 41).Select(x => $"POINT({x},{y})"));
    var body = $"""
      LINE (0,{y})-(40,{y}),9,BF
      LINE ({from},{y})-({to},{y}),6,,{style}
      PRINT {probes}
      """;
    var optimized = Execute(body, true, cpu);
    var baseline = Execute(body, false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output),
        "the original 16-bit style rotation, drawing direction and preserved gaps are observable");
      Assert.That(optimized.PortWrites.Count(write => write.Port is 0x3CE or 0x3CF),
        Is.GreaterThan(0), "the optimized path must issue a VGA byte-mask transaction");
      Assert.That(baseline.PortWrites.Any(write => write.Port is 0x3CE or 0x3CF),
        Is.False, "the baseline must continue through the BIOS pixel path");
    });
  }

  [Test]
  public void Execute_GivenPatternedLineCrossingByteAndStyleBoundaries_ThenKeepsNeighbors() {
    const string body = """
      LINE (0,25)-(63,25),10,BF
      LINE (3,25)-(44,25),5,,&HF0F0
      PRINT POINT(0,25);POINT(2,25);POINT(3,25);POINT(7,25);
      PRINT POINT(8,25);POINT(15,25);POINT(16,25);POINT(31,25);
      PRINT POINT(32,25);POINT(44,25);POINT(45,25);POINT(63,25)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
  }

  [Test]
  public void Execute_GivenRelativePatternedLine_ThenLeavesGraphicCursorAtEndpoint() {
    const string body = """
      LINE (3,30)-(17,30),4,,&HAAAA
      LINE -(31,30),7,,&H5555
      PRINT POINT(3,30);POINT(17,30);POINT(18,30);POINT(30,30);POINT(31,30)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
  }

  [Test]
  public void Execute_GivenPatternedBox_ThenEachEdgePreservesTheStylePhase() {
    const string body = """
      LINE (4,4)-(23,13),6,B,&HAAAA
      PRINT POINT(4,4);POINT(5,4);POINT(22,4);POINT(23,4);
      PRINT POINT(4,8);POINT(23,8);POINT(4,13);POINT(23,13)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenLongStyledLine_ThenUsesFewerCpuDispatchesThanBiosPixels() {
    const string body = """
      LINE (4,210)-(635,210),14,,&HF0F0
      PRINT POINT(4,210);POINT(16,210);POINT(634,210)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions),
        "counts emulator dispatches, not real ISA cycles or bus wait states");
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF),
        Is.LessThan(80), "VGA programming must be per primitive rather than per pixel");
    });
  }
}
