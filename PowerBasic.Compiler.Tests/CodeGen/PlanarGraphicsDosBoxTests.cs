using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Independent SCREEN-12 framebuffer differential. The drawing executable exits first; a second,
/// unoptimized executable then samples the still-active DOSBox VGA framebuffer. The capture program
/// has no SCREEN statement, so it does not clear the subject's mode or pixels before BIOS POINT reads.
/// </summary>
[TestFixture, Category("Slow")]
[NonParallelizable]
public sealed class PlanarGraphicsDosBoxTests {
  private static byte[] Compile(string source, bool optimize) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36);
    var model = Binder.Bind(unit, Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.Multiple(() => {
      Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
      Assert.That(generator.BackendDeclines, Is.Empty);
    });
    return image;
  }

  private static readonly string _scene = """
    $CPU 8086
    SCREEN 12

    LINE (8,8)-(95,31),3,BF
    LINE (8,40)-(120,80),12,,&HAAAA
    LINE (120,80)-(8,100),5,,&HF0F0
    CIRCLE (180,80),25,9
    LINE (220,20)-(300,100),14,B
    PAINT (250,50),6,14
    DRAW "C11 BM100,150 R32 D16 L20 U8"

    DIM s%(400)
    LINE (320,20)-(383,35),10,BF
    LINE (328,24)-(359,31),2,BF
    GET (320,20)-(383,35),s%(0)

    LINE (400,20)-(463,35),5,BF
    PUT (400,20),s%(0),PSET
    LINE (400,50)-(463,65),5,BF
    PUT (400,50),s%(0),PRESET
    LINE (400,80)-(463,95),5,BF
    PUT (400,80),s%(0),AND
    LINE (400,110)-(463,125),5,BF
    PUT (400,110),s%(0),OR
    LINE (400,140)-(463,155),5,BF
    PUT (400,140),s%(0),XOR
    END
    """;

  private static readonly byte[] _capture = Compile("""
    OPEN "R.TXT" FOR OUTPUT AS #1
    PRINT #1, POINT(20,20); POINT(205,80); POINT(250,50); POINT(132,150);
    PRINT #1, POINT(336,28); POINT(416,28); POINT(416,58); POINT(416,88);
    PRINT #1, POINT(416,118); POINT(416,148)
    FOR y% = 0 TO 188 STEP 4
      FOR x% = 0 TO 500 STEP 4
        PRINT #1, POINT(x%,y%);
      NEXT x%
      PRINT #1,
    NEXT y%
    CLOSE #1
    END
    """, optimize: false);

  private static string Capture(bool optimize) {
    var subject = Compile(_scene, optimize);
    var files = DosBoxRunner.RunUnredirectedSequenceWithFiles([subject, _capture], ["R.TXT"]);
    Assert.That(files.TryGetValue("R.TXT", out var result), Is.True, "capture program did not create R.TXT");
    return DosBoxRunner.Normalize(result!);
  }

  [Test]
  public void Execute_GivenMixedPlanarScene_ThenOptimizedFramebufferMatchesBiosReferenceReadback() {
    var baseline = Capture(optimize: false);
    var optimized = Capture(optimize: true);

    Assert.That(optimized, Is.EqualTo(baseline),
      "optimized Mode-12 primitives must leave the same DOSBox VGA framebuffer as BIOS/per-pixel paths");

    var values = optimized.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
      .Select(int.Parse)
      .ToArray();
    Assert.Multiple(() => {
      Assert.That(values.Length, Is.GreaterThan(5000), "the oracle must sample a broad framebuffer region");
      Assert.That(values.Take(10), Is.EqualTo(new[] { 3, 9, 6, 11, 2, 2, 13, 0, 15, 7 }),
        "anchor samples exercise fill, circle, PAINT, DRAW and every PUT verb");
      Assert.That(values.All(v => v is >= 0 and <= 15), Is.True);
    });
  }
}
