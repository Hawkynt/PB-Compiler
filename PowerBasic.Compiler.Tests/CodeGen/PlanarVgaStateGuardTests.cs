using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// BIOS SCREEN 12 is not proof of an unchanged VGA hardware aperture:
/// an OUT to the sequencer or graphics controller can silently switch
/// address decoding while INT 10h AH=0Fh continues reporting mode 12h.
/// </summary>
[TestFixture]
public sealed class PlanarVgaStateGuardTests {
  private static Cpu8086 Execute(string body, bool optimize) {
    var source = "SCREEN 12\n" + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "VGA.BAS", Dialect.Pb36),
      "VGA.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase("OUT &H3CE,6: OUT &H3CF,&H0D")]
  [TestCase("OUT &H3CE,6: OUT &H3CF,&H00")]
  [TestCase("OUT &H3C4,4: OUT &H3C5,&H0E")]
  [TestCase("OUT &H3C4,4: OUT &H3C5,&H02")]
  [TestCase("OUT &H3CE,5: OUT &H3CF,&H10")]
  public void Execute_GivenChangedVgaAddressing_ThenGraphicsUseBiosFallback(string setup) {
    var body = $"""
      {setup}
      LINE (8,9)-(41,9),6,,&HAAAA
      LINE (8,11)-(41,11),6,BF
      LINE (20,13)-(35,24),9,,&HF0F0
      CIRCLE (60,60),9,5
      PRINT POINT(8,9);POINT(9,9);POINT(8,11);POINT(20,13);
      PRINT POINT(69,60);POINT(60,51)
      """;
    var optimized = Execute(body, optimize: true);
    var baseline = Execute(body, optimize: false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5),
        Is.EqualTo(baseline.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5)),
        "failed hardware certification must never program VGA controller values");
    });
  }

  [Test]
  public void Execute_GivenBiosStandardMapping_ThenPlanarWritesAreEnabled() {
    const string body = """
      LINE (8,9)-(41,9),6,,&HAAAA
      LINE (8,11)-(41,11),6,BF
      CIRCLE (60,60),9,5
      PRINT POINT(8,9);POINT(9,9);POINT(8,11);POINT(69,60)
      """;
    var optimized = Execute(body, true);
    var baseline = Execute(body, false);
    Assert.Multiple(() => {
      Assert.That(optimized.Output, Is.EqualTo(baseline.Output));
      Assert.That(optimized.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5),
        Is.GreaterThan(baseline.PortWrites.Count(p => p.Port is 0x3CF or 0x3C5)));
    });
  }
}
