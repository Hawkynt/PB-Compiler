using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Tests.Exec;

namespace PowerBasic.Compiler.Tests.Backend;

/// <summary>
/// Executed dialect gate for the IR middle end and x86-16 back end. Parser acceptance is insufficient:
/// every advertised dialect must survive lowering, route through the back end, assemble into an MZ
/// executable and behave like the dialect-aware direct emitter.
/// </summary>
[TestFixture]
public sealed class BackendDialectDifferentialTests {

  private const string _portableProgram = """
    10 A% = 0
    20 FOR I% = 1 TO 6
    30 A% = A% + I%
    40 NEXT I%
    50 A% = A% * 2
    60 PRINT A%
    70 END
    """;

  private static readonly Dialect[] _allDialects = Enum.GetValues<Dialect>();

  private static SemanticModel Bind(Dialect dialect) {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize(_portableProgram, "T.BAS", dialect), "T.BAS", dialect),
      dialect);
    Assert.That(model.Errors, Is.Empty, "bind: " + string.Join("; ", model.Errors));
    return model;
  }

  /// <summary>
  /// Given an advertised dialect, when the same program is compiled through both x86-16 paths and
  /// executed, then the routed image must have used the back end and exhibit identical behaviour.
  /// </summary>
  [TestCaseSource(nameof(_allDialects))]
  public void Run_GivenAnyAdvertisedDialect_ThenTheBackEndProducesAnEquivalentExecutable(Dialect dialect) {
    var direct = new CodeGenerator(Bind(dialect)) { Optimize = true, UseExperimentalBackend = false };
    var routed = new CodeGenerator(Bind(dialect)) { Optimize = true, UseExperimentalBackend = true };

    var directImage = direct.EmitExecutable();
    var routedImage = routed.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(direct.Errors, Is.Empty, "direct: " + string.Join("; ", direct.Errors));
      Assert.That(routed.Errors, Is.Empty, "routed: " + string.Join("; ", routed.Errors));
      Assert.That(routed.BackendRoutedNames, Does.Contain("main"), "the back end declined the portable core");
    });

    var directRun = Cpu8086.Run(directImage);
    var routedRun = Cpu8086.Run(routedImage);
    Assert.Multiple(() => {
      Assert.That(routedRun.Output, Is.EqualTo(directRun.Output));
      Assert.That(routedRun.ExitCode, Is.EqualTo(directRun.ExitCode));
      Assert.That(routedRun.Output.Trim(), Is.EqualTo("42"));
    });
  }
}
