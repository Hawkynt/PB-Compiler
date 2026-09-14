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
/// has been typed, so <c>INKEY$</c> is empty; and the two that read DOS are compared against the
/// direct emitter rather than against a number, which is the honest form of "the two paths agree".
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

  private static (string Output, bool Routed) Run(bool routed) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"),
      generator.BackendRoutedNames.Contains("main", StringComparer.OrdinalIgnoreCase));
  }

  [Test]
  public void Execute_GivenTheParenthesisLessStringIntrinsics_WhenRouted_ThenTheyMatchTheDirectEmitter() {
    var (routed, tookIt) = Run(routed: true);

    Assert.That(tookIt, Is.True, "a body naming one of these must route now");
    Assert.That(routed, Is.EqualTo(Run(routed: false).Output));
  }

  /// <summary>
  /// The two fixed-width ones, pinned to a number as well as to the other path - a lowering that
  /// answered with an empty handle would agree with nothing and still pass a comparison if the direct
  /// emitter were asked the same wrong question.
  /// </summary>
  [Test]
  public void Execute_GivenDateAndTime_WhenRouted_ThenTheyAreTheirFixedWidths() {
    var (routed, _) = Run(routed: true);
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
  /// device-error reporting and no resizable string heap on either path, so the direct emitter answers
  /// zero, an empty string and a large stable figure. Agreeing explicitly is what routing being
  /// mandatory turns from a pointless stub into the only way the program compiles.
  /// </para>
  /// </summary>
  [Test]
  public void Execute_GivenTheSmallIntrinsics_WhenRouted_ThenTheyMatchTheDirectEmitter() {
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
    var routedGen = new CodeGenerator(model) { Optimize = false, UseExperimentalBackend = true };
    var routedImage = routedGen.EmitExecutable();
    Assert.That(routedGen.Errors, Is.Empty, string.Join("; ", routedGen.Errors));
    Assert.That(routedGen.BackendRoutedNames, Does.Contain("main"), "the body must route");

    var directGen = new CodeGenerator(Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36)) {
      Optimize = false,
      UseExperimentalBackend = false,
    };
    var directImage = directGen.EmitExecutable();
    Assert.That(directGen.Errors, Is.Empty, string.Join("; ", directGen.Errors));

    var routed = Cpu8086.Run(routedImage).Output.Trim().Replace("\r\n", "|");
    Assert.Multiple(() => {
      Assert.That(routed, Is.EqualTo(Cpu8086.Run(directImage).Output.Trim().Replace("\r\n", "|")));
      Assert.That(routed, Does.StartWith("3  3 "), "log2 8 and log10 1000");
      Assert.That(routed, Does.Contain(" 8  100 "), "2^3 and 10^2");
      Assert.That(routed, Does.Contain(" 0  0  0 "), "ERDEV, ERDEV$ and a cleared error are all nothing");
      // the ADDRESSES themselves are each emitter's own - the two lay out frames differently - so
      // what is asserted is the RELATIONSHIP, which holds on both: the low half of the 32-bit
      // spelling is the 16-bit one. A LONG holds it because a frame offset near the top of the
      // segment does not fit an INTEGER, and truncating it compared -6 against 65530.
      // the ADDRESS itself is each emitter's own - the two lay out frames differently - so what is
      // asserted is the RELATIONSHIP, which holds on both: the low half of the 32-bit spelling is the
      // 16-bit one. A LONG holds it because a frame offset near the top of the segment does not fit
      // an INTEGER, and truncating it compared -6 against 65530.
      Assert.That(routed, Does.Contain("-1 |-1 |-1 |"), "each 32-bit pointer's low half is the 16-bit one, and PEEK$ reads v");
      Assert.That(routed, Does.Contain(" 0  32767  32767 "), "nothing is typed, and free memory is the advisory figure");
      Assert.That(routed, Does.EndWith("hello"), "INPUT$ read five characters back out of the file");
    });
  }
}
