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
  public void CodeGenerator_PublicSurface_HasNoBackendRoutingSelector() {
    var properties = typeof(CodeGenerator).GetProperties(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

    Assert.Multiple(() => {
      Assert.That(properties.Any(p => p.Name == "UseExperimentalBackend"), Is.False,
        "production callers must not be able to reactivate the retired direct emitter");
      Assert.That(properties.Any(p => p.Name == "RequireBackend"), Is.False,
        "mandatory IR routing is no longer a selectable production policy");
    });
  }

  [Test]
  public void CodeGenerator_GivenMandatoryRoutingDecline_ThenDoesNotEmitWithDirectFallback() {
    var model = Binder.Bind(
      Parser.Parse(Lexer.Tokenize("DECLARE SUB Missing()\nCALL Missing\nEND", "T.BAS", Dialect.Pb35),
        "T.BAS", Dialect.Pb35),
      Dialect.Pb35);
    var generator = new CodeGenerator(model);

    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(image, Is.Empty, "a mandatory routing decline must not produce a legacy-emitter image");
      Assert.That(generator.BackendRoutedNames, Does.Not.Contain("main"));
      Assert.That(generator.Errors.Select(e => e.Message),
        Has.Some.Contains("routing is mandatory").Or.Some.Contains("external procedure"));
    });
  }

  [TestCase("--x-backend")]
  [TestCase("--x-backend-strict")]
  [TestCase("--no-x-backend")]
  public void Driver_GivenLegacyBackendSelector_ThenRejectsIt(string option) {
    using var stderr = new StringWriter();

    var exitCode = Driver.Run([option, "unused.bas"], TextWriter.Null, stderr);

    Assert.Multiple(() => {
      Assert.That(exitCode, Is.EqualTo(1));
      Assert.That(stderr.ToString(), Does.Contain(option));
      Assert.That(stderr.ToString(), Does.Contain("was removed"));
      Assert.That(stderr.ToString(), Does.Contain("IR/native backend is mandatory"));
    });
  }
}
