using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>
/// <c>$ERROR STACK ON</c> on the IR path: every procedure entry probes for headroom and raises
/// Error 201 without it.
///
/// <para>
/// The direct emitter writes the probe inline as <c>CMP SP, [rt_stackmin]</c>, and <c>SP</c> is not a
/// value the IR has any way to name - so the comparison moved into a routine. The two therefore
/// cannot probe at the same instant and do not try to: the frames differ in shape between the paths
/// and no adjustment would make the moment identical. What is identical is the contract, which is
/// what these tests assert.
/// </para>
/// </summary>
[TestFixture]
public sealed class StackProbeLoweringTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static string Run(string source, bool routed) {
    var cg = new CodeGenerator(Bind(source)) { Optimize = true, UseExperimentalBackend = routed };
    var image = cg.EmitExecutable();
    Assert.That(cg.Errors, Is.Empty, string.Join("; ", cg.Errors));
    return Cpu8086.Run(image, maxSteps: 8_000_000).Output;
  }

  /// <summary>
  /// Runaway recursion, which is what the probe exists to stop - and deliberately NOT tail
  /// recursion. The direct emitter turns a self-call in tail position into a frame-reusing jump
  /// (pb36 O14), so <c>CALL Deep(n + 1)</c> as the last statement never consumes a stack at all: it
  /// becomes a loop and runs until the emulator's step limit. The trailing PRINT is what keeps this
  /// a real recursion on BOTH paths, which is the only way the two can be compared here.
  /// </summary>
  private const string _RUNAWAY = """
    $ERROR STACK ON
    DECLARE SUB Deep(BYVAL n AS INTEGER)
    ON ERROR GOTO Trapped
    CALL Deep(1)
    PRINT "returned"
    END
    Trapped:
      PRINT "caught"; ERR
      END
    SUB Deep(BYVAL n AS INTEGER)
      CALL Deep(n + 1)
      PRINT n;
    END SUB
    """;

  /// <summary>Ordinary nesting, which it must not stop.</summary>
  private const string _SHALLOW = """
    $ERROR STACK ON
    DECLARE SUB Down(BYVAL n AS INTEGER)
    CALL Down(20)
    PRINT "done"
    END
    SUB Down(BYVAL n AS INTEGER)
      IF n > 0 THEN CALL Down(n - 1)
    END SUB
    """;

  [Test]
  public void Lowering_GivenTheStackCheck_ThenTheModuleLowers() {
    foreach (var (name, source) in new[] { ("runaway", _RUNAWAY), ("shallow", _SHALLOW) }) {
      var module = IrLowering.TryLowerModule(Bind(source), out var why);
      Assert.That(module, Is.Not.Null, $"'{name}' declined: {why}");
    }
  }

  /// <summary>
  /// The contract: recursion that would run the stack out raises 201 and is catchable, rather than
  /// scribbling over whatever lies below the stack and carrying on.
  /// </summary>
  [Test]
  public void Runaway_GivenTheStackCheck_ThenErrorTwoHundredAndOneIsRaised() {
    Assert.That(Run(_RUNAWAY, routed: true), Does.Contain("caught"));
    Assert.That(Run(_RUNAWAY, routed: true), Does.Contain("201"));
  }

  /// <summary>
  /// And a program that nests twenty deep is not disturbed by it. A probe with its comparison the
  /// wrong way round would pass the test above and fail this one.
  /// </summary>
  [Test]
  public void Shallow_GivenTheStackCheck_ThenNothingIsRaised()
    => Assert.That(Run(_SHALLOW, routed: true).Trim(), Is.EqualTo("done"));

  [Test]
  public void Routed_GivenTheStackCheck_ThenItBehavesAsTheDirectEmitterDoes() {
    foreach (var (name, source) in new[] { ("runaway", _RUNAWAY), ("shallow", _SHALLOW) })
      Assert.That(Run(source, routed: true), Is.EqualTo(Run(source, routed: false)), $"program '{name}'");
  }

  /// <summary>
  /// Without the directive there is no probe, and the same program runs the stack off the bottom
  /// rather than reporting anything - the emulator stops it with a fault or a step limit. That is
  /// what says the check is the DIRECTIVE's doing and not something the lowering does anyway: a
  /// probe emitted unconditionally would make every other test here pass and this one fail.
  ///
  /// <para>
  /// The directive is removed WITHOUT its newline. A raw string literal keeps the file's own line
  /// endings, so matching on <c>"...ON\n"</c> quietly matches nothing on a CRLF checkout - and a
  /// replacement that silently does not happen leaves this test asserting the opposite of what it
  /// says. It did, and reported 201 from the probe that was still there.
  /// </para>
  /// </summary>
  [Test]
  public void Runaway_WithoutTheDirective_ThenNothingCatchesIt() {
    var unguarded = _RUNAWAY.Replace("$ERROR STACK ON", "");
    Assert.That(unguarded, Does.Not.Contain("$ERROR"), "the directive must actually be gone");
    Assert.That(() => Run(unguarded, routed: true), Throws.TypeOf<Cpu8086Exception>());
  }
}
