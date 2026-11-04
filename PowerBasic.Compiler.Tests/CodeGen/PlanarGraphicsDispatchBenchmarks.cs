using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Deterministic CPU-dispatch and VGA I/O accounting for graphics kernels.
/// Instruction count is not elapsed time, real 8086/286/386 cycles, or the
/// latency of a VGA host bus. Emulator pixel readback is compared separately.
/// </summary>
[TestFixture]
[Category("Performance")]
public sealed class PlanarGraphicsDispatchBenchmarks {
  private static Cpu8086 Execute(string mode, string body, bool optimize, string cpu) {
    var source = cpu + "\nSCREEN " + mode + "\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "VGA-METRICS.BAS", Dialect.Pb36),
      "VGA-METRICS.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var cg = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var executable = cg.EmitExecutable();
    TestContext.Progress.WriteLine(
      $"{cpu} SCREEN {mode} optimize={optimize}: DOS image bytes={executable.Length}");
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    Assert.That(cg.BackendDeclines, Is.Empty);
    return Cpu8086.Run(executable);
  }

  [TestCase("12", "$CPU 8086", "LINE (8,20)-(119,23),11,BF\nPRINT POINT(8,20);POINT(119,23)")]
  [TestCase("12", "$CPU 80286", "LINE (8,20)-(119,23),11,BF\nPRINT POINT(8,20);POINT(119,23)")]
  [TestCase("12", "$CPU 80386", "LINE (8,20)-(119,23),11,BF\nPRINT POINT(8,20);POINT(119,23)")]
  [TestCase("12", "$CPU 8086", "LINE (8,20)-(119,20),11,,&HF0F0\nPRINT POINT(8,20);POINT(119,20)")]
  [TestCase("12", "$CPU 80386", "LINE (8,20)-(119,20),11,,&HF0F0\nPRINT POINT(8,20);POINT(119,20)")]
  [TestCase("12", "$CPU 8086", "LINE (18,23)-(119,79),11,,&HAAAA\nPRINT POINT(18,23);POINT(119,79)")]
  [TestCase("12", "$CPU 8086", "CIRCLE (80,80),25,11\nPRINT POINT(105,80);POINT(80,55)")]
  [TestCase("12", "$CPU 8086", "LINE (8,20)-(119,23),11,B\nPAINT (9,21),5,11\nPRINT POINT(9,21);POINT(118,22)")]
  [TestCase("13", "$CPU 8086", "LINE (8,20)-(119,20),11,,&HF0F0\nPRINT POINT(8,20);POINT(119,20)")]
  [TestCase("13", "$CPU 80286", "LINE (8,20)-(119,79),11,,&HAAAA\nPRINT POINT(8,20);POINT(119,79)")]
  [TestCase("13", "$CPU 80386", "LINE (8,20)-(119,79),11,,&HAAAA\nPRINT POINT(8,20);POINT(119,79)")]
  public void Measure_GivenReferencePrimitive_ThenReportExactVgaTrafficAndCheckPixels(
      string mode, string cpu, string body) {
    var optimized = Execute(mode, body, optimize: true, cpu);
    var baseline = Execute(mode, body, optimize: false, cpu);
    Assert.That(optimized.Output, Is.EqualTo(baseline.Output));

    static int VgaOut(Cpu8086 cpu) =>
      cpu.PortWrites.Count(p => p.Port is 0x3C4 or 0x3C5 or 0x3CE or 0x3CF);

    TestContext.Progress.WriteLine(
      $"{cpu} SCREEN {mode}: optimized: CPU={optimized.ExecutedInstructions}, " +
      $"VGA OUT={VgaOut(optimized)}, A000 READ={optimized.PlanarHostReads}, " +
      $"A000 WRITE={optimized.PlanarHostWrites}; " +
      $"baseline: CPU={baseline.ExecutedInstructions}, VGA OUT={VgaOut(baseline)}, " +
      $"A000 READ={baseline.PlanarHostReads}, A000 WRITE={baseline.PlanarHostWrites}");
  }

  [Test]
  public void Measure_GivenAlignedSpriteReadAndWrite_ThenReportPlaneMajorBusTraffic() {
    const string body = """
      DIM s%(300)
      LINE (8,20)-(71,21),10,BF
      GET (8,20)-(71,21),s%(0)
      PUT (80,31),s%(0),PSET
      PRINT POINT(80,31);POINT(143,32);POINT(144,32)
      """;
    var fast = Execute("12", body, optimize: true, "$CPU 8086");
    var baseline = Execute("12", body, optimize: false, "$CPU 8086");
    Assert.Multiple(() => {
      Assert.That(fast.Output, Is.EqualTo(baseline.Output));
      Assert.That(fast.PlanarHostWrites, Is.GreaterThan(0));
      Assert.That(fast.PlanarHostReads, Is.GreaterThan(0));
    });
    TestContext.Progress.WriteLine(
      $"Aligned GET/PUT 64x2: fast VRAM reads={fast.PlanarHostReads}, " +
      $"writes={fast.PlanarHostWrites}, instructions={fast.ExecutedInstructions}; " +
      $"reference instructions={baseline.ExecutedInstructions}");
  }
}
