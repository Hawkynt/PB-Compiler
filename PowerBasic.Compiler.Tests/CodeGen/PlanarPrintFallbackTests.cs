using System.Text;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// A DOS PRINT is handle-based output, including in SCREEN 12/13. The
/// explicit B800h optimization must never write character/attribute pairs
/// into graphics memory's unrelated text aperture. Until a true glyph
/// rasterizer has proven console, font, page and scroll semantics, keep
/// graphics PRINT on the original DOS device/redirectable path.
/// </summary>
[TestFixture]
public sealed class PlanarPrintFallbackTests {
  private static Cpu8086 Execute(string body, bool fastVideo, bool optimize) {
    var source = (fastVideo ? "$OPTION VIDEO\n" : "") + body + "\nEND\n";
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "GFXPRINT.BAS", Dialect.Pb36),
      "GFXPRINT.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    Assert.That(generator.BackendDeclines, Is.Empty);
    return Cpu8086.Run(image);
  }

  [TestCase(12)]
  [TestCase(13)]
  [TestCase(1)]
  public void Execute_GivenGraphicsMode_ThenOptionVideoDoesNotBypassDosPrint(int mode) {
    var body = $"""
      SCREEN {mode}
      PRINT "PLANAR GRAPHICS";
      PRINT " PRINT"; 42
      PRINT "next"; CHR$(9); "tab"
      """;
    var baseline = Execute(body, fastVideo: false, optimize: false);
    var fast = Execute(body, fastVideo: true, optimize: true);
    Assert.Multiple(() => {
      Assert.That(fast.Output, Is.EqualTo(baseline.Output));
      Assert.That(fast.Output, Does.Contain("PLANAR GRAPHICS PRINT 42"));
      Assert.That(fast.MemoryAt(0xB800, 0), Is.EqualTo(baseline.MemoryAt(0xB800, 0)),
        "graphics-mode output must not create an unrelated B800 text cell");
    });
  }

  [Test]
  public void Execute_GivenGraphicsPrintAndAnExplicitFileHandle_ThenBothOutputsArePreserved() {
    const string body = """
      SCREEN 12
      OPEN "CAPTURE.TXT" FOR OUTPUT AS #1
      PRINT "console text";
      PRINT #1, "file text"; 123
      PRINT " continues"
      CLOSE #1
      """;
    var baseline = Execute(body, fastVideo: false, optimize: false);
    var fast = Execute(body, fastVideo: true, optimize: true);
    Assert.Multiple(() => {
      Assert.That(fast.Output, Is.EqualTo(baseline.Output));
      Assert.That(fast.FileBytes("CAPTURE.TXT"), Is.EqualTo(baseline.FileBytes("CAPTURE.TXT")));
      Assert.That(Encoding.ASCII.GetString(fast.FileBytes("CAPTURE.TXT") ?? []),
        Does.Contain("file text 123"));
    });
  }
}
