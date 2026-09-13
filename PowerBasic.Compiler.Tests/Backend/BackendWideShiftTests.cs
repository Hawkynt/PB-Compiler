using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// A 32-bit shift whose count is not a compile-time number.
///
/// <para>
/// A constant count is written out as that many one-bit steps, which is what a pair shift is on an
/// 8086. A variable one needs a LOOP, and a loop is basic blocks rather than a straight line, so it
/// declined - 37 times over the SVGA corpus, each taking a whole module body to the direct emitter.
/// It is a runtime call now, the same shape as that emitter's own per-bit walk over the word chain.
/// </para>
/// <para>
/// The counts below are chosen to be the ones that break a careless implementation: 0 must leave the
/// value alone (the loop must not run once), 16 crosses the word boundary, 31 is the last bit that
/// survives, and 32 shifts every bit out and must answer 0 rather than the 386's masked count of
/// zero - this is the baseline routine, and it agrees with the direct emitter's loop, not with a
/// 386's SHL.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendWideShiftTests {

  private const string _source = """
    FUNCTION ShlBy&(BYVAL v AS LONG, BYVAL n AS INTEGER) NOINLINE
      DIM r AS LONG
      r = v
      SHIFT LEFT r, n
      ShlBy& = r
    END FUNCTION

    FUNCTION ShrBy&(BYVAL v AS LONG, BYVAL n AS INTEGER) NOINLINE
      DIM r AS LONG
      r = v
      SHIFT RIGHT r, n
      ShrBy& = r
    END FUNCTION

    DIM k AS INTEGER
    k = 0  : PRINT ShlBy&(1, k)
    k = 1  : PRINT ShlBy&(1, k)
    k = 16 : PRINT ShlBy&(1, k)
    k = 30 : PRINT ShlBy&(1, k)
    k = 32 : PRINT ShlBy&(1, k)
    k = 16 : PRINT ShrBy&(&H10000, k)
    k = 32 : PRINT ShrBy&(&H10000, k)
    """;

  private static (string Output, IEnumerable<string> Routed) Run(bool optimize) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(_source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Run_GivenAVariableShiftCount_ThenTheValueIsRight(bool optimize) {
    var (output, _) = Run(optimize);

    Assert.That(output, Is.EqualTo("1 | 2 | 65536 | 1073741824 | 0 | 1 | 0"),
      "a count of 0 must not shift, 32 must shift everything out, and 16 must cross the word boundary");
  }

  /// <summary>
  /// The premise: before the runtime loop these functions declined, and the values above would be
  /// the direct emitter's - correct, and proving nothing about this back end.
  /// </summary>
  [Test]
  public void Route_GivenAVariableShiftCount_ThenTheBackEndTakesTheFunctions() {
    var (_, routed) = Run(optimize: false);

    Assert.Multiple(() => {
      Assert.That(routed, Does.Contain("ShlBy"), "a variable-count SHIFT LEFT must route");
      Assert.That(routed, Does.Contain("ShrBy"), "a variable-count SHIFT RIGHT must route");
    });
  }
}
