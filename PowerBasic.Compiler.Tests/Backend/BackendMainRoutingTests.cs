using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// The module body compiled by the x86-16 back end - the step from "the back end compiles some
/// functions" to "the back end compiles a whole program". It is the same pipeline every routed
/// procedure goes through, with the three differences that follow from main not being a procedure:
/// no arguments, no caller to RET to (it falls into the runtime's exit), and no entry in
/// ProcedureList, so the routing looks it up by name.
/// </summary>
[TestFixture]
public sealed class BackendMainRoutingTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36), Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private const string _wholeProgram = """
    DIM n AS INTEGER
    n = 6
    PRINT "n="
    PRINT n * 7
    """;

  [Test]
  public void Emit_GivenASelectableModuleBody_ThenTheBackEndOwnsTheWholeProgram() {
    var routed = new CodeGenerator(Bind(_wholeProgram)) { Optimize = true, UseExperimentalBackend = true };

    var image = routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(image, Is.Not.Empty);
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
  }

  [Test]
  public void Emit_GivenTheGate_ThenTheDirectPathStillOwnsMainByDefault() {
    var direct = new CodeGenerator(Bind(_wholeProgram)) { Optimize = true, UseExperimentalBackend = false };

    direct.EmitExecutable();

    Assert.That(direct.BackendRoutedNames, Is.Empty, "the back end is opt-in");
  }

  /// <summary>
  /// A module body that arms an error handler is now routed. It used to be excluded on the grounds
  /// that ON ERROR is emitted AROUND the body by the direct path - true of a PROCEDURE, which saves
  /// and restores the caller's handler triple, but never true of main, which has no caller to
  /// restore for. What the back end really needed was the ability to EMIT the arming: it captures
  /// the current BP and SP, so it expands inline rather than becoming a call, and the handler is
  /// named by the offset of its own block.
  /// </summary>
  [Test]
  public void Emit_GivenErrorHandlingInTheModuleBody_ThenTheBackEndTakesIt() {
    var routed = new CodeGenerator(Bind("""
      ON ERROR GOTO oops
      DIM n AS INTEGER
      n = 1
      PRINT n
      END
      oops:
      RESUME NEXT
      """)) { Optimize = true, UseExperimentalBackend = true };

    routed.EmitExecutable();

    Assert.That(routed.Errors, Is.Empty, string.Join("; ", routed.Errors));
    Assert.That(routed.BackendRoutedNames, Does.Contain("main"));
  }

  /// <summary>
  /// A PROCEDURE that arms one is still excluded, and for the reason the module body never had: the
  /// direct path saves the caller's handler triple on entry and restores it on every exit, and that
  /// bookkeeping has no equivalent in the routed prologue yet. Routing it would lose the caller's
  /// handler silently.
  /// </summary>
  [Test]
  public void Emit_GivenErrorHandlingInAProcedure_ThenItStaysWithTheDirectPath() {
    var routed = new CodeGenerator(Bind("""
      CALL Risky
      END
      SUB Risky
        ON ERROR GOTO oops
        ERROR 5
        EXIT SUB
        oops:
        RESUME NEXT
      END SUB
      """)) { Optimize = true, UseExperimentalBackend = true };

    routed.EmitExecutable();

    Assert.That(routed.BackendRoutedNames, Does.Not.Contain("Risky"));
  }
}
