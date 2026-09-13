using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A string routine's COUNT given as a LONG.
///
/// <para>
/// These counts are WORDS in the DOS ABI, and the direct emitter simply coerces the argument to
/// INTEGER before the call. The IR declared them i32 - the same declaration feeds the C back end -
/// and the lowering coerced to LONG, which left the selector a 32-bit value it could only take where
/// it could PROVE the range. Where it could not, the whole module body went to the direct emitter:
/// 37 declines over the SVGA corpus, led by <c>rt_str_char_at</c>.
/// </para>
/// <para>
/// The value is narrowed to a word and widened straight back. The coercion is where a LONG that does
/// not fit raises, exactly as on the other path, and the widening is what the argument staging peels
/// off again to reach the word the ABI wants - the same peel that must NOT accept a byte, which is
/// why it checks for a word source rather than for "narrower than 32 bits".
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendStringCountTests {

  private const string _source = """
    DIM n AS LONG, s AS STRING
    n = 3
    s = "abcdef"
    PRINT LEFT$(s, n)
    PRINT RIGHT$(s, n)
    PRINT MID$(s, n, 2)
    PRINT "[" + SPACE$(n) + "]"
    PRINT STRING$(n, "x")
    PRINT MID$(s, 2, n)
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The counts are read as counts, not as something the narrowing mangled. SPACE$ is bracketed so
  /// its width is visible rather than trimmed away, and the last line uses the LONG as a LENGTH
  /// rather than a position, which is the other argument slot.
  /// </summary>
  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenALongCount_ThenTheStringsAreRight(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("abc|def|cd|[   ]|xxx|bcd"));
  }

  /// <summary>
  /// The premise: before the narrowing this body declined for want of a provable range, and the
  /// strings above would have been the direct emitter's.
  /// </summary>
  [Test]
  public void Route_GivenALongCount_ThenTheModuleBodyIsTakenByTheBackEnd() {
    var (_, routed) = Run(optimize: false);

    Assert.That(routed, Does.Contain("main"), "a LONG count must not send the body to the direct emitter");
  }
}
