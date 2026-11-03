using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// The actual VGA four-plane register model and the BIOS pixel model use
/// one underlying screen. Compare compiled direct PSET/POINT against the
/// original INT 10h route, including set/overwrite and indexed port state.
/// </summary>
[TestFixture]
public sealed class PlanarPixelReadWriteTests {
  private static Cpu8086 Execute(string body, bool optimize, string cpu = "") {
    var source = cpu + "\nSCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "PIXEL12.BAS", Dialect.Pb36),
      "PIXEL12.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("$CPU 8086", 0, 0)]
  [TestCase("$CPU 8086", 7, 0)]
  [TestCase("$CPU 8086", 8, 0)]
  [TestCase("$CPU 8086", 9, 1)]
  [TestCase("$CPU 8086", 639, 479)]
  [TestCase("$CPU 80386", 318, 211)]
  public void Execute_GivenAllSixteenColors_ThenMaskedPsetAndPointAgreeWithBios(
      string cpu, int x, int y) {
    var lines = string.Join("\n", Enumerable.Range(0, 16).Select(color =>
      $"PSET ({x},{y}),{color}\nPRINT POINT({x},{y});POINT({x == 639 ? x - 1 : x + 1},{y})"));
    var optimized = Execute(lines, true, cpu);
    var reference = Execute(lines, false, cpu);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.PortWrites.Count(e => e.Port is 0x3CE or 0x3CF or 0x3C4 or 0x3C5),
        Is.GreaterThan(reference.PortWrites.Count(e => e.Port is 0x3CE or 0x3CF or 0x3C4 or 0x3C5)));
    });
  }

  [Test]
  public void Execute_GivenEightAdjacentColoredPixels_ThenEveryOtherPlaneBitIsPreserved() {
    const string body = """
      LINE (0,10)-(15,10),15,BF
      PSET (1,10),1
      PSET (2,10),2
      PSET (3,10),4
      PSET (4,10),8
      PSET (5,10),0
      PSET (6,10),7
      PSET (7,10),14
      PRINT POINT(0,10);POINT(1,10);POINT(2,10);POINT(3,10);
      PRINT POINT(4,10);POINT(5,10);POINT(6,10);POINT(7,10);POINT(8,10)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("15  1  2  4"));
      Assert.That(optimized.Output.Trim(), Does.Contain("8  0  7  14  15"));
    });
  }

  [Test]
  public void Execute_GivenVgaRegisterSettings_ThenThePixelKernelsRestoreTheirValues() {
    const string body = """
      OUT &H3CE, 0: OUT &H3CF, 8
      OUT &H3CE, 3: OUT &H3CF, 0
      OUT &H3CE, 4: OUT &H3CF, 3
      OUT &H3CE, 5: OUT &H3CF, 0
      OUT &H3CE, 8: OUT &H3CF, &HAC
      OUT &H3C4, 2: OUT &H3C5, 5
      PSET (15,17),6
      PRINT POINT(15,17)
      OUT &H3CE, 0: PRINT INP(&H3CF);
      OUT &H3CE, 4: PRINT INP(&H3CF);
      OUT &H3CE, 5: PRINT INP(&H3CF);
      OUT &H3CE, 8: PRINT INP(&H3CF);
      OUT &H3C4, 2: PRINT INP(&H3C5)
      """;
    var optimized = Execute(body, true);
    var reference = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(reference.Output));
      Assert.That(optimized.Output.Trim(), Does.Contain("8  3  0  172  5"));
    });
  }

  [Test]
  public void Execute_GivenXorOrOutOfBoundsPixel_ThenTheBiosPixelContractIsPreserved() {
    const string body = """
      PSET (20,21),4
      PSET (20,21),&H83
      PSET (640,20),8
      PRINT POINT(20,21);POINT(640,20)
      """;
    Assert.That(Execute(body, true).Output, Is.EqualTo(Execute(body, false).Output));
  }

  [TestCase("OUT &H3C4,4: OUT &H3C5,&H0E")]
  [TestCase("OUT &H3CE,6: OUT &H3CF,0")]
  public void Execute_GivenReprogrammedVgaAddressing_ThenPixelFastpathIsNotUsed(string setup) {
    var body = $"""
      {setup}
      PSET (11,12),10
      PRINT POINT(11,12)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
  }
}
