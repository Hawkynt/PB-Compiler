using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Real-mode screen-12 solid fills compared with the existing BIOS pixel loop.
/// These tests read the same four VGA planes that direct ES:A000 stores modify.
/// </summary>
[TestFixture]
public sealed class PlanarMode12FillTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "PLANAR.BAS", Dialect.Pb36),
      "PLANAR.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("", 0, 0, 639, 0, 15)]
  [TestCase("", 0, 0, 63, 47, 2)]
  [TestCase("", 3, 4, 17, 8, 5)]
  [TestCase("", 7, 10, 7, 11, 12)]
  [TestCase("", 8, 7, 15, 7, 1)]
  [TestCase("", 9, 20, 10, 20, 14)]
  [TestCase("", 639, 479, 639, 479, 3)]
  [TestCase("$CPU 80386", 3, 4, 17, 8, 5)]
  public void Execute_GivenInBoundsPlanarFill_ThenMatchesBiosAndBounds(
      string cpu, int x1, int y1, int x2, int y2, int color) {
    var body = $"""
      LINE ({x1},{y1})-({x2},{y2}),{color},BF
      PRINT POINT({x1},{y1});POINT({x2},{y2})
      """;
    var optimized = Execute(body, optimize: true, cpu);
    var faithful = Execute(body, optimize: false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(faithful.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain($"{color}  {color}"));
      Assert.That(optimized.PortWrites.Count(entry => entry.Port is 0x3CE or 0x3CF or 0x3C4 or 0x3C5),
        Is.GreaterThan(0), "optimized planar span must actually use VGA control registers");
      Assert.That(faithful.PortWrites.Count(entry => entry.Port is 0x3CE or 0x3CF or 0x3C4 or 0x3C5),
        Is.Zero, "the reference path must remain BIOS pixel drawing");
    });
  }

  [Test]
  public void Execute_GivenFullScreenPlanarFill_ThenAllScreenCornersAreCovered() {
    // Executing more than 300K individual BIOS pixel interrupts in the test
    // interpreter is costly. Smaller fixtures above compare both paths.
    const string body = """
      LINE (0,0)-(639,479),2,BF
      PRINT POINT(0,0);POINT(639,0);POINT(0,479);POINT(639,479)
      """;
    var optimized = Execute(body, true);
    Assert.That(optimized.Output.Trim(), Does.Contain("2  2  2  2"));
  }

  [TestCase("LINE (2,3)-(10,3),4,BF\nLINE (11,3)-(13,3),5,BF", "4  4  5  5")]
  [TestCase("LINE (31,3)-(8,1),9,BF", "9  9  9  9")]
  [TestCase("LINE (0,0)-(0,0),0,BF", "0  0  0  0")]
  public void Execute_GivenSeveralPlanarRects_ThenMatchesReference(
      string commands, string expected) {
    var body = commands + "\nPRINT POINT(8,3);POINT(10,3);POINT(11,3);POINT(13,3)";
    var optimized = Execute(body, true);
    var faithful = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(faithful.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain(expected));
    });
  }

  [Test]
  public void Execute_GivenMaskedPlanarFill_ThenRetainsExistingNeighborColors() {
    const string body = """
      LINE (0,12)-(31,12),9,BF
      LINE (3,12)-(18,12),5,BF
      PRINT POINT(0,12);POINT(2,12);POINT(3,12);POINT(18,12);POINT(19,12);POINT(31,12)
      """;
    var optimized = Execute(body, true);
    var faithful = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(faithful.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("9  9  5  5  9  9"));
    });
  }

  [Test]
  public void Execute_GivenPreconfiguredVgaRegisters_ThenRestoresExactIndexedState() {
    const string body = """
      OUT &H3CE, 3: OUT &H3CF, 4
      OUT &H3CE, 8: OUT &H3CF, &H3C
      OUT &H3C4, 2: OUT &H3C5, 5
      LINE (3,12)-(18,13),5,BF
      OUT &H3CE, 3: PRINT INP(&H3CF);
      OUT &H3CE, 8: PRINT INP(&H3CF);
      OUT &H3C4, 2: PRINT INP(&H3C5)
      """;
    var optimized = Execute(body, true);
    var faithful = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(faithful.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("4  60  5"));
    });
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenPlanarRectangle_WhenOptimized_ThenRemovesPerPixelBiosDispatch() {
    const string body = """
      LINE (8,16)-(135,47),11,BF
      PRINT POINT(8,16);POINT(135,47);POINT(7,16);POINT(136,47)
      """;
    var optimized = Execute(body, true);
    var faithful = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(faithful.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(faithful.ExecutedInstructions),
        "deterministic CPU instruction dispatch, not a physical VGA bus cycle estimate");
      Assert.That(optimized.PortWrites.Count(e => e.Port is 0x3CE or 0x3CF),
        Is.LessThan(100), "per-pixel register selection would destroy planar batching");
    });
  }
}
