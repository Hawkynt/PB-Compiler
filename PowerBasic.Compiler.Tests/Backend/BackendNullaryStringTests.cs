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
}
