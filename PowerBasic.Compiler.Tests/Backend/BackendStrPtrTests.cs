using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>STRPTR</c> - the offset of a string's CHARACTERS in the heap.
///
/// <para>
/// Its segment is <c>rt_strseg</c>, which <c>STRSEG</c> answers separately; the pair is how a program
/// reaches the bytes without the runtime copying them, and it is what graphics code uses to blit a
/// string it has built. The IR had no name for the offset, so every body using one declined - 19 of
/// them over the SVGA corpus, each taking the whole module body with it.
/// </para>
/// <para>
/// The test reads the characters BACK through the pointer rather than asserting the pointer's value.
/// An address is not a meaningful thing to assert - it moves whenever the heap does - but "the bytes
/// at that address are the string's" is exactly the promise STRPTR makes, and it fails for a
/// pointer that is off by one, points at the handle instead of the data, or names the wrong segment.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendStrPtrTests {

  private const string _source = """
    DIM s AS STRING, p AS WORD, g AS INTEGER
    s = "PB!"
    p = STRPTR(s)
    g = STRSEG(s)
    DEF SEG = g
    PRINT CHR$(PEEK(p)); CHR$(PEEK(p + 1)); CHR$(PEEK(p + 2))
    DEF SEG
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim(), generator.BackendRoutedNames.ToList());
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenStrPtr_ThenTheBytesAtThatAddressAreTheString(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("PB!"),
      "STRPTR must name the characters, not the handle, and STRSEG must name the heap they live in");
  }

  /// <summary>
  /// The premise: before the IR named it, this body declined and the bytes above would have been
  /// read by the direct emitter - correct, and proving nothing about this back end.
  /// </summary>
  [Test]
  public void Route_GivenStrPtr_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"), "a body using STRPTR must route now");
  }
}
