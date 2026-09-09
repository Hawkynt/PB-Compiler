using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Procedure-local error traps need one ABI behavior the module body does not: a callee may replace
/// the process-global handler triple while it runs, but its caller's triple must be back on every
/// ordinary return. These cases execute both emitters and pin that boundary with a real nested trap.
/// </summary>
[TestFixture]
public sealed class BackendProcedureErrorHandlerRoutingTests {

  [TestCase(false)]
  [TestCase(true)]
  public void Procedure_GivenHandlerAndNormalReturn_ThenCallerHandlerIsRestored(bool optimize) =>
    BothPathsAgree("""
      DECLARE SUB Inner()
      ON ERROR GOTO OuterTrap
      Inner
      ERROR 9
      PRINT "bad-main"
      END
      OuterTrap:
      PRINT "outer"
      RESUME Finished
      Finished:
      END

      SUB Inner() NOINLINE
        ON ERROR GOTO InnerTrap
        PRINT "inner"
        EXIT SUB
      InnerTrap:
        PRINT "bad-inner"
        RESUME NEXT
      END SUB
      """, "inner|outer", optimize);

  [TestCase(false)]
  [TestCase(true)]
  public void Procedure_GivenHandledInnerFault_ThenCallerHandlerIsRestoredAfterResume(bool optimize) =>
    BothPathsAgree("""
      DECLARE SUB Inner()
      ON ERROR GOTO OuterTrap
      Inner
      ERROR 9
      PRINT "bad-main"
      END
      OuterTrap:
      PRINT "outer"
      RESUME Finished
      Finished:
      END

      SUB Inner() NOINLINE
        ON ERROR GOTO InnerTrap
        ERROR 5
        PRINT "bad-inner"
        EXIT SUB
      InnerTrap:
        PRINT "inner"
        RESUME InnerDone
      InnerDone:
      END SUB
      """, "inner|outer", optimize);

  [TestCase(false)]
  [TestCase(true)]
  public void Function_GivenHandlerAndIntegerResult_ThenRestoreDoesNotClobberReturnRegister(bool optimize) =>
    BothPathsAgree("""
      DECLARE FUNCTION Inner%()
      ON ERROR GOTO OuterTrap
      IF Inner%() = 42 THEN PRINT "result"
      ERROR 9
      PRINT "bad-main"
      END
      OuterTrap:
      PRINT "outer"
      RESUME Finished
      Finished:
      END

      FUNCTION Inner%() NOINLINE
        ON ERROR GOTO InnerTrap
        Inner% = 42
        EXIT FUNCTION
      InnerTrap:
        Inner% = 0
        RESUME NEXT
      END FUNCTION
      """, "result|outer", optimize);

  private static void BothPathsAgree(string source, string expected, bool optimize) {
    var routed = CompileAndRun(source, optimize, routed: true);
    var direct = CompileAndRun(source, optimize, routed: false);

    Assert.Multiple(() => {
      Assert.That(routed.Routed, Does.Contain("Inner"), "the handler-bearing procedure did not route");
      Assert.That(routed.Routed, Does.Contain("main"), "the caller did not route");
      Assert.That(routed.Output, Is.EqualTo(direct.Output), "routed and direct execution disagree");
      Assert.That(routed.Output, Is.EqualTo(expected));
      Assert.That(routed.ExitCode, Is.EqualTo(direct.ExitCode));
    });
  }

  private static (string Output, int ExitCode, IReadOnlyList<string> Routed) CompileAndRun(
      string source, bool optimize, bool routed) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));

    var generator = new CodeGenerator(model) { Optimize = optimize, UseExperimentalBackend = routed };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, string.Join("; ", generator.Errors));
    var result = Cpu8086.Run(image);
    return (result.Output.Trim().Replace("\r\n", "|"), result.ExitCode, generator.BackendRoutedNames.ToList());
  }
}
