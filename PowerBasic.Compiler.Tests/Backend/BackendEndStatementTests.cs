using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// <c>END [n]</c>, which terminates the PROGRAM wherever it is written.
///
/// <para>
/// Two faults, and they were the same one: the lowering read neither the exit code nor anything but
/// whether it was in main. <c>END</c> inside a procedure declined, and <c>END n</c> anywhere threw its
/// code away.
/// </para>
/// <para>
/// The second is the one worth keeping a test for, because nothing else could have caught it. The
/// differential harness compares <c>RESULT.TXT</c> - a program's output - and an exit code is not in
/// it; the corpus suites that do compare exit codes never end with one. So <c>END 3</c> exited 3
/// directly and 0 routed, on the DEFAULT path, with every gate green.
/// </para>
/// </summary>
[TestFixture]
public sealed class BackendEndStatementTests {

  private static (string Output, int Exit, IEnumerable<string> Routed) Run(string source, bool optimize, bool routed) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    var generator = new CodeGenerator(model) { Optimize = optimize};
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    var cpu = Cpu8086.Run(image);
    return (cpu.Output.Trim().Replace("\r\n", "|"), cpu.ExitCode, generator.BackendRoutedNames.ToList());
  }

  /// <summary>
  /// The code has to be a value the optimizer cannot read, or the assertion would hold for a lowering
  /// that folded one constant into the epilogue and still ignored the argument in general.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenEndWithAnExitCode_WhenRouted_ThenDosSeesIt(bool optimize) {
    const string source = """
      DECLARE FUNCTION Code%(BYVAL v%)
      DIM warm AS INTEGER
      warm = Code%(9)
      PRINT "bye"
      END Code%(3)
      FUNCTION Code%(BYVAL v%) NOINLINE
        Code% = v%
      END FUNCTION
      """;

    var (output, exit, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("main"));
    var direct = Run(source, optimize, routed: false);
    Assert.Multiple(() => {
      Assert.That((output, exit), Is.EqualTo((direct.Output, direct.Exit)));
      Assert.That(exit, Is.EqualTo(3), "END n sets the errorlevel DOS reports");
      Assert.That(output, Is.EqualTo("bye"));
    });
  }

  /// <summary>
  /// <c>END</c> inside a procedure: the program stops there, so neither the statement after the call
  /// nor the rest of the procedure runs. Both halves are asserted - a lowering that returned from the
  /// procedure instead of exiting would skip the second and print the first.
  /// </summary>
  [TestCase(true)]
  [TestCase(false)]
  public void Execute_GivenEndInsideAProcedure_WhenRouted_ThenTheProgramStopsThere(bool optimize) {
    const string source = """
      DECLARE SUB Stop2()
      PRINT "start"
      Stop2
      PRINT "never"
      END
      SUB Stop2() NOINLINE
        PRINT "in"
        END 7
        PRINT "unreached"
      END SUB
      """;

    var (output, exit, routed) = Run(source, optimize, routed: true);
    Assert.That(routed, Does.Contain("Stop2"), "the procedure containing END must route now");
    var direct = Run(source, optimize, routed: false);
    Assert.Multiple(() => {
      Assert.That((output, exit), Is.EqualTo((direct.Output, direct.Exit)));
      Assert.That(output, Is.EqualTo("start|in"), "nothing after the END ran, in either scope");
      Assert.That(exit, Is.EqualTo(7));
    });
  }
}
