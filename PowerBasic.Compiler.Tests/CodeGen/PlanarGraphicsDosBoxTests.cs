using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// Independent SCREEN-12 framebuffer differential. The drawing executable CHAINs directly to a
/// second unoptimized executable via DOS EXEC, without returning to DOSBox's command shell (which
/// may restore text mode and clear the graphics framebuffer). The capture has no SCREEN statement
/// and checks BIOS mode before reading the subject's framebuffer.
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

    ' Force the odd-width/unaligned four-plane GET and PUT fallbacks through
    ' multiple rows; the independent framebuffer sampler probes both regions.
    LINE (321,160)-(331,168),9,BF
    LINE (324,162)-(328,166),14,BF
    GET (321,160)-(331,168),s%(0)
    LINE (440,160)-(450,168),1,BF
    PUT (440,160),s%(0),PSET
    CHAIN "T1.EXE"
    """;

  private static readonly byte[] _capture = Compile("""
    OPEN "R.TXT" FOR OUTPUT AS #1
    DEF SEG = &H40
    PRINT #1, PEEK(&H49);
    DEF SEG
    PRINT #1, POINT(20,20); POINT(205,80); POINT(250,50); POINT(132,150);
    PRINT #1, POINT(336,28); POINT(416,28); POINT(416,58); POINT(416,88);
    PRINT #1, POINT(416,118); POINT(416,148)
    PRINT #1, POINT(321,160); POINT(325,164); POINT(331,168);
    PRINT #1, POINT(440,160); POINT(444,164); POINT(450,168)
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
    var files = DosBoxRunner.RunUnredirectedSequenceWithFiles(
      [subject, _capture], ["R.TXT"], chainFromFirst: true);
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
      Assert.That(values[0], Is.EqualTo(0x12),
        "DOS EXEC must preserve SCREEN 12; the DOS shell may reset it between separate commands");
      Assert.That(values.Skip(1).Take(10), Is.EqualTo(new[] { 3, 9, 6, 11, 2, 2, 13, 0, 7, 7 }),
        "anchor samples exercise fill, circle, PAINT, DRAW and every PUT verb");
      Assert.That(values.Skip(11).Take(6), Is.EqualTo(new[] { 9, 14, 9, 9, 14, 9 }),
        "independent probes cover odd-width, unaligned, multi-row GET/PUT");
      Assert.That(values.All(v => v is >= 0 and <= 15), Is.True);
    });
  }
}
