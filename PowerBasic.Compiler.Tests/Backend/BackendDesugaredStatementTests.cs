using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The pb36 statements the BINDER rewrites into calls: a member call <c>r.Dispose()</c> becomes
/// <c>Res.Dispose(r)</c>, a property set <c>o.P = x</c> becomes <c>Type.set_P(o, x)</c>, and a
/// <c>USING</c> block's <c>END USING</c> is a compiler-inserted member call of the first kind.
///
/// <para>
/// None of that resolution belongs to a back end - it is overload lookup against the receiver's TYPE,
/// done once at bind time and recorded in <c>DesugaredStatements</c>. The direct emitter has read that
/// table from the start. The lowering never did, so every one of these declined, and a lowering that
/// re-derived the rewrite instead would be a second implementation of the overload rules.
/// </para>
/// <para>
/// Each case is executed rather than only compiled, because "the statement lowered" and "the statement
/// ran" are different claims and the interesting one is the second. A <c>USING</c> block whose Dispose
/// was dropped compiles perfectly.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendDesugaredStatementTests {

  private static (string Output, IEnumerable<string> Routed) Run(string source, bool optimize, bool routed) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (Cpu8086.Run(image).Output.Trim().Replace("\r\n", "|"), generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The scope exit is the whole point of the block, so the counter is read INSIDE as well as after:
  /// a build that called Dispose eagerly, or twice, or never, disagrees with the expected pair in a
  /// different place each time, where a single reading after the block could only catch the last.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAUsingBlock_WhenRouted_ThenDisposeRunsAtEndUsing(bool optimize) {
    const string source = """
      DECLARE SUB Work()
      DIM disposed AS SHARED INTEGER
      TYPE Res
        H AS INTEGER
        SUB Dispose()
          disposed = disposed + 1
        END SUB
      END TYPE
      Work
      PRINT "out"; disposed
      END
      SUB Work()
        USING r AS Res
        r.H = 7
        PRINT "in"; r.H; disposed
      END SUB
      """;

    var (output, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("Work"), "the body with the USING block must route now");
    Assert.Multiple(() => {
      Assert.That(output, Is.EqualTo(Run(source, optimize, routed: false).Output));
      Assert.That(output, Is.EqualTo("in 7  0 |out 1"),
        "the field was written, and Dispose ran once and only on the way out");
    });
  }

  /// <summary>
  /// A member call written by the programmer rather than inserted by END USING, and one that takes an
  /// argument - the desugar prepends the receiver, so an implementation that forgot it would pass the
  /// argument as the receiver and read a field of whatever that was.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAMemberCall_WhenRouted_ThenTheReceiverIsPassedFirst(bool optimize) {
    const string source = """
      TYPE Counter
        N AS INTEGER
        SUB Bump(BYVAL by AS INTEGER)
          THIS.N = THIS.N + by
        END SUB
      END TYPE
      DIM c AS Counter
      c.N = 10
      c.Bump 5
      c.Bump 2
      PRINT c.N
      """;

    var (output, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"));
    Assert.Multiple(() => {
      Assert.That(output, Is.EqualTo(Run(source, optimize, routed: false).Output));
      Assert.That(output, Is.EqualTo("17"), "both calls reached the same record");
    });
  }
}
