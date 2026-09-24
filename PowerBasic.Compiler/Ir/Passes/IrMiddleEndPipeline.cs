using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// The sole owner of production IR middle-end policy. <see cref="IrPassManager"/> is the execution
/// engine; this type decides which transformations form the legalization and optimizing pipelines.
/// Keeping policy here prevents the old monolithic pass-manager API from becoming a second middle end.
/// </summary>
public static class IrMiddleEndPipeline {

  /// <summary>Builds the representation-only pipeline required even when source optimization is disabled.</summary>
  public static IrPassManager Legalize(bool recoverIntegerArithmetic = false) => new IrPassManager()
    .InFunctionPhase(IrMiddleEndPhase.Legalization)
    .AddAnalyzedWhen(recoverIntegerArithmetic, "integer-recovery", IntegerRecovery.Run)
    .AddAnalyzed("mem2reg-faithful", Mem2Reg.RunForFaithfulSelection)
    .AddAnalyzed("instcombine-faithful", (fn, _) => Conservative(() => InstCombine.RunForFaithfulSelection(fn)))
    .AddAnalyzed("dce", Dce.Run)
    .AddAnalyzed("simplifycfg", (fn, _) => Conservative(() => SimplifyCfg.Run(fn)));

  /// <summary>Builds the analysis-aware optimizing middle end in its proven relative order.</summary>
  public static IrPassManager Standard(bool optimizeForSpeed = false, bool includeModulePasses = true,
      IrDataLayoutTarget? dataLayoutTarget = null, bool enableFpLookupTables = false, bool optimizeForSize = false,
      IIrArithmeticCostModel? arithmeticCostModel = null,
      IIrCallCostModel? callCostModel = null,
      int minimumIntegerStorageBits = 16,
      bool recoverIntegerArithmetic = false)
    => new IrPassManager { OptimizeForSpeed = optimizeForSpeed }
    .InEarlyModulePhase(IrMiddleEndPhase.Canonicalization)
    .AddEarlyModuleConservativeWhen(includeModulePasses, "array-zero-fill", ArrayZeroFillElision.Run)
    .InFunctionPhase(IrMiddleEndPhase.SsaPreparation)
    .AddAnalyzedWhen(recoverIntegerArithmetic, "integer-recovery", IntegerRecovery.Run)
    .AddAnalyzed("storagenarrow", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("mem2reg", Mem2Reg.Run)
    .AddAnalyzed("storagenarrow-ssa", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .InFunctionPhase(IrMiddleEndPhase.DataLayout)
    .AddAnalyzed("structpack", (fn, _) => Conservative(() => StructurePackingByRange.Run(fn)))
    .AddAnalyzed("fieldreorder", (fn, _) => Conservative(() => FieldReordering.Run(fn)))
    .AddAnalyzed("hotcold", (fn, _) => Conservative(() => HotColdFieldSplitting.Run(fn)))
    .AddAnalyzed("aos2soa", (fn, _) => Conservative(() => ArrayOfStructsToStructOfArrays.Run(fn)))
    .AddAnalyzed("transpose", (fn, _) => Conservative(() => DataTransposition.Run(fn)))
    .AddAnalyzed("arrayfusion", (fn, _) => Conservative(() => TemporaryArrayFusion.Run(fn)))
    .AddAnalyzed("arraycontract", (fn, _) => Conservative(() => ArrayContraction.Run(fn)))
    .AddAnalyzed("prefixscan", (fn, _) => Conservative(() => ParallelPrefixScan.Run(fn)))
    .AddAnalyzedWhen(dataLayoutTarget?.PointerBits > 16, "ptrcompress",
      (fn, _) => Conservative(() => PointerCompression.Run(fn, dataLayoutTarget!.PointerBits)))
    .AddAnalyzedWhen(dataLayoutTarget?.CacheSizeBytes > 0, "cachepad",
      (fn, _) => Conservative(() => CacheConflictPadding.Run(fn, dataLayoutTarget!.CacheSizeBytes,
        dataLayoutTarget.CacheLineBytes, dataLayoutTarget.CacheAssociativity)))
    .AddAnalyzedWhen(dataLayoutTarget?.VectorBytes > 1, "arraypad",
      (fn, _) => Conservative(() => ArrayPaddingAlignment.Run(fn, dataLayoutTarget!.VectorBytes)))
    .AddAnalyzedWhen(dataLayoutTarget?.VectorBytes > 1, "arrayalign",
      (fn, _) => Conservative(() => ArrayBaseAlignment.Run(fn, dataLayoutTarget!.VectorBytes, dataLayoutTarget.PointerBits)))
    .InFunctionPhase(IrMiddleEndPhase.LoopPreparation)
    .AddAnalyzed("looptemp-reuse", (fn, _) => Conservative(() => LoopTemporaryReuse.Run(fn)))
    .AddAnalyzed("overflow-version", (fn, _) => Conservative(() => SpeculativeOverflowElimination.Run(fn)))
    .AddAnalyzed("ownershipbatch", (fn, _) => Conservative(() => OwnershipBatching.Run(fn)))
    .AddAnalyzed("unroll", (fn, _) => Conservative(() => LoopUnroll.Run(fn)))
    .InFunctionPhase(IrMiddleEndPhase.ScalarSimplification)
    .AddAnalyzed("instcombine", (fn, _) => Conservative(() => InstCombine.Run(fn)))
    .AddAnalyzedWhen(optimizeForSpeed, "demandedbits", DemandedBits.Run)
    .AddAnalyzed("sccp", (fn, _) => Conservative(() => Sccp.Run(fn)))
    .AddAnalyzed("correlate", CorrelatedValueProp.Run)
    .AddAnalyzed("bbversion", BasicBlockVersioning.Run)
    .AddAnalyzed("ptrcheck", PointerCheckElim.Run)
    .AddAnalyzed("rangefold", RangeCheckElim.Run)
    .AddAnalyzedWhen(optimizeForSpeed, "specnarrow", (fn, _) => Conservative(() => SpeculativeIntegerNarrowing.Run(fn)))
    .AddAnalyzed("conversion-rangefold", ConversionRangeCheckElim.Run)
    .AddAnalyzed("overflow-coalesce", (fn, _) => Conservative(() => OverflowCheckCoalescing.Run(fn)))
    .AddAnalyzed("sroa", (fn, _) => Conservative(() => ScalarReplaceArrays.Run(fn)))
    .AddAnalyzed("aggregate-sroa", (fn, _) => Conservative(() => ScalarReplaceAggregates.Run(fn)))
    .InFunctionPhase(IrMiddleEndPhase.MemoryAndObjects)
    .AddAnalyzed("storagenarrow2", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("mem2reg2", Mem2Reg.Run)
    .AddAnalyzed("storagenarrow-ssa2", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("strcow", (fn, _) => Conservative(() => StringCopyOnWriteElision.Run(fn)))
    .AddAnalyzed("ownership-elision", (fn, _) => Conservative(() => HandleOwnershipElision.Run(fn)))
    .InFunctionPhase(IrMiddleEndPhase.ArithmeticSimplification)
    .AddAnalyzed("fpsimplify", (fn, analyses) => FpSimplify.Run(fn,
      optimizeForSpeed ? IrFastMathFlags.Fast : IrFastMathFlags.None, analyses))
    .AddAnalyzed("reassociate", Reassociate.Run)
    .AddAnalyzedWhen(optimizeForSpeed, "fpfast",
      (fn, _) => Conservative(() => FpFastMath.Run(fn, IrFastMathFlags.Fast)))
    .AddAnalyzed("eqsat", (fn, _) => Conservative(() => EqualitySaturation.Run(fn)))
    .AddAnalyzed("verified-arith", (fn, _) => Conservative(() => VerifiedArithmeticLowering.Run(fn, optimizeForSpeed)))
    .AddAnalyzed("polynomial", (fn, _) => Conservative(() => PolynomialEvaluation.Run(fn)))
    .AddAnalyzed("demote", (fn, _) => Conservative(() => FloatDemotion.Run(fn)))
    .AddAnalyzed("ivsimplify", InductionVariableSimplification.Run)
    .AddAnalyzed("phicong", (fn, _) => Conservative(() => PhiCongruence.Run(fn)))
    .AddAnalyzed("gvn", Gvn.Run)
    .InFunctionPhase(IrMiddleEndPhase.MemoryOptimization)
    .AddAnalyzed("memopt", RedundantMemory.Run)
    .AddAnalyzed("dse", DeadStoreElim.Run)
    .InFunctionPhase(IrMiddleEndPhase.LoopOptimization)
    .AddAnalyzed("interchange", (fn, _) => Conservative(() => LoopInterchange.Run(fn)))
    .AddAnalyzed("licm", Licm.Run)
    .AddAnalyzed("reciprocal-reuse", (fn, analyses) => ReciprocalSequenceReuse.Run(fn, arithmeticCostModel, analyses))
    .AddAnalyzed("unswitch", (fn, _) => Conservative(() => LoopUnswitch.Run(fn)))
    .AddAnalyzed("loopversion", (fn, _) => Conservative(() => LoopVersioning.Run(fn)))
    .AddAnalyzed("dce", Dce.Run)
    .AddAnalyzed("allocsink", (fn, _) => Conservative(() => AllocationSinking.Run(fn)))
    .AddAnalyzed("closed-form", (fn, _) => Conservative(() => RecurrenceClosedForm.Run(fn)))
    .AddAnalyzedWhen(optimizeForSpeed, "deadloop", (fn, _) => Conservative(() => DeadLoopElimination.Run(fn)))
    .InFunctionPhase(IrMiddleEndPhase.LateScalarCleanup)
    .AddAnalyzed("ifconv", (fn, _) => Conservative(() => IfConversion.Run(fn)))
    .AddAnalyzed("simplifycfg", (fn, _) => Conservative(() => SimplifyCfg.Run(fn)))
    .AddAnalyzed("tailrec", (fn, _) => Conservative(() => TailRecursion.Run(fn)))
    .AddAnalyzed("switchform", (fn, _) => Conservative(() => SwitchFormation.Run(fn)))
    .InModulePhase(IrMiddleEndPhase.Interprocedural)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "cold-outline", ColdCodeOutlining.Run)
    .AddModuleConservativeWhen(includeModulePasses, "icp",
      module => IndirectCallPromotion.Run(module, callCostModel))
    .AddModuleAnalyzedWhen(includeModulePasses, "return-structure-reduction", ReturnStructureReduction.Run)
    .AddModuleAnalyzedWhen(includeModulePasses, "argstruct", ArgumentStructureReduction.Run)
    .AddModuleAnalyzedWhen(includeModulePasses && optimizeForSpeed, "spec-devirt", SpeculativeDevirtualization.Run)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "inline-speed",
      module => Inliner.Run(module, optimizeForSpeed: true))
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "ctxclone", ContextSensitiveCloning.Run)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "inline-context",
      module => Inliner.Run(module, optimizeForSpeed: true))
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "libcalls", LibraryCallRecognition.Run)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "static-search", StaticSearchRecognition.Run)
    .AddModuleConservativeWhen(includeModulePasses, "bitsets", BitsetSubstitution.Run)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "lutgen", LookupTableGeneration.Run)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSpeed, "fpdomain",
      module => FpDomainSpecialization.Run(module, enableFpLookupTables))
    .AddModuleConservativeWhen(includeModulePasses, "lutelim", LookupTableElimination.Run)
    .AddModuleConservativeWhen(includeModulePasses, "const-data-merge", ConstantDataMerging.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strfold", StringConstantFold.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strchain", StringConcatChain.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strappend", StringAppendInPlace.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strcapacity", StringCapacityHoisting.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strcmpeq", StringCompareEquality.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strempty", StringEmptinessTest.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strslice", StringSliceLength.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strbyte", StringByteRead.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strview", StringSliceView.Run)
    .AddModuleConservativeWhen(includeModulePasses, "strcoalesce", StringAllocationCoalescing.Run)
    .AddModuleConservativeWhen(includeModulePasses, "readonly-globals", ReadOnlyGlobals.Run)
    .AddModuleConservativeWhen(includeModulePasses, "localize-globals", LocalizeGlobals.Run)
    .AddModuleAnalyzedWhen(includeModulePasses, "devirt", WholeProgramDevirtualization.Run)
    .AddModuleAnalyzedWhen(includeModulePasses, "ipconstprop", IpConstantProp.Run)
    .AddModuleAnalyzedWhen(includeModulePasses, "dead-pure-calls", FunctionSummaries.RemoveDeadPureCalls)
    .AddModuleConservativeWhen(includeModulePasses && optimizeForSize, "semantic-merge", SemanticFunctionMerging.Run);

  /// <summary>
  /// Runs the production native middle end with the historical sweep/restart order intact. Policy,
  /// including the final ordinary inliner, lives here so backend routing cannot grow a second optimizer.
  /// </summary>
  public static void RunNativeModule(
      IrModule module,
      bool optimize,
      bool optimizeForSpeed = false,
      bool optimizeForSize = false,
      IIrArithmeticCostModel? arithmeticCostModel = null,
      IIrCallCostModel? callCostModel = null,
      int minimumIntegerStorageBits = 16,
      bool recoverIntegerArithmetic = false,
      TargetCost? targetCost = null) {
    ArgumentNullException.ThrowIfNull(module);

    Func<IrPassManager> pipeline = optimize
      ? () => Standard(optimizeForSpeed,
          arithmeticCostModel: arithmeticCostModel,
          callCostModel: callCostModel ?? targetCost,
          minimumIntegerStorageBits: minimumIntegerStorageBits,
          recoverIntegerArithmetic: recoverIntegerArithmetic)
      : () => Legalize(recoverIntegerArithmetic);

    pipeline().RunOnModule(module);
    pipeline().RunOnModule(module);

    if (optimize && !optimizeForSize && Inliner.Run(module) > 0) {
      pipeline().RunOnModule(module);
      pipeline().RunOnModule(module);
    }

    if (optimize) {
      StringStackPromotion.Run(module);
      foreach (var function in module.Functions)
        if (!function.IsDeclaration)
          MemoryRoutineSpecialization.Run(function,
            targetCost ?? new TargetCost(CpuTier.I8086, CostObjective.Balanced));
    }

    // Kept as a separate final sweep for now: this is the pre-cutover native ordering. Once exact-head
    // validation proves the centralized runner equivalent, redundancy can be tested and removed alone.
    if (optimize)
      foreach (var function in module.Functions)
        if (!function.IsDeclaration && SwitchFormation.Run(function) > 0) {
          SimplifyCfg.Run(function);
          Dce.Run(function);
        }
  }

  /// <summary>
  /// Runs the hosted C/LLVM middle end. Optional parallel-loop preparation stays ahead of Standard,
  /// while final inlining and whole-program DCE stay after the same full-pipeline replay they had before.
  /// </summary>
  public static void RunHostedModule(
      IrModule module,
      bool optimize,
      bool optimizeForSpeed = false,
      bool enableFpLookupTables = false,
      bool recoverIntegerArithmetic = false,
      bool parallelLoops = false) {
    ArgumentNullException.ThrowIfNull(module);

    var pipeline = optimize
      ? Standard(optimizeForSpeed: optimizeForSpeed, enableFpLookupTables: enableFpLookupTables,
          recoverIntegerArithmetic: recoverIntegerArithmetic)
      : Legalize();

    if (parallelLoops) {
      foreach (var function in module.Functions)
        if (!function.IsDeclaration)
          Mem2Reg.Run(function);
      ParallelLoopVersioning.Run(module);
    }

    pipeline.RunOnModule(module);
    if (!optimize)
      return;

    pipeline.RunOnModule(module);
    Inliner.Run(module);
    pipeline.RunOnModule(module);
    pipeline.RunOnModule(module);
    GlobalDce.Run(module);
  }

  /// <summary>
  /// Adapts a transform that does not currently consume analyses to the analysis-aware execution contract.
  /// Mutating transforms invalidate all cached facts; unchanged transforms preserve the complete cache.
  /// This is deliberately local to pipeline policy so no second legacy registration API can reappear.
  /// </summary>
  private static IrPassResult Conservative(Func<int> run) {
    var changes = run();
    return changes == 0 ? IrPassResult.Unchanged : IrPassResult.Changed(changes);
  }
}
