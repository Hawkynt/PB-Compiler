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

  [Test]
  public void Emit_GivenErrorHandling_ThenMainStaysWithTheDirectPath() {
    // ON ERROR is emitted AROUND the body by the direct path, not inside it - a routed body would
    // silently lose the handler
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

    Assert.That(routed.BackendRoutedNames, Does.Not.Contain("main"));
  }
}
