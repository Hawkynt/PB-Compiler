using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// A bounded SCREEN 13 PAINT can read packed pixels directly instead of
/// querying BIOS video mode for every fillable seed or neighbor. The image
/// itself is the visited map; no additional 64 KiB bitmap is allocated.
/// </summary>
[TestFixture]
public sealed class PackedPaintScanlineTests {
  private static Cpu8086 Execute(string body, bool optimize) {
    var source = "$CPU 8086\nSCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "PAINT13.BAS", Dialect.Pb36),
      "PAINT13.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase(12, 18, 65, 33)]
  [TestCase(301, 180, 319, 199)]
  [TestCase(0, 0, 22, 15)]
  public void Execute_GivenPackedBorderNearScreenEdges_ThenMatchesOriginalScanlineFill(
      int x0, int y0, int x1, int y1) {
    var body = $"""
      LINE ({x0},{y0})-({x1},{y1}),4,B
      PAINT ({x0 + 1},{y0 + 1}),11,4
      PRINT POINT({x0},{y0});POINT({x0 + 1},{y0 + 1});
      PRINT POINT({x1 - 1},{y1 - 1});POINT({x1},{y1})
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("4  11  11  4"));
    });
  }

  [Test]
  public void Execute_GivenNestedObstacles_ThenPreservesTopologyAndOutsidePixels() {
    const string body = """
      LINE (6,8)-(72,29),5,B
      LINE (24,13)-(54,24),7,B
      PAINT (9,10),12,5
      PRINT POINT(6,8);POINT(9,10);POINT(24,13);
      PRINT POINT(30,19);POINT(54,24);POINT(75,18)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(reference.Output));
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenPackedScanlineRegion_ThenEliminatesPerPixelBiosProbes() {
    const string body = """
      LINE (10,20)-(90,30),2,B
      PAINT (11,21),13,2
      PRINT POINT(11,21);POINT(89,29);POINT(10,20)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(reference.ExecutedInstructions));
    });
    TestContext.Progress.WriteLine(
      $"SCREEN 13 PAINT: optimized {optimized.ExecutedInstructions} dispatches, "
      + $"{optimized.FetchedInstructionBytes} instruction bytes; "
      + $"reference {reference.ExecutedInstructions} dispatches, "
      + $"{reference.FetchedInstructionBytes} instruction bytes");
  }
}
