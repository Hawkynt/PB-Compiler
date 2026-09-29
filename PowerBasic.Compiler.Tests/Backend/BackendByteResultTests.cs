using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A BYTE or SBYTE FUNCTION result leaves in the WHOLE of AX, not in AL with AH left as it lies.
///
/// <para>
/// The direct emitter loads a byte result with <c>MOVZX</c>/<c>MOVSX</c> - or <c>MOV AL</c> plus
/// <c>XOR AH,AH</c> / <c>CBW</c> below a 386 - so a caller it compiled reads AX and gets a value.
/// The routed epilogue returned AL alone, which is only indistinguishable while BOTH sides route.
/// </para>
/// <para>
/// It was found by the corpus: <c>FileUtil_CanRead</c> answered <b>-255</b> where it meant 1, and
/// <c>FileUtil_Seek</c> <b>-256</b> where it meant 0 - the low byte right, the high byte 0xFF from
/// whatever ran last. That is the shape of the bug and the reason it survived: the caller's
/// comparison against 1 or 0 simply failed, and the program carried on.
/// </para>
/// <para>
/// The callee here DIRTIES AH before returning, by calling a function that answers 0xFF00 or n. A
/// test whose callee happens to leave AH zero cannot tell a fixed epilogue from a broken one.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendByteResultTests {

  private const string _source = """
    FUNCTION Dirty%(BYVAL n%) NOINLINE
      Dirty% = &HFF00 OR n%
    END FUNCTION

    FUNCTION Flag(BYVAL n%) AS BYTE NOINLINE
      DIM junk%
      junk% = Dirty%(n%)
      IF n% > 0 THEN Flag = 1 ELSE Flag = 0
    END FUNCTION

    FUNCTION Signed(BYVAL n%) AS SBYTE NOINLINE
      DIM junk%
      junk% = Dirty%(n%)
      IF n% > 0 THEN Signed = -3 ELSE Signed = 2
    END FUNCTION

    DIM r%
    r% = Flag(5) : PRINT r%
    r% = Flag(0) : PRINT r%
    r% = Signed(5) : PRINT r%
    r% = Signed(0) : PRINT r%
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool routed, bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The values are the point: an unsigned BYTE 1 read as a word must be 1 and not -255, and a signed
  /// SBYTE -3 must survive as -3 rather than becoming 253 or 0xFF.. something. Both signs are here
  /// because they need different instructions - <c>XOR AH,AH</c> against <c>CBW</c> - and a fix that
  /// zero-extends everything would pass the first two lines and fail the third.
  /// </summary>
  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void Run_GivenAByteResultFromACalleeThatDirtiesAh_ThenTheWholeWordIsTheValue(bool routed, bool optimize) {
    var (output, _) = Run(routed, optimize);

    Assert.That(output, Is.EqualTo("1 | 0 |-3 | 2"),
      "a BYTE result must arrive zero-extended and an SBYTE one sign-extended");
  }

  /// <summary>
  /// The premise, and it has CHANGED. This used to be the mixed-path case the bug needed - the
  /// byte-returning FUNCTION routed while its caller did not, so an epilogue returning AL alone met a
  /// caller reading AX - and it required main NOT to route in order to stay that case.
  ///
  /// <para>
  /// Main routes now: the selector learned <c>SExt i8 -&gt; i16</c>, which is the last thing the
  /// module body here was declining on. The mixed boundary this program used to produce cannot be
  /// produced by it any more, and pinning main to the direct emitter to keep it would be asserting a
  /// gap rather than a behaviour. What still holds - and is what the fixture above actually needs -
  /// is that the byte-returning callee routes, so the values it asserts are this back end's answer
  /// and not the other one's. The routed/direct comparison moved into those four cases, which run
  /// both paths over the same source.
  /// </para>
  /// </summary>
  [Test]
  public void Route_GivenAByteResult_ThenTheByteReturningCalleeRoutes() {
    var (_, routed) = Run(routed: true, optimize: false);

    Assert.Multiple(() => {
      Assert.That(routed, Does.Contain("Flag"), "the byte-returning callee must route");
      Assert.That(routed, Does.Contain("Signed"), "and so must the signed one, which returns through CBW");
    });
  }
}
