using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// O0275 on the native x86 path. The outliner appends a module-level helper that has no
/// <see cref="ProcedureSymbol"/>, and every part of the routing used to be keyed on one - so the
/// caller was stranded by its own optimization and fell back to the direct emitter wholesale. These
/// tests hold the two halves of the bridge: the helper is ROUTED, and the program it is part of still
/// prints what its source says.
/// </summary>
[TestFixture]
public sealed class BackendColdCodeOutliningTests {

  private static SemanticModel Bind(string source) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb36), "T.BAS", Dialect.Pb36),
      Dialect.Pb36);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  private static (byte[] Image, CodeGenerator Generator) Compile(string source) {
    var generator = new CodeGenerator(Bind(source)) {
      Optimize = true,
    };
    var image = generator.EmitExecutable();
    Assert.That(generator.Errors, Is.Empty, "codegen: " + string.Join("; ", generator.Errors));
    return (image, generator);
  }

  [Test]
  public void Execute_GivenAnOutlinedColdArm_WhenRouted_ThenTheHelperIsRoutedAndTheHotCallStillPrints() {
    const string source = """
      $OPTIMIZE SPEED
      DECLARE FUNCTION Given%(BYVAL v%)
      DECLARE SUB Walk(BYVAL n%)
      Walk Given%(2)
      Walk Given%(7)
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      SUB Walk(BYVAL n%) NOINLINE
        DIM i AS INTEGER
        FOR i = 1 TO 10
          IF n% > 5 THEN
            EXIT SUB
          END IF
        NEXT i
        PRINT "done"; n%
      END SUB
      """;

    var routed = Compile(source);
    var routedResult = Cpu8086.Run(routed.Image);

    Assert.Multiple(() => {
      Assert.That(routed.Generator.BackendGeneratedRoutedNames,
        Has.Some.StartsWith("Walk__cold_"), "O0275's helper must be a routing target of its own");
      Assert.That(routed.Generator.BackendRoutedNames, Does.Contain("Walk"),
        "the procedure the region was lifted out of must stay on the native backend");
      // Walk 2 never takes the cold EXIT SUB and prints; Walk 7 takes it on the first trip and does not
      Assert.That((routedResult.Output, routedResult.ExitCode), Is.EqualTo(("done 2 \r\n", 0)),
        "the outlined helper's ABI must leave the caller's control flow intact");
    });
  }

  /// <summary>
  /// The module body is not in <c>ProcedureList</c> either, so a helper outlined out of it exercises
  /// the half of the bridge a procedure cannot: main is settled AFTER the procedure set, so a helper
  /// whose only caller is main must survive the routing fixpoint that decides the procedures.
  /// </summary>
  [Test]
  public void Execute_GivenAColdArmInTheModuleBody_WhenRouted_ThenMainKeepsItsHelper() {
    const string source = """
      $OPTIMIZE SPEED
      DECLARE FUNCTION Given%(BYVAL v%)
      DIM n AS INTEGER
      n = Given%(0)
      IF n = 0 THEN
        PRINT "zero"
        PRINT "path"
        PRINT "taken"
        END
      END IF
      PRINT "hot"; n
      END

      FUNCTION Given%(BYVAL v%) NOINLINE
        Given% = v%
      END FUNCTION
      """;

    var routed = Compile(source);
    var routedResult = Cpu8086.Run(routed.Image);

    Assert.Multiple(() => {
      Assert.That(routed.Generator.BackendGeneratedRoutedNames,
        Has.Some.StartsWith("main__cold_"), "the module body's cold arm must be outlined and routed");
      Assert.That(routed.Generator.BackendRoutedNames, Does.Contain("main"),
        "a module body that outlines a cold arm must still be owned by the back end");
      Assert.That((routedResult.Output, routedResult.ExitCode), Is.EqualTo(("zero\r\npath\r\ntaken\r\n", 0)),
        "n is 0, so the cold arm runs and ENDs before the hot PRINT");
    });
  }
}
