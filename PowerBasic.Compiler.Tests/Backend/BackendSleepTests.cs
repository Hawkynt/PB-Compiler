using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>SLEEP [n]</c> on the routed path, which had no case for it at all.
///
/// <para>
/// The statement is two alternatives rather than a sequence, and that is the whole of what is worth
/// testing: a non-zero count delays and returns with no key involved, and a zero count waits for a key
/// with no timeout. A lowering that only ever delayed would satisfy any test that just ran
/// <c>SLEEP 1</c> and watched the program finish.
/// </para>
/// <para>
/// So the zero case is the discriminating one, and the interpreter is what makes it observable: it
/// answers the keyboard's PEEK honestly (nothing has been typed) and refuses its blocking READ with
/// "INT 16h AH=00h would block". That refusal is the proof the key branch was taken - a build that
/// delayed instead would finish quietly.
/// </para>
/// <para>
/// Every count comes back through a two-call-site <c>NOINLINE</c> function. A written-down count is
/// folded long before selection, which would leave the branch decided at compile time and the test
/// measuring a program nobody wrote.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendSleepTests {

  private static (byte[] Image, IEnumerable<string> Routed) Compile(string source, bool optimize, bool routed) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    return (image, generator.BackendRoutedNames.ToList());
  }

  private const string _Opaque = """
    DECLARE FUNCTION Count#(BYVAL v#)
    DIM warm AS DOUBLE
    warm = Count#(9)
    """;

  private const string _Epilogue = """
    END
    FUNCTION Count#(BYVAL v#) NOINLINE
      Count# = v#
    END FUNCTION
    """;

  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenANonZeroSleep_WhenRouted_ThenItDelaysAndReturns(bool optimize) {
    var source = _Opaque + "\nSLEEP Count#(1)\nPRINT \"awake\"\n" + _Epilogue;

    var (image, routed) = Compile(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"), "a body containing SLEEP must route now");
    var output = Cpu8086.Run(image).Output.Trim();

    Assert.That(output, Is.EqualTo(Cpu8086.Run(Compile(source, optimize, routed: false).Image).Output.Trim()));
    Assert.That(output, Is.EqualTo("awake"), "the delay ended on the tick counter, with no key involved");
  }

  /// <summary>
  /// The other arm, and the one that says the branch exists. A zero count must reach the KEY wait,
  /// which this interpreter cannot serve and says so - where a build that delayed instead would print
  /// "awake" and pass every comparison the case above can make.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenAZeroSleep_WhenRouted_ThenItWaitsForAKeyRatherThanDelaying(bool optimize) {
    var source = _Opaque + "\nSLEEP Count#(0)\nPRINT \"awake\"\n" + _Epilogue;

    var (image, routed) = Compile(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"));

    var blocked = Assert.Throws<Cpu8086Exception>(() => Cpu8086.Run(image));
    Assert.That(blocked!.Message, Does.Contain("INT 16h"), "the zero count took the key wait");
    // and the direct emitter reaches the same wait, from its own inline INT
    Assert.That(Assert.Throws<Cpu8086Exception>(
      () => Cpu8086.Run(Compile(source, optimize, routed: false).Image))!.Message, Does.Contain("INT 16h"));
  }

  /// <summary>
  /// <c>SLEEP</c> with no argument at all: the key wait with no test in front of it, which is what the
  /// direct emitter emits for it too. Written separately because the parser makes it a different
  /// statement rather than a zero-valued one, and a lowering that required an argument would decline.
  /// </summary>
  [Test]
  public void Execute_GivenABareSleep_WhenRouted_ThenItWaitsForAKey() {
    var (image, routed) = Compile("SLEEP\nPRINT \"awake\"\n", optimize: false, routed: true);

    Assert.That(routed, Does.Contain("main"));
    Assert.That(Assert.Throws<Cpu8086Exception>(() => Cpu8086.Run(image))!.Message, Does.Contain("INT 16h"));
  }
}
