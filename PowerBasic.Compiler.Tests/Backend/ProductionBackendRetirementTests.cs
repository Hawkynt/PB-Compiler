using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class ProductionBackendRetirementTests {

  [Test]
  public void CodeGenerator_GivenNoRoutingOverride_ThenIrBackendIsMandatory() {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize("PRINT 1\nEND", "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35),
      Dialect.Pb35);

    var generator = new CodeGenerator(model);

    Assert.Multiple(() => {
      Assert.That(generator.UseExperimentalBackend, Is.True);
      Assert.That(generator.RequireBackend, Is.True);
    });
  }

  [Test]
  public void Driver_GivenLegacyDirectEmitterSwitch_ThenRejectsIt() {
    using var stderr = new StringWriter();

    var exitCode = Driver.Run(["--no-x-backend", "unused.bas"], TextWriter.Null, stderr);

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.EqualTo(1));
      Assert.That(stderr.ToString(), Does.Contain("was removed"));
      Assert.That(stderr.ToString(), Does.Contain("IR/native backend is mandatory"));
    });
  }
}
