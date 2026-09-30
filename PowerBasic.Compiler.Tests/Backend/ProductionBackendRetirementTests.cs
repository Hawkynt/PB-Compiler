using PowerBasic.Compiler.Cli;
using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Backend;

[TestFixture]
public sealed class ProductionBackendRetirementTests {

  private static readonly string _repoRoot =
    Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));

  private static readonly string[] _retiredSyntaxEmitterFiles = [
    "CodeGenerator.Arrays.cs",
    "CodeGenerator.Expressions.cs",
    "CodeGenerator.Extras.cs",
    "CodeGenerator.Graphics.cs",
    "CodeGenerator.Intrinsics.cs",
    "CodeGenerator.Io.cs",
    "CodeGenerator.LowLevel.cs",
    "CodeGenerator.OnGoto.cs",
    "CodeGenerator.Optimize.cs",
    "CodeGenerator.OverflowVectorization.cs",
    "CodeGenerator.Places.cs",
    "CodeGenerator.Procs.cs",
    "CodeGenerator.Search.cs",
    "CodeGenerator.Trivial.cs",
    "CodeGenerator.Vendor.cs",
  ];

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
  public void CompilerSources_GivenTheRetirementBoundary_ThenNoSyntaxOrRawByteEmitterRemains() {
    var codeGeneratorDirectory = Path.Combine(_repoRoot, "PowerBasic.Compiler", "CodeGen");
    var existingSyntaxEmitters = _retiredSyntaxEmitterFiles
      .Where(file => File.Exists(Path.Combine(codeGeneratorDirectory, file)))
      .ToList();
    var rawByteEmitter = Path.Combine(_repoRoot, "PowerBasic.Compiler", "Emit", "DosTrivialImage.cs");
    var codeGeneratorMethods = typeof(CodeGenerator)
      .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
      .Select(method => method.Name)
      .ToHashSet(StringComparer.Ordinal);
    var retiredEntryPoints = new[] {
      "EmitStatement", "EmitStatements", "EmitExpression", "EmitProcedure", "EmitDirect",
      "CanCallDirectCallee", "DirectCalleeWithCompatibleAbi", "InlineAsmAboveTarget",
    }.Where(codeGeneratorMethods.Contains).ToList();

    Assert.Multiple(() => {
      Assert.That(existingSyntaxEmitters, Is.Empty,
        "retired bound-AST machine emitters must not re-enter the SDK-default compiler inputs");
      Assert.That(File.Exists(rawByteEmitter), Is.False,
        "a whole-program raw-byte shortcut bypasses selection, scheduling, allocation and MachineEmitter");
      Assert.That(typeof(CodeGenerator).Assembly.GetType("PowerBasic.Compiler.Emit.DosTrivialImage"), Is.Null,
        "the raw-byte shortcut must not survive in the compiled compiler under another project-item rule");
      Assert.That(retiredEntryPoints, Is.Empty,
        "a renamed source file must not restore a syntax-level emission entry point");
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
