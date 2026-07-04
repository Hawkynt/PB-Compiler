using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.CodeGen;

/// <summary>
/// R1 fast video PRINT ($OPTION VIDEO): console PRINT writes glyphs straight into B800 text
/// memory. Verified through the screen-capture oracle - a compiled BASIC helper PEEKs the
/// text screen into SCREEN.TXT after the program ran unredirected, so the OBSERVABLE screen
/// is compared between the direct-video and the plain DOS/BIOS build.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FastVideoTests {

  private static byte[] Compile(string source, Dialect dialect = Dialect.Pb36) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "t.bas", dialect), "t.bas", dialect);
    var model = Binder.Bind(unit, dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model);
    var exe = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return exe;
  }

  /// <summary>The capture helper: PEEKs the 80x25 text screen into SCREEN.TXT (rows right-trimmed).</summary>
  private static readonly byte[] _capture = Compile("""
    DEF SEG = &HB800
    OPEN "SCREEN.TXT" FOR OUTPUT AS #1
    DIM r AS INTEGER, c AS INTEGER, s AS STRING
    FOR r = 0 TO 24
      s = ""
      FOR c = 0 TO 79
        s = s + CHR$(PEEK((r * 80 + c) * 2))
      NEXT
      PRINT #1, RTRIM$(s)
    NEXT
    CLOSE #1
    """, Dialect.Pb35);

  private const string _SUBJECT = """
    PRINT "HELLO DIRECT VIDEO"
    PRINT "LINE TWO -"; 42
    PRINT "third line just text"
    """;

  [Test]
  public void Capture_GivenPlainBuild_WhenRun_ThenScreenHoldsThePrintedLines() {
    var screen = DosBoxRunner.RunWithScreenCapture(Compile(_SUBJECT), _capture);
    Assert.That(screen, Does.Contain("HELLO DIRECT VIDEO").And.Contain("LINE TWO - 42").And.Contain("third line just text"),
      "the oracle sees what the program printed");
  }

  [Test]
  public void Capture_GivenOptionVideoBuild_WhenRun_ThenScreenIdenticalToDosPath() {
    var plain = DosBoxRunner.RunWithScreenCapture(Compile(_SUBJECT), _capture);
    var direct = DosBoxRunner.RunWithScreenCapture(Compile("$OPTION VIDEO\n" + _SUBJECT), _capture);
    Assert.Multiple(() => {
      Assert.That(direct, Does.Contain("HELLO DIRECT VIDEO"), "the fast path put the glyphs on screen");
      Assert.That(direct, Is.EqualTo(plain), "direct-video output is screen-identical to the DOS/BIOS path");
    });
  }

  [Test]
  public void Execute_GivenScreen13PixelPrimitives_WhenRun_ThenPointReadsBackPsetColors() {
    // R2: PSET/POINT are direct A000 stores/loads (mode 13h linear addressing) - no BIOS
    // per-pixel path exists at all, so 'fast graphics' holds by construction; verified by
    // writing pixels and reading them back through POINT into the file oracle
    const string subject = """
      OPEN "R.TXT" FOR OUTPUT AS #1
      SCREEN 13
      PSET (10, 20), 7
      PSET (319, 199), 42
      PSET (0, 0), 1
      PRESET (10, 20)
      PRINT #1, POINT(319, 199); POINT(0, 0); POINT(10, 20); POINT(5, 5)
      SCREEN 0
      CLOSE #1
      """;
    var (_, files) = DosBoxRunner.RunWithFiles(Compile(subject), ["R.TXT"]);
    Assert.That(files.TryGetValue("R.TXT", out var r) ? r.Trim() : "<missing>", Is.EqualTo("42  1  0  0"),
      "corner and origin pixels hold their colors; PRESET erased; untouched pixel is 0");
  }

  [Test]
  public void Capture_GivenControlCharsAndLongLines_WhenOptionVideo_ThenDosFallbackKeepsScreenIdentical() {
    // TAB (control char) and a >80-column line take the DOS fallback inside the fast build;
    // the screens must still match the plain build exactly
    const string subject = """
      PRINT "A"; CHR$(9); "B"
      PRINT STRING$(100, "x")
      PRINT "tail"
      """;
    var plain = DosBoxRunner.RunWithScreenCapture(Compile(subject), _capture);
    var direct = DosBoxRunner.RunWithScreenCapture(Compile("$OPTION VIDEO\n" + subject), _capture);
    Assert.That(direct, Is.EqualTo(plain));
  }
}
