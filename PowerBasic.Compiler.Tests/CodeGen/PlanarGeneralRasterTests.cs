using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Compare the ordered planar byte-mask stream with the pre-existing
/// BIOS pixel path. Emulated POINT observes the same four VGA planes that
/// masked ES:A000 stores modify, so accidentally losing untouched colors
/// or changing drawing direction is visible.
/// </summary>
[TestFixture]
public sealed class PlanarGeneralRasterTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "RASTER.BAS", Dialect.Pb36),
      "RASTER.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase(12, 8, 12, 47, "&HAAAA", "$CPU 8086")]
  [TestCase(12, 47, 12, 8, "&HF0F0", "$CPU 8086")]
  [TestCase(6, 7, 43, 35, "&H5555", "$CPU 8086")]
  [TestCase(43, 35, 6, 7, "&H8001", "$CPU 8086")]
  [TestCase(8, 42, 47, 8, "&HF0F0", "$CPU 80386")]
  [TestCase(47, 8, 8, 42, "&HAAAA", "$CPU 80386")]
  [TestCase(20, 20, 20, 20, "&H8000", "$CPU 8086")]
  [TestCase(0, 0, 35, 35, "&H0000", "$CPU 8086")]
  [TestCase(0, 0, 35, 35, "&HFFFF", "$CPU 8086")]
  public void Execute_GivenVerticalOrDiagonalStyle_ThenMatchesTheBiosPixelStream(
      int x1, int y1, int x2, int y2, string style, string cpu) {
    var probes = string.Join(';', Enumerable.Range(0, 50)
      .Select(i => $"POINT({i},{i})"))
      + ";" + string.Join(';', Enumerable.Range(0, 50)
      .Select(i => $"POINT(12,{i})"));
    var body = $"""
      LINE (0,0)-(49,49),9,BF
      LINE ({x1},{y1})-({x2},{y2}),6,,{style}
      PRINT {probes}
      """;
    var accelerated = Execute(body, true, cpu);
    var reference = Execute(body, false, cpu);
    Assert.Multiple(() => {
      Assert.That(accelerated.Output, Is.EqualTo(reference.Output));
      Assert.That(accelerated.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF),
        Is.GreaterThan(0), "the mode-12 optimized path must use VGA register programming");
      Assert.That(reference.PortWrites.Any(p => p.Port is 0x3CE or 0x3CF),
        Is.False, "reference should retain BIOS pixel services");
    });
  }

  [TestCase("CIRCLE (60,60),15,7")]
  [TestCase("CIRCLE (95,65),18,12")]
  [TestCase("CIRCLE (14,40),20,9")]
  [TestCase("CIRCLE (30,30),0,4")]
  [TestCase("CIRCLE (60,60),20,13,,,0.5")]
  [TestCase("CIRCLE (60,60),20,13,0,1.5707963,0.5")]
  [TestCase("CIRCLE (60,60),20,13,0,6.2831853,0.25")]
  public void Execute_GivenCircleOrEllipse_ThenRetainsPixelGeometryAndBackground(string shape) {
    var points = string.Join(';', Enumerable.Range(0, 45).Select(i => $"POINT({i + 35},60)"))
      + ";" + string.Join(';', Enumerable.Range(0, 45).Select(i => $"POINT(60,{i + 35})"));
    var body = $"""
      LINE (35,35)-(95,95),2,BF
      {shape}
      PRINT {points}
      """;
    var accelerated = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(accelerated.Output, Is.EqualTo(reference.Output));
      Assert.That(accelerated.PortWrites.Count(p => p.Port is 0x3CE or 0x3CF),
        Is.GreaterThan(0));
    });
  }

  [Test]
  public void Execute_GivenMode12DrawCommands_ThenItUsesTheSameRasterAsLine() {
    const string body = """
      PSET (40,40),0
      DRAW "C9 R25D25L15U10"
      PRINT POINT(40,40);POINT(52,40);POINT(65,40);
      PRINT POINT(65,52);POINT(65,65);POINT(52,65);
      PRINT POINT(50,55);POINT(50,45)
      """;
    var accelerated = Execute(body, true);
    var reference = Execute(body, false);
    Assert.That(accelerated.Output, Is.EqualTo(reference.Output));
  }

  [Test]
  public void Execute_GivenBoxOutline_ThenHorizontalAndVerticalEdgesShareTheSamePixelResult() {
    const string body = """
      LINE (20,40)-(60,61),8,B,&HAAAA
      PRINT POINT(20,40);POINT(21,40);POINT(60,40);POINT(60,41);
      PRINT POINT(40,40);POINT(60,61);POINT(20,61);POINT(20,50)
      """;
    var accelerated = Execute(body, true);
    var reference = Execute(body, false);
    Assert.That(accelerated.Output, Is.EqualTo(reference.Output));
  }

  [Test]
  public void Execute_GivenRelativeDiagonalLineAfterCircle_ThenPreservesTheCursor() {
    const string body = """
      CIRCLE (60,60),12,7
      LINE -(80,80),5,,&HF0F0
      PRINT POINT(60,60);POINT(61,61);POINT(66,66);POINT(80,80)
      """;
    var accelerated = Execute(body, true);
    var reference = Execute(body, false);
    Assert.That(accelerated.Output, Is.EqualTo(reference.Output));
  }
}
