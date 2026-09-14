namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// The sole owner of production IR middle-end policy. <see cref="IrPassManager"/> is the execution
/// engine; this type decides which transformations form the legalization and optimizing pipelines.
/// Keeping policy here prevents the old monolithic pass-manager API from becoming a second middle end.
/// </summary>
public static class IrMiddleEndPipeline {

  /// <summary>Builds the representation-only pipeline required even when source optimization is disabled.</summary>
  public static IrPassManager Legalize() => new IrPassManager()
    .AddAnalyzed("mem2reg-faithful", (fn, _) => Conservative(() => Mem2Reg.RunForFaithfulSelection(fn)))
    .AddAnalyzed("instcombine-faithful", (fn, _) => Conservative(() => InstCombine.RunForFaithfulSelection(fn)))
    .AddAnalyzed("dce", (fn, _) => Conservative(() => Dce.Run(fn)))
    .AddAnalyzed("simplifycfg", (fn, _) => Conservative(() => SimplifyCfg.Run(fn)));

  /// <summary>Builds the analysis-aware optimizing middle end in its proven relative order.</summary>
  public static IrPassManager Standard(bool optimizeForSpeed = false, bool includeModulePasses = true,
      IrDataLayoutTarget? dataLayoutTarget = null, bool enableFpLookupTables = false, bool optimizeForSize = false,
      IIrArithmeticCostModel? arithmeticCostModel = null,
      int minimumIntegerStorageBits = 16)
    => new IrPassManager { OptimizeForSpeed = optimizeForSpeed }
    .AddEarlyModulePassWhen(includeModulePasses, "array-zero-fill", ArrayZeroFillElision.Run)
    .AddAnalyzed("storagenarrow", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("mem2reg", (fn, _) => Conservative(() => Mem2Reg.Run(fn)))
    .AddAnalyzed("storagenarrow-ssa", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
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
    .AddAnalyzed("looptemp-reuse", (fn, _) => Conservative(() => LoopTemporaryReuse.Run(fn)))
    .AddAnalyzed("overflow-version", (fn, _) => Conservative(() => SpeculativeOverflowElimination.Run(fn)))
    .AddAnalyzed("ownershipbatch", (fn, _) => Conservative(() => OwnershipBatching.Run(fn)))
    .AddAnalyzed("unroll", (fn, _) => Conservative(() => LoopUnroll.Run(fn)))
    .AddAnalyzed("instcombine", (fn, _) => Conservative(() => InstCombine.Run(fn)))
    .AddAnalyzedWhen(optimizeForSpeed, "demandedbits", (fn, _) => Conservative(() => DemandedBits.Run(fn)))
    .AddAnalyzed("sccp", (fn, _) => Conservative(() => Sccp.Run(fn)))
    .AddAnalyzed("correlate", CorrelatedValueProp.Run)
    .AddAnalyzed("bbversion", (fn, _) => Conservative(() => BasicBlockVersioning.Run(fn)))
    .AddAnalyzed("ptrcheck", PointerCheckElim.Run)
    .AddAnalyzed("rangefold", RangeCheckElim.Run)
    .AddAnalyzedWhen(optimizeForSpeed, "specnarrow", (fn, _) => Conservative(() => SpeculativeIntegerNarrowing.Run(fn)))
    .AddAnalyzed("conversion-rangefold", ConversionRangeCheckElim.Run)
    .AddAnalyzed("overflow-coalesce", (fn, _) => Conservative(() => OverflowCheckCoalescing.Run(fn)))
    .AddAnalyzed("sroa", (fn, _) => Conservative(() => ScalarReplaceArrays.Run(fn)))
    .AddAnalyzed("aggregate-sroa", (fn, _) => Conservative(() => ScalarReplaceAggregates.Run(fn)))
    .AddAnalyzed("storagenarrow2", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("mem2reg2", (fn, _) => Conservative(() => Mem2Reg.Run(fn)))
    .AddAnalyzed("storagenarrow-ssa2", (fn, analyses) => StorageNarrowing.Run(fn, minimumIntegerStorageBits, analyses))
    .AddAnalyzed("strcow", (fn, _) => Conservative(() => StringCopyOnWriteElision.Run(fn)))
    .AddAnalyzed("ownership-elision", (fn, _) => Conservative(() => HandleOwnershipElision.Run(fn)))
    .AddAnalyzed("fpsimplify", (fn, analyses) => FpSimplify.Run(fn,
      optimizeForSpeed ? IrFastMathFlags.Fast : IrFastMathFlags.None, analyses))
    .AddAnalyzed("reassociate", (fn, _) => Conservative(() => Reassociate.Run(fn)))
    .AddAnalyzedWhen(optimizeForSpeed, "fpfast",
      (fn, _) => Conservative(() => FpFastMath.Run(fn, IrFastMathFlags.Fast)))
    .AddAnalyzed("eqsat", (fn, _) => Conservative(() => EqualitySaturation.Run(fn)))
    .AddAnalyzed("verified-arith", (fn, _) => Conservative(() => VerifiedArithmeticLowering.Run(fn, optimizeForSpeed)))
    .AddAnalyzed("polynomial", (fn, _) => Conservative(() => PolynomialEvaluation.Run(fn)))
    .AddAnalyzed("demote", (fn, _) => Conservative(() => FloatDemotion.Run(fn)))
    .AddAnalyzed("ivsimplify", InductionVariableSimplification.Run)
    .AddAnalyzed("phicong", (fn, _) => Conservative(() => PhiCongruence.Run(fn)))
    .AddAnalyzed("gvn", Gvn.Run)
    .AddAnalyzed("memopt", (fn, _) => Conservative(() => RedundantMemory.Run(fn)))
    .AddAnalyzed("dse", (fn, _) => Conservative(() => DeadStoreElim.Run(fn)))
    .AddAnalyzed("interchange", (fn, _) => Conservative(() => LoopInterchange.Run(fn)))
    .AddAnalyzed("licm", Licm.Run)
    .AddAnalyzed("reciprocal-reuse", (fn, analyses) => ReciprocalSequenceReuse.Run(fn, arithmeticCostModel, analyses))
    .AddAnalyzed("unswitch", (fn, _) => Conservative(() => LoopUnswitch.Run(fn)))
    .AddAnalyzed("loopversion", (fn, _) => Conservative(() => LoopVersioning.Run(fn)))
    .AddAnalyzed("dce", (fn, _) => Conservative(() => Dce.Run(fn)))
    .AddAnalyzed("allocsink", (fn, _) => Conservative(() => AllocationSinking.Run(fn)))
    .AddAnalyzed("closed-form", (fn, _) => Conservative(() => RecurrenceClosedForm.Run(fn)))
    .AddAnalyzedWhen(optimizeForSpeed, "deadloop", (fn, _) => Conservative(() => DeadLoopElimination.Run(fn)))
    .AddAnalyzed("ifconv", (fn, _) => Conservative(() => IfConversion.Run(fn)))
    .AddAnalyzed("simplifycfg", (fn, _) => Conservative(() => SimplifyCfg.Run(fn)))
    .AddAnalyzed("tailrec", (fn, _) => Conservative(() => TailRecursion.Run(fn)))
    .AddAnalyzed("switchform", (fn, _) => Conservative(() => SwitchFormation.Run(fn)))
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
