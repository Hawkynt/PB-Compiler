using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Syntax;

[TestFixture]
public sealed class StatementSurfaceBackendTests {
  private static IEnumerable<TestCaseData> GetConfigurations() {
    foreach (var dialect in StatementSurface.AllDialects)
      foreach (var optimize in new[] { false, true })
        foreach (var emitCom in new[] { false, true })
          yield return new TestCaseData(dialect, optimize, emitCom).SetName(
            $"Compile_{dialect.CanonicalName()}_{(optimize ? "optimized" : "plain")}_{(emitCom ? "COM" : "MZ")}");
  }

  private static string? CompileForm(StatementSurface.Form form, Dialect dialect, bool optimize, bool emitCom) {
    try {
      var source = StatementSurface.Program(form, dialect);
      var model = Binder.Bind(Parser.Parse(Lexer.Tokenize(source, "T.BAS", dialect), "T.BAS", dialect), dialect);
      if (model.Errors.Count != 0)
        return "bind: " + string.Join("; ", model.Errors);
      var compiler = new CodeGenerator(model) { Optimize = optimize, Container = DosContainer.Exe };
      var image = emitCom ? compiler.EmitCom() : compiler.EmitExecutable();
      if (compiler.Errors.Count != 0)
        return "emit: " + string.Join("; ", compiler.Errors);
      var declines = compiler.BackendDeclines.Where(decline =>
        !decline.Reason.StartsWith("filter: external declaration", StringComparison.Ordinal)).ToArray();
      if (declines.Length != 0)
        return "routing: " + string.Join("; ", declines);
      var lowIr = compiler.BackendModuleForTesting;
      if (lowIr?.RepresentationStage != IrRepresentationStage.LowIr)
        return "machine selection did not consume Low IR";
      var violations = IrVerifier.Verify(lowIr);
      if (violations.Count != 0)
        return "Low IR verification: " + string.Join("; ", violations);
      if (lowIr.Functions.Any(function => !function.IsDeclaration && function.Name == "main")
          && !compiler.BackendEmittedNamesForTesting.Contains("main"))
        return "retained main body was not emitted from IR";
      var unownedBodies = compiler.BackendEmittedNamesForTesting.Where(name =>
        !lowIr.Functions.Any(function => !function.IsDeclaration
          && function.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
      if (unownedBodies.Length != 0)
        return "emitted bodies without IR definitions: " + string.Join(", ", unownedBodies);
      return image.Length < 2 || image.AsSpan(0, 2).SequenceEqual("MZ"u8) == emitCom
        ? "artifact does not match the explicit container request"
        : null;
    } catch (Exception error) {
      return error.GetType().Name + ": " + error.Message;
    }
  }

  [Test]
  public void Catalog_GivenDeclaredDialects_ThenIncludesEveryCompilerDialect() {
    Assert.That(StatementSurface.AllDialects, Is.EquivalentTo(Enum.GetValues<Dialect>()));
    var matrix = typeof(StatementSurfaceBackendTests).GetMethod(
      nameof(Compile_GivenEveryAcceptedStatementForm_ThenEmitsOnlyVerifiedLowIrBodies));
    Assert.That(matrix!.GetCustomAttributes(false).OfType<CategoryAttribute>().Select(category => category.Name),
      Does.Contain("Slow"), "the corpus-wide matrix belongs in the full gate, not the fast smoke tier");
  }

  [TestCaseSource(nameof(GetConfigurations))]
  [Category("Slow")]
  public void Compile_GivenEveryAcceptedStatementForm_ThenEmitsOnlyVerifiedLowIrBodies(
      Dialect dialect, bool optimize, bool emitCom) {
    var forms = StatementSurface.All.Where(form => StatementSurface.ShouldAccept(form, dialect)).ToArray();
    var failures = new List<string>();
    foreach (var form in forms)
      if (CompileForm(form, dialect, optimize, emitCom) is { } error)
        failures.Add($"{form.Id}: {error}");
    Assert.Multiple(() => {
      Assert.That(forms, Is.Not.Empty, "an empty catalog is not backend coverage");
      Assert.That(failures, Is.Empty, string.Join("\n", failures));
    });
    TestContext.Out.WriteLine($"{forms.Length} forms emitted through verified Low IR with no routing declines");
  }
}
