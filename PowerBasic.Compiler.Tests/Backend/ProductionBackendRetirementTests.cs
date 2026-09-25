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
    var image = generator.EmitExecutable();

    Assert.Multiple(() => {
      Assert.That(image, Is.Not.Empty);
      Assert.That(generator.Errors, Is.Empty);
      Assert.That(generator.BackendRoutedNames, Does.Contain("main"), "the IR back end compiled the program");
    });
  }

  [Test]
  public void CodeGenerator_PublicSurface_HasNoBackendRoutingSelector() {
    var properties = typeof(CodeGenerator).GetProperties(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
      | System.Reflection.BindingFlags.Static);

    Assert.Multiple(() => {
      Assert.That(properties.Any(p => p.Name == "UseExperimentalBackend"), Is.False,
        "there is no second code generator to select");
      Assert.That(properties.Any(p => p.Name == "RequireBackend"), Is.False,
        "IR routing is not a policy: a declined body is a compile error");
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
