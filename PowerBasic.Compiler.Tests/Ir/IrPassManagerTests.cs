using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Analysis;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Tests.Ir;

/// <summary>The pass manager: the standard pipeline run to a verified fixpoint.</summary>
[TestFixture]
public sealed class IrPassManagerTests {

  [TestCase(false)]
  [TestCase(true)]
  public void AddModuleConservativeWhen_GivenACondition_ThenRunsOnlyWhenEnabled(bool enabled) {
    var module = new IrModule("test");
    var calls = 0;
    var manager = new IrPassManager()
      .AddModuleConservativeWhen(enabled, "probe", _ => {
        ++calls;
        return 0;
      });

    manager.RunOnModule(module);

    Assert.That(calls, Is.EqualTo(enabled ? 1 : 0));
  }

  [Test]
  public void AddModuleAnalyzed_GivenPreservedAnalysis_ThenProductionRunnerReusesIt() {
    var module = new IrModule("test");
    var computations = 0;
    var analysis = new IrModuleAnalysisKey<int>("probe", (_, _) => ++computations);
    var manager = new IrPassManager()
      .AddModuleAnalyzed("read-before", (_, analyses) => {
        analyses.Get(analysis);
        return IrModulePassResult.Unchanged;
      })
      .AddModuleAnalyzed("preserve", (_, _) => IrModulePassResult.ChangedPreserving(1, analysis))
      .AddModuleAnalyzed("read-after", (_, analyses) => {
        analyses.Get(analysis);
        return IrModulePassResult.Unchanged;
      });

    manager.RunOnModule(module);

    Assert.That(computations, Is.EqualTo(1));
  }

  [Test]
  public void AddAnalyzed_GivenPreservedAnalysis_ThenProductionRunnerReusesIt() {
    var fn = new IrFunction("test", IrType.Void);
    var computations = 0;
    var analysis = new IrAnalysisKey<int>("probe", (_, _) => ++computations);
    var manager = new IrPassManager()
      .AddAnalyzed("read-before", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      })
      .AddAnalyzed("preserve", (_, _) => IrPassResult.ChangedPreserving(1, analysis))
      .AddAnalyzed("read-after", (_, analyses) => {
        analyses.Get(analysis);
        return IrPassResult.Unchanged;
      });

    var changes = manager.Run(fn);

    Assert.Multiple(() => {
      Assert.That(changes, Is.EqualTo(1));
      Assert.That(computations, Is.EqualTo(1));
    });
  }

  [TestCase(0)]
  [TestCase(-1)]
  public void RunToFixpoint_GivenNonPositiveIterationBudget_ThenDoesNotRun(int maxIterations) {
    var fn = new IrFunction("test", IrType.Void);
    var calls = 0;
    var manager = new IrPassManager()
      .AddAnalyzed("probe", (_, _) => {
        ++calls;
        return IrPassResult.Changed(1);
      });

    var changes = manager.RunToFixpoint(fn, maxIterations);

    Assert.Multiple(() => {
      Assert.That(changes, Is.Zero);
      Assert.That(calls, Is.Zero);
    });
  }

  [Test]
  public void PassManager_Surface_HasNoLegacyFunctionRegistrationOrPipelinePolicy() {
    var methods = typeof(IrPassManager).GetMethods(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
      | System.Reflection.BindingFlags.Static);

    Assert.Multiple(() => {
      Assert.That(methods.Any(m => m.Name == "Add"), Is.False,
        "function transforms must enter through AddAnalyzed and report preservation");
      Assert.That(methods.Any(m => m.Name is "AddModulePass" or "AddModulePassWhen"), Is.False,
        "module transforms must use analyzed or explicitly conservative registration");
      Assert.That(methods.Any(m => m.Name is "Standard" or "Legalize"), Is.False,
        "pipeline policy belongs exclusively to IrMiddleEndPipeline");
    });
  }

  [Test]
  public void Standard_SourceContainsNoLegacyFunctionRegistrations() {
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var source = File.ReadAllLines(Path.Combine(root, "PowerBasic.Compiler", "Ir", "Passes", "IrMiddleEndPipeline.cs"));
    var legacy = source.Where(line => System.Text.RegularExpressions.Regex.IsMatch(
      line, @"^\s*\.Add(?:When)?\s*\("));

    Assert.That(legacy, Is.Empty,
      "production function optimizers must register through AddAnalyzed/AddAnalyzedWhen only");
  }

  [Test]
  public void ModulePipeline_PublicSurface_HasNoLegacyAdapter() {
    var publicMethods = typeof(IrModulePassPipeline).GetMethods(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

    Assert.That(publicMethods.Any(m => m.Name == "AddLegacy"), Is.False);
  }

  [Test]
  public void FunctionPipeline_PublicSurface_HasNoLegacyAdapter() {
    var publicMethods = typeof(IrFunctionPassPipeline).GetMethods(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

    Assert.That(publicMethods.Any(m => m.Name == "AddLegacy"), Is.False);
  }

  private static IrFunction Lower(string source) {
    var unit = Parser.Parse(Lexer.Tokenize(source, "T.BAS", Dialect.Pb35), "T.BAS", Dialect.Pb35);
    return IrLowering.TryLowerMainBody(Binder.Bind(unit, Dialect.Pb35))!;
  }

  [Test]
  public void Standard_OverLoweredProgram_OptimizesToAVerifiedFixpoint() {
    var fn = Lower(
      "a% = 2\nb% = 3\nc% = a% + b%\nIF c% > 4 THEN\n  d% = c% * 2\nELSE\n  d% = 0\nEND IF");
    var pm = IrMiddleEndPipeline.Standard();
    pm.VerifyEachPass = true;

    pm.RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    // a second run must make no further change - the pipeline has reached a fixpoint
    Assert.That(pm.RunToFixpoint(fn), Is.EqualTo(0));
  }

  [Test]
  public void Standard_FullyEvaluatesAConstantOnlyProgram() {
    // everything is compile-time constant and unused -> the body collapses to ret void
    var fn = Lower("a% = 10\nb% = 20\nc% = a% + b%");
    IrMiddleEndPipeline.Standard().RunToFixpoint(fn);

    Assert.That(IrPrinter.Print(fn), Is.EqualTo(
      "define void @main() {\n" +
      "entry:\n" +
      "  ret void\n" +
      "}\n"));
  }

  [Test]
  public void VerifyEachPass_ThrowsIfAPassWouldLeaveInvalidIr() {
    var fn = new IrFunction("bad", IrType.Void);
    fn.CreateBlock("entry").Append(new IrBinary(IrBinaryOp.Add, IrBuilder.ConstI32(1), IrBuilder.ConstI32(2)));  // no terminator
    var pm = new IrPassManager { VerifyEachPass = true }
      .AddAnalyzed("noop", (_, _) => IrPassResult.Unchanged);

    Assert.That(() => pm.Run(fn), Throws.TypeOf<IrVerificationException>());
  }

  [Test]
  public void Standard_OverLoop_PromotesAndStaysVerifiable() {
    var fn = Lower("s% = 0\nFOR i% = 1 TO 10\n  s% = s% + i% * 2\nNEXT i%");
    var pm = IrMiddleEndPipeline.Standard();
    pm.VerifyEachPass = true;

    pm.RunToFixpoint(fn);

    Assert.That(IrVerifier.Verify(fn), Is.Empty);
    Assert.That(fn.AllInstructions.OfType<IrAlloca>().Count(), Is.EqualTo(0));   // fully promoted
  }
}
