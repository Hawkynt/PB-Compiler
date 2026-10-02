using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The string intrinsics written WITHOUT parentheses.
///
/// <para>
/// <c>DATE$</c>, <c>TIME$</c>, <c>INKEY$</c>, <c>COMMAND$</c> and <c>CURDIR$</c> each read the MACHINE
/// rather than an argument - the clock, the keyboard buffer, the PSP's command tail, the current
/// directory - so none of them takes one, and the binder does not turn a bare name into a call. They
/// arrived in the lowering as ordinary names bound to nothing and declined as "unsupported string
/// expression: NameExpr", which is the same route the numeric nullary intrinsics already had an
/// answer for.
/// </para>
/// <para>
/// What the assertions can promise is a LENGTH, because the values themselves are the machine's.
/// <c>DATE$</c> is <c>MM-DD-YYYY</c> and <c>TIME$</c> is <c>HH:MM:SS</c>, both fixed width; nothing
/// has been typed, so <c>INKEY$</c> is empty; and the two that read DOS answer what the interpreter's
/// DOS holds - an empty command tail and <c>C:\PBC</c>.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendNullaryStringTests {

  private const string _source = """
    DIM s AS STRING
    s = DATE$
    PRINT LEN(s)
    s = TIME$
    PRINT LEN(s)
    s = INKEY$
    PRINT LEN(s)
    s = COMMAND$
    PRINT LEN(s)
    s = CURDIR$
    PRINT LEN(s)
    """;

  private static (string Output, bool Routed) Run() {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"),
      generator.BackendRoutedNames.Contains("main", StringComparer.OrdinalIgnoreCase));
  }

  [Test]
  public void Execute_GivenTheParenthesisLessStringIntrinsics_WhenRouted_ThenEachHasTheLengthTheMachineGives() {
    var (routed, tookIt) = Run();

    Assert.That(tookIt, Is.True, "a body naming one of these must route now");
    Assert.That(routed, Is.EqualTo("10 | 8 | 0 | 0 | 6"));
  }

  /// <summary>
  /// The two fixed-width ones, pinned each to its own number - a lowering that answered with an empty
  /// handle would fail here whatever the machine's clock says.
  /// </summary>
  [Test]
  public void Execute_GivenDateAndTime_WhenRouted_ThenTheyAreTheirFixedWidths() {
    var (routed, _) = Run();
    var lengths = routed.Split('|');

    Assert.That(lengths[0].Trim(), Is.EqualTo("10"), "DATE$ is MM-DD-YYYY");
    Assert.That(lengths[1].Trim(), Is.EqualTo("8"), "TIME$ is HH:MM:SS");
    Assert.That(lengths[2].Trim(), Is.EqualTo("0"), "nothing has been typed, so INKEY$ is empty");
  }

  /// <summary>
  /// The long tail of small intrinsics that had no lowering: the based logarithms and powers, the two
  /// device-error stubs, <c>ERRCLEAR</c>, <c>SETMEM</c>, the two 32-bit pointer spellings and
  /// <c>PEEK$</c>. Each is one construct, and each was one row in the coverage census
  /// (<c>Intrinsics_GivenEveryCatalogEntry</c>) reported as binding but generating nothing.
  ///
  /// <para>
  /// ERDEV, ERDEV$ and SETMEM are deliberately answers rather than implementations - there is no
  /// device-error reporting and no resizable string heap, so they answer zero, an empty string and a
  /// large stable figure. Answering explicitly is what routing being mandatory turns from a pointless
  /// stub into the only way the program compiles.
  /// </para>
  /// </summary>
  [Test]
  public void Execute_GivenTheSmallIntrinsics_WhenRouted_ThenEachAnswersItsDocumentedValue() {
    const string source = """
      DIM v AS INTEGER, s AS STRING, ok AS LONG
      v = 7
      s = "abcd"
      PRINT LOG2(8!); LOG10(1000!)
      PRINT EXP2(3!); EXP10(2!)
      PRINT ERDEV; LEN(ERDEV$); ERRCLEAR
      PRINT SETMEM(100)
      ok = VARPTR32(v) AND &HFFFF&
      PRINT ok = VARPTR(v)
      ok = STRPTR32(s) AND &HFFFF&
      PRINT ok = STRPTR(s)
      DEF SEG = VARSEG(v)
      s = PEEK$(VARPTR(v), 2)
      PRINT s = CHR$(7) + CHR$(0)
      PRINT INSTAT; FRE; FRE(0)
      OPEN "IN.TXT" FOR OUTPUT AS #1
      PRINT #1, "hello"
      CLOSE #1
      OPEN "IN.TXT" FOR INPUT AS #1
      s = INPUT$(5, #1)
      CLOSE #1
      PRINT s
      """;

    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var routedGen = new CodeGenerator(model) { Optimize = false};
    var routedImage = routedGen.EmitExecutable();
    Assert.That(routedGen.Errors, Is.Empty, string.Join("; ", routedGen.Errors));
    Assert.That(routedGen.BackendRoutedNames, Does.Contain("main"), "the body must route");

    var routed = Cpu8086.Run(routedImage).Output.Trim().Replace("\r\n", "|");
    Assert.Multiple(() => {
      Assert.That(routed, Does.StartWith("3  3 "), "log2 8 and log10 1000");
      Assert.That(routed, Does.Contain(" 8  100 "), "2^3 and 10^2");
      Assert.That(routed, Does.Contain(" 0  0  0 "), "ERDEV, ERDEV$ and a cleared error are all nothing");
      // the ADDRESS itself is the frame layout's business, so what is asserted is the RELATIONSHIP:
      // the low half of the 32-bit spelling is the 16-bit one. A LONG holds it because a frame offset near the top of the segment does not fit
      // an INTEGER, and truncating it compared -6 against 65530.
      Assert.That(routed, Does.Contain("-1 |-1 |-1 |"), "each 32-bit pointer's low half is the 16-bit one, and PEEK$ reads v");
      Assert.That(routed, Does.Contain(" 0  32767  32767 "), "nothing is typed, and free memory is the advisory figure");
      Assert.That(routed, Does.EndWith("hello"), "INPUT$ read five characters back out of the file");
    });
  }

  /// <summary>
  /// <c>USING$</c> whose FORMAT is not a literal. The literal form is read at compile time into fields
  /// and emitted through capture mode; a runtime one has nothing to read, so the runtime parses it
  /// itself - for a single numeric field, which is the whole of what is offered. Every other shape
  /// declines.
  /// </summary>
  [Test]
  public void Execute_GivenARuntimeUsingFormat_WhenRouted_ThenItRendersWhatTheLiteralFormatDoes() {
    const string source = """
      DIM f AS STRING
      f = "##.##"
      PRINT USING$(f, 3.14159)
      PRINT USING$("##.##", 3.14159)
      f = "#####"
      PRINT USING$(f, 42)
      """;

    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var routedGen = new CodeGenerator(model) { Optimize = false};
    var routedImage = routedGen.EmitExecutable();
    Assert.That(routedGen.Errors, Is.Empty, string.Join("; ", routedGen.Errors));
    Assert.That(routedGen.BackendRoutedNames, Does.Contain("main"), "the body must route");

    var routed = Cpu8086.Run(routedImage).Output.Trim().Replace("\r\n", "|");
    Assert.Multiple(() => {
      Assert.That(routed, Does.StartWith("3.14| 3.14"), "the runtime format renders what the literal one does");
      Assert.That(routed, Is.EqualTo("3.14| 3.14|   42"), "and the second field pads 42 to five columns");
    });
  }
}
