namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// The sole owner of production IR middle-end policy. <see cref="IrPassManager"/> is the execution
/// engine; this type decides which transformations form the legalization and optimizing pipelines.
/// Keeping policy here prevents the old monolithic pass-manager API from becoming a second middle end.
/// </summary>
public static class IrMiddleEndPipeline {

  /// <summary>Builds the representation-only pipeline required even when source optimization is disabled.</summary>
  public static IrPassManager Legalize() => new IrPassManager()
    .Add("mem2reg-faithful", Mem2Reg.RunForFaithfulSelection)
    .Add("instcombine-faithful", InstCombine.RunForFaithfulSelection)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);

  /// <summary>Builds the analysis-aware optimizing middle end in its proven relative order.</summary>
  public static IrPassManager Standard(bool optimizeForSpeed = false, bool includeModulePasses = true,
      IrDataLayoutTarget? dataLayoutTarget = null, bool enableFpLookupTables = false, bool optimizeForSize = false,
      IIrArithmeticCostModel? arithmeticCostModel = null,
      int minimumIntegerStorageBits = 16)
    => new IrPassManager { OptimizeForSpeed = optimizeForSpeed }
    .AddEarlyModulePassWhen(includeModulePasses, "array-zero-fill", ArrayZeroFillElision.Run)
    .Add("storagenarrow", fn => StorageNarrowing.Run(fn, minimumIntegerStorageBits))
    .Add("mem2reg", Mem2Reg.Run)
    .Add("storagenarrow-ssa", fn => StorageNarrowing.Run(fn, minimumIntegerStorageBits))
    .Add("structpack", StructurePackingByRange.Run)
    .Add("fieldreorder", FieldReordering.Run)
    .Add("hotcold", HotColdFieldSplitting.Run)
    .Add("aos2soa", ArrayOfStructsToStructOfArrays.Run)
    .Add("transpose", DataTransposition.Run)
    .Add("arrayfusion", TemporaryArrayFusion.Run)
    .Add("arraycontract", ArrayContraction.Run)
    .Add("prefixscan", ParallelPrefixScan.Run)
    .AddWhen(dataLayoutTarget?.PointerBits > 16, "ptrcompress",
      fn => PointerCompression.Run(fn, dataLayoutTarget!.PointerBits))
    .AddWhen(dataLayoutTarget?.CacheSizeBytes > 0, "cachepad",
      fn => CacheConflictPadding.Run(fn, dataLayoutTarget!.CacheSizeBytes, dataLayoutTarget.CacheLineBytes, dataLayoutTarget.CacheAssociativity))
    .AddWhen(dataLayoutTarget?.VectorBytes > 1, "arraypad",
      fn => ArrayPaddingAlignment.Run(fn, dataLayoutTarget!.VectorBytes))
    .AddWhen(dataLayoutTarget?.VectorBytes > 1, "arrayalign",
      fn => ArrayBaseAlignment.Run(fn, dataLayoutTarget!.VectorBytes, dataLayoutTarget.PointerBits))
    .Add("looptemp-reuse", LoopTemporaryReuse.Run)
    .Add("overflow-version", SpeculativeOverflowElimination.Run)
    .Add("ownershipbatch", OwnershipBatching.Run)
    .Add("unroll", LoopUnroll.Run)
    .Add("instcombine", InstCombine.Run)
    .AddWhen(optimizeForSpeed, "demandedbits", DemandedBits.Run)
    .Add("sccp", Sccp.Run)
    .AddAnalyzed("correlate", CorrelatedValueProp.Run)
    .Add("bbversion", BasicBlockVersioning.Run)
    .AddAnalyzed("ptrcheck", PointerCheckElim.Run)
    .AddAnalyzed("rangefold", RangeCheckElim.Run)
    .AddWhen(optimizeForSpeed, "specnarrow", SpeculativeIntegerNarrowing.Run)
    .AddAnalyzed("conversion-rangefold", ConversionRangeCheckElim.Run)
    .Add("overflow-coalesce", OverflowCheckCoalescing.Run)
    .Add("sroa", ScalarReplaceArrays.Run)
    .Add("aggregate-sroa", ScalarReplaceAggregates.Run)
    .Add("storagenarrow2", fn => StorageNarrowing.Run(fn, minimumIntegerStorageBits))
    .Add("mem2reg2", Mem2Reg.Run)
    .Add("storagenarrow-ssa2", fn => StorageNarrowing.Run(fn, minimumIntegerStorageBits))
    .Add("strcow", StringCopyOnWriteElision.Run)
    .Add("ownership-elision", HandleOwnershipElision.Run)
    .Add("fpsimplify", fn => FpSimplify.Run(fn,
      optimizeForSpeed ? IrFastMathFlags.Fast : IrFastMathFlags.None))
    .Add("reassociate", Reassociate.Run)
    .AddWhen(optimizeForSpeed, "fpfast", fn => FpFastMath.Run(fn, IrFastMathFlags.Fast))
    .Add("eqsat", EqualitySaturation.Run)
    .Add("verified-arith", fn => VerifiedArithmeticLowering.Run(fn, optimizeForSpeed))
    .Add("polynomial", PolynomialEvaluation.Run)
    .Add("demote", FloatDemotion.Run)
    .AddAnalyzed("ivsimplify", InductionVariableSimplification.Run)
    .Add("phicong", PhiCongruence.Run)
    .AddAnalyzed("gvn", Gvn.Run)
    .Add("memopt", RedundantMemory.Run)
    .Add("dse", DeadStoreElim.Run)
    .Add("interchange", LoopInterchange.Run)
    .AddAnalyzed("licm", Licm.Run)
    .AddAnalyzed("reciprocal-reuse", (fn, analyses) => ReciprocalSequenceReuse.Run(fn, analyses, arithmeticCostModel))
    .Add("unswitch", LoopUnswitch.Run)
    .AddAnalyzed("loopversion", LoopVersioning.Run)
    .Add("dce", Dce.Run)
    .Add("allocsink", AllocationSinking.Run)
    .Add("closed-form", RecurrenceClosedForm.Run)
    .AddWhen(optimizeForSpeed, "deadloop", DeadLoopElimination.Run)
    .Add("ifconv", IfConversion.Run)
    .Add("simplifycfg", SimplifyCfg.Run)
    .Add("tailrec", TailRecursion.Run)
    .Add("switchform", SwitchFormation.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "cold-outline", ColdCodeOutlining.Run)
    .AddModulePassWhen(includeModulePasses, "icp", IndirectCallPromotion.Run)
    .AddModulePassWhen(includeModulePasses, "return-structure-reduction", ReturnStructureReduction.Run)
    .AddModulePassWhen(includeModulePasses, "argstruct", ArgumentStructureReduction.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "spec-devirt", SpeculativeDevirtualization.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "inline-speed",
      module => Inliner.Run(module, optimizeForSpeed: true))
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "ctxclone", ContextSensitiveCloning.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "inline-context",
      module => Inliner.Run(module, optimizeForSpeed: true))
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "libcalls", LibraryCallRecognition.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "static-search", StaticSearchRecognition.Run)
    .AddModulePassWhen(includeModulePasses, "bitsets", BitsetSubstitution.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "lutgen", LookupTableGeneration.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "fpdomain",
      module => FpDomainSpecialization.Run(module, enableFpLookupTables))
    .AddModulePassWhen(includeModulePasses, "lutelim", LookupTableElimination.Run)
    .AddModulePassWhen(includeModulePasses, "const-data-merge", ConstantDataMerging.Run)
    .AddModulePassWhen(includeModulePasses, "strfold", StringConstantFold.Run)
    .AddModulePassWhen(includeModulePasses, "strchain", StringConcatChain.Run)
    .AddModulePassWhen(includeModulePasses, "strappend", StringAppendInPlace.Run)
    .AddModulePassWhen(includeModulePasses, "strcapacity", StringCapacityHoisting.Run)
    .AddModulePassWhen(includeModulePasses, "strcmpeq", StringCompareEquality.Run)
    .AddModulePassWhen(includeModulePasses, "strempty", StringEmptinessTest.Run)
    .AddModulePassWhen(includeModulePasses, "strslice", StringSliceLength.Run)
    .AddModulePassWhen(includeModulePasses, "strbyte", StringByteRead.Run)
    .AddModulePassWhen(includeModulePasses, "strview", StringSliceView.Run)
    .AddModulePassWhen(includeModulePasses, "strcoalesce", StringAllocationCoalescing.Run)
    .AddModulePassWhen(includeModulePasses, "readonly-globals", ReadOnlyGlobals.Run)
    .AddModulePassWhen(includeModulePasses, "localize-globals", LocalizeGlobals.Run)
    .AddModulePassWhen(includeModulePasses, "devirt", WholeProgramDevirtualization.Run)
    .AddModulePassWhen(includeModulePasses, "ipconstprop", IpConstantProp.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSize, "semantic-merge", SemanticFunctionMerging.Run);
}
