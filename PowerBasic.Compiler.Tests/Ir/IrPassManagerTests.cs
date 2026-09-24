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
  public void MigratedPasses_DoNotConstructPrivateAnalysisManagers() {
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var passDir = Path.Combine(root, "PowerBasic.Compiler", "Ir", "Passes");
    var offenders = Directory.EnumerateFiles(passDir, "*.cs")
      .Where(file => !Path.GetFileName(file).Equals("IrFunctionPassPipeline.cs", StringComparison.Ordinal))
      .Where(file => File.ReadAllText(file).Contains("new IrAnalysisManager(", StringComparison.Ordinal))
      .Select(Path.GetFileName)
      .Order()
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "analysis caches are owned by IrFunctionPassPipeline, never by individual optimization passes");
  }

  [Test]
  public void ModulePipeline_PublicSurface_HasNoLegacyAdapter() {
    var publicMethods = typeof(IrModulePassPipeline).GetMethods(System.Reflection.BindingFlags.Public
      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

    Assert.That(publicMethods.Any(m => m.Name == "AddLegacy"), Is.False);
  }

  [Test]
  public void ProductionCallers_DoNotOwnMiddleEndOptimizationChoreography() {
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var files = new[] {
      Path.Combine(root, "pbc", "Driver.cs"),
      Path.Combine(root, "PowerBasic.Compiler", "CodeGen", "CodeGenerator.Backend.cs"),
    };
    var forbidden = new[] {
      "Inliner.Run(",
      "GlobalDce.Run(",
      "ParallelLoopVersioning.Run(",
      "IntegerRecovery.Run(",
      "SwitchFormation.Run(",
      "StringStackPromotion.Run(",
      "MemoryRoutineSpecialization.Run(",
      "InstructionSelector.TrySelect(",
      "MachineScheduler.Schedule(",
      "LinearScanAllocator.Allocate(",
    };

    var violations = files
      .SelectMany(file => forbidden
        .Where(token => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
        .Select(token => $"{Path.GetFileName(file)}: {token}"))
      .ToArray();

    Assert.That(violations, Is.Empty,
      "production callers must delegate middle-end policy to IrMiddleEndPipeline");
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
  public void Standard_Plan_HasNamedPhasesWithoutChangingTheHistoricalFlattenedOrder() {
    var manager = IrMiddleEndPipeline.Standard(
      optimizeForSpeed: true,
      includeModulePasses: true,
      dataLayoutTarget: new IrDataLayoutTarget(32, 16, 32768, 64, 8),
      enableFpLookupTables: true,
      optimizeForSize: true,
      recoverIntegerArithmetic: true);

    var functionPlan = manager.PassPlan.Where(pass => pass.Scope == IrPassScope.Function).ToArray();
    var expected = new[] {
      "integer-recovery", "storagenarrow", "mem2reg", "storagenarrow-ssa",
      "structpack", "fieldreorder", "hotcold", "aos2soa", "transpose", "arrayfusion",
      "arraycontract", "prefixscan", "ptrcompress", "cachepad", "arraypad", "arrayalign",
      "looptemp-reuse", "overflow-version", "ownershipbatch", "unroll",
      "instcombine", "demandedbits", "sccp", "correlate", "bbversion", "ptrcheck",
      "rangefold", "specnarrow", "conversion-rangefold", "overflow-coalesce", "sroa",
      "aggregate-sroa", "storagenarrow2", "mem2reg2", "storagenarrow-ssa2", "strcow",
      "ownership-elision", "fpsimplify", "reassociate", "fpfast", "eqsat", "verified-arith",
      "polynomial", "demote", "ivsimplify", "phicong", "gvn", "memopt", "dse",
      "interchange", "licm", "reciprocal-reuse", "unswitch", "loopversion", "dce",
      "allocsink", "closed-form", "deadloop", "ifconv", "simplifycfg", "tailrec", "switchform",
    };

    Assert.Multiple(() => {
      Assert.That(functionPlan.Select(pass => pass.Name), Is.EqualTo(expected),
        "phase naming must not silently reorder the proven production pipeline");
      Assert.That(manager.PassPlan.Any(pass => pass.Phase == IrMiddleEndPhase.Unspecified), Is.False,
        "every production transform belongs to an explicit phase");
      Assert.That(manager.PassPlan.Single(pass => pass.Scope == IrPassScope.EarlyModule).Phase,
        Is.EqualTo(IrMiddleEndPhase.Canonicalization));
      Assert.That(manager.PassPlan.Where(pass => pass.Scope == IrPassScope.Module)
        .All(pass => pass.Phase == IrMiddleEndPhase.Interprocedural), Is.True);
    });

    Assert.Multiple(() => {
      Assert.That(functionPlan.Single(pass => pass.Name == "mem2reg").Phase,
        Is.EqualTo(IrMiddleEndPhase.SsaPreparation));
      Assert.That(functionPlan.Single(pass => pass.Name == "structpack").Phase,
        Is.EqualTo(IrMiddleEndPhase.DataLayout));
      Assert.That(functionPlan.Single(pass => pass.Name == "instcombine").Phase,
        Is.EqualTo(IrMiddleEndPhase.ScalarSimplification));
      Assert.That(functionPlan.Single(pass => pass.Name == "strcow").Phase,
        Is.EqualTo(IrMiddleEndPhase.MemoryAndObjects));
      Assert.That(functionPlan.Single(pass => pass.Name == "gvn").Phase,
        Is.EqualTo(IrMiddleEndPhase.ArithmeticSimplification));
      Assert.That(functionPlan.Single(pass => pass.Name == "dse").Phase,
        Is.EqualTo(IrMiddleEndPhase.MemoryOptimization));
      Assert.That(functionPlan.Single(pass => pass.Name == "licm").Phase,
        Is.EqualTo(IrMiddleEndPhase.LoopOptimization));
      Assert.That(functionPlan.Single(pass => pass.Name == "switchform").Phase,
        Is.EqualTo(IrMiddleEndPhase.LateScalarCleanup));
    });
  }

  [Test]
  public void RunToFixpoint_GivenAnExhaustedBudget_ThenReportsTheNonConvergingPasses() {
    var fn = new IrFunction("never-settles", IrType.Void);
    var manager = new IrPassManager()
      .InFunctionPhase(IrMiddleEndPhase.ScalarSimplification)
      .AddAnalyzed("oscillating-probe", (_, _) => IrPassResult.Changed(1));

    Assert.That(manager.RunToFixpoint(fn, maxIterations: 3), Is.EqualTo(3));

    var diagnostic = manager.LastFixpointDiagnostic;
    Assert.That(diagnostic, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(diagnostic!.FunctionName, Is.EqualTo("never-settles"));
      Assert.That(diagnostic.IterationBudget, Is.EqualTo(3));
      Assert.That(diagnostic.CompletedIterations, Is.EqualTo(3));
      Assert.That(diagnostic.ChangedPasses.Select(pass => pass.Name), Is.EqualTo(new[] { "oscillating-probe" }));
      Assert.That(diagnostic.ChangedPasses.Single().Phase, Is.EqualTo(IrMiddleEndPhase.ScalarSimplification));
    });
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
