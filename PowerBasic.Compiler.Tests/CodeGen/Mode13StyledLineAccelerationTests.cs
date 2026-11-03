using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// SCREEN 13 style phase cannot be anchored at a framebuffer byte boundary:
/// its 16-bit ring advances on the *visited pixel*, including skipped pixels,
/// and direction reverses the mapping between ring position and screen x.
/// </summary>
[TestFixture]
public sealed class Mode13StyledLineAccelerationTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nSCREEN 13\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "STYLE13.BAS", Dialect.Pb36),
      "STYLE13.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase(0, 69, "&HAAAA", "$CPU 8086")]
  [TestCase(69, 0, "&HAAAA", "$CPU 8086")]
  [TestCase(5, 62, "&HF0F0", "$CPU 8086")]
  [TestCase(62, 5, "&HF0F0", "$CPU 8086")]
  [TestCase(7, 30, "&H5555", "$CPU 8086")]
  [TestCase(30, 7, "&H8000", "$CPU 80386")]
  [TestCase(0, 69, "&H0000", "$CPU 80386")]
  [TestCase(0, 69, "&HFFFF", "$CPU 80386")]
  [TestCase(5, 5, "&H8000", "$CPU 8086")]
  public void Execute_GivenStyledHorizontalMode13Line_ThenMatchesBresenham(
      int from, int to, string mask, string cpu) {
    var probes = string.Join(';', Enumerable.Range(0, 74).Select(x => $"POINT({x},17)"));
    var body = $"""
      LINE (0,17)-(73,17),11,BF
      LINE ({from},17)-({to},17),6,,{mask}
      PRINT {probes}
      """;
    var accelerated = Execute(body, true, cpu);
    var baseline = Execute(body, false, cpu);
    Assert.That(accelerated.Output, Is.EqualTo(baseline.Output));
  }

  [TestCase("&HF0F0")]
  [TestCase("&HAAAA")]
  [TestCase("&H8000")]
  public void Execute_GivenStyledOutlineAndRelativeLine_ThenPreservesPhaseAndCursor(string mask) {
    var body = $"""
      LINE (11,11)-(37,27),5,B,{mask}
      LINE -(65,27),7,,{mask}
      PRINT POINT(11,11);POINT(12,11);POINT(37,11);POINT(37,27);
      PRINT POINT(38,27);POINT(50,27);POINT(65,27);POINT(11,27)
      """;
    var accelerated = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.That(accelerated.Output, Is.EqualTo(baseline.Output));
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenLongPatternedMode13Line_ThenRemovesBiosModeCheckFromThePixelLoop() {
    const string body = """
      LINE (1,50)-(318,50),13,,&HF0F0
      PRINT POINT(1,50);POINT(16,50);POINT(318,50)
      """;
    var accelerated = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(accelerated.Output, Is.EqualTo(baseline.Output));
      Assert.That(accelerated.ExecutedInstructions, Is.LessThan(baseline.ExecutedInstructions),
        "emulator instruction dispatch, not physical VGA bus-cycle measurement");
    });
  }

  [Test]
  public void Execute_GivenAlternatingHorizontalAndDiagonalStyles_ThenKeepsTheirIndependentRasterOrder() {
    const string body = """
      LINE (12,12)-(49,12),4,,&HAAAA
      LINE (49,12)-(15,43),5,,&HF0F0
      PRINT POINT(12,12);POINT(13,12);POINT(49,12);POINT(49,13);
      PRINT POINT(16,42);POINT(15,43);POINT(50,12)
      """;
    Assert.That(Execute(body, true).Output, Is.EqualTo(Execute(body, false).Output));
  }
}
