using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>Planar flood-fill runs must preserve the BIOS topology and the 128-entry span stack.</summary>
[TestFixture]
public sealed class PlanarPaintSpanTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var text = cpu + "\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(text, "PAINT12.BAS", Dialect.Pb36),
      "PAINT12.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("$CPU 8086", 0, 15)]
  [TestCase("$CPU 8086", 5, 14)]
  [TestCase("$CPU 8086", 7, 7)]
  [TestCase("$CPU 80386", 5, 14)]
  public void Execute_GivenPlanarBorder_ThenScanlineFillMatchesReferenceAndPreservesBorder(
      string cpu, int left, int right) {
    var body = $"""
      LINE ({left},8)-({right},12),4,B
      PAINT ({left + 1},9),9,4
      PRINT POINT({left},9);POINT({left + 1},9);POINT({right - 1},11);
      PRINT POINT({right},11);POINT({left + 1},8)
      """;
    var optimized = Execute(body, true, cpu);
    var reference = Execute(body, false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("4  9  9  4  4"));
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5),
        Is.GreaterThan(reference.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5)));
    });
  }

  [Test]
  public void Execute_GivenScreen12SeedBeyondThe320x200Viewport_ThenFillsThereToo() {
    const string body = """
      LINE (325,210)-(351,216),4,B
      PAINT (330,212),11,4
      PRINT POINT(325,210);POINT(326,211);POINT(350,215);
      PRINT POINT(351,216);POINT(324,210);POINT(352,216)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("4  11  11  4  0  0"));
    });
  }

  [Test]
  public void Execute_GivenTwoDisjointRegions_ThenPreservesOtherRegionAndGraphicsCursor() {
    const string body = """
      LINE (4,4)-(23,14),6,B
      LINE (8,7)-(19,11),7,B
      PAINT (6,6),3,6
      PRINT POINT(5,5);POINT(9,8);POINT(8,7);POINT(4,4);POINT(24,15)
      LINE -(26,15),2
      PRINT POINT(25,15);POINT(26,15)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(reference.Output));
  }

  [Test]
  [Category("Performance")]
  public void Measure_GivenLongPlanarFillRun_ThenReducesPerPixelBiosDispatch() {
    const string body = """
      LINE (10,20)-(255,24),2,B
      PAINT (11,21),13,2
      PRINT POINT(11,21);POINT(254,23);POINT(10,20);POINT(256,24)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.ExecutedInstructions, Is.LessThan(reference.ExecutedInstructions));
    });
  }
}
