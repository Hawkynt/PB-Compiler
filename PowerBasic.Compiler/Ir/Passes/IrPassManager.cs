namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>Raised when <see cref="IrPassManager.VerifyEachPass"/> is on and a pass leaves the IR malformed.</summary>
public sealed class IrVerificationException(string pass, IReadOnlyList<string> errors)
  : Exception($"IR invalid after pass '{pass}': {string.Join("; ", errors)}") {
  public string Pass { get; } = pass;
  public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Runs an ordered set of function passes, once or to a fixpoint. Each pass reports
/// how many changes it made; the fixpoint loop repeats the set until a full sweep
/// changes nothing. With <see cref="VerifyEachPass"/> on, the IR is verified after
/// every pass so a miscompiling pass is caught immediately - invaluable while the
/// middle-end grows.
/// </summary>
public sealed class IrPassManager {

  private readonly List<(string Name, Func<IrFunction, int> Run)> _passes = [];
  private readonly List<(string Name, Func<IrModule, int> Run)> _modulePasses = [];

  /// <summary>When true, verifies the function after each pass and throws on any error.</summary>
  public bool VerifyEachPass { get; set; }

  public IrPassManager Add(string name, Func<IrFunction, int> pass) {
    this._passes.Add((name, pass));
    return this;
  }

  /// <summary>Adds a pass only when <paramref name="condition"/> holds, so the pipeline stays one expression.</summary>
  public IrPassManager AddWhen(bool condition, string name, Func<IrFunction, int> pass)
    => condition ? this.Add(name, pass) : this;

  /// <summary>Adds an interprocedural pass, run by <see cref="RunOnModule"/> around the function pipeline.</summary>
  public IrPassManager AddModulePass(string name, Func<IrModule, int> pass) {
    this._modulePasses.Add((name, pass));
    return this;
  }

  /// <summary>Adds a module pass only when <paramref name="condition"/> holds.</summary>
  public IrPassManager AddModulePassWhen(bool condition, string name, Func<IrModule, int> pass)
    => condition ? this.AddModulePass(name, pass) : this;

  /// <summary>Runs every pass once over the function; returns the total number of changes.</summary>
  public int Run(IrFunction fn) {
    // a function with an armed error handler has control-flow edges the CFG does not show, so every
    // pass here would be reasoning from an incomplete graph - see IrFunction.HasErrorHandler
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;
    var total = 0;
    foreach (var (name, run) in this._passes) {
      total += run(fn);
      if (this.VerifyEachPass) {
        var errors = IrVerifier.Verify(fn);
        if (errors.Count > 0)
          throw new IrVerificationException(name, errors);
      }
    }
    return total;
  }

  /// <summary>Repeats the pass set until it stops changing anything (or the iteration cap is hit).</summary>
  public int RunToFixpoint(IrFunction fn, int maxIterations = 16) {
    var total = 0;
    for (var i = 0; i < maxIterations; ++i) {
      var changes = this.Run(fn);
      total += changes;
      if (changes == 0)
        break;
    }
    return total;
  }

  /// <summary>
  /// Runs the pipeline over a module: the interprocedural passes first, then the function pipeline
  /// over each body, then the interprocedural passes once more.
  ///
  /// The order is the point. A pass that reasons across the call graph wants the bodies simplified —
  /// a return is only recognisably constant after the body's arithmetic has folded — while the
  /// function passes want the call-graph facts, because a parameter that turns out to be a literal is
  /// what makes a branch inside the body foldable. Neither can go first and be right, so both run,
  /// and the second interprocedural sweep is followed by another function sweep for what it exposed.
  /// </summary>
  public void RunOnModule(IrModule module) {
    RunFunctions();
    foreach (var (_, run) in this._modulePasses)
      if (run(module) > 0)
        RunFunctions();
    return;

    void RunFunctions() {
      foreach (var fn in module.Functions)
        if (!fn.IsDeclaration)
          this.RunToFixpoint(fn);
    }
  }

  /// <summary>
  /// The local legalization the x86-16 selector needs even when optimization is disabled.
  /// It changes representation without applying module or interprocedural optimization.
  /// <list type="bullet">
  ///   <item><b>mem2reg-faithful</b> promotes compiler temporaries into the SSA values consumed by
  ///     instruction selection, but retains source-variable storage whose presence is observable.</item>
  ///   <item><b>instcombine-faithful</b> canonicalizes address and arithmetic forms without folding a
  ///     comparison that originated as a BASIC source condition.</item>
  ///   <item><b>dce</b> removes legalization residue that those representation changes made dead.</item>
  ///   <item><b>simplifycfg</b> removes constant branch forms the selector cannot encode directly.</item>
  /// </list>
  /// <para>
  /// Everything else in <see cref="Standard"/> is optimization and is off: data-layout rewrites,
  /// unrolling, sccp, correlate, pointer checks, integer/float range folds, overflow coalescing,
  /// sroa, aggregate-sroa, mem2reg2, reassociate, polynomial recovery, equality saturation,
  /// verified arithmetic lowering, demote, phicong, gvn, memopt, dse, interchange, licm,
  /// reciprocal reuse, unswitch, closed-form, deadloop, ifconv, tailrec and the string/global
  /// module passes. So are the steps the caller runs around the pipeline - <c>Inliner</c>,
  /// <c>SwitchFormation</c> and <c>MemoryRoutineSpecialization</c>, the last of which is not in
  /// <see cref="Standard"/> at all because it wants the final shape (see CodeGenerator.Backend).
  /// </para>
  /// </summary>
  public static IrPassManager Legalize() => new IrPassManager()
    .Add("mem2reg-faithful", Mem2Reg.RunForFaithfulSelection)
    .Add("instcombine-faithful", InstCombine.RunForFaithfulSelection)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);

  /// <summary>
  /// The default optimization pipeline: promote memory to registers, then iterate
  /// simplification, conditional constant propagation, value numbering and dead-code
  /// elimination to a fixpoint.
  ///
  /// <para>
  /// <paramref name="optimizeForSpeed"/> reflects <c>$OPTIMIZE SPEED</c>. SPEED may spend code size to
  /// erase abstraction overhead: it runs demanded-bit cleanup, admits larger callees to the inliner,
  /// recognizes library loops, generates lookup tables, compiles static searches, removes semantically
  /// dead loops, and grants the relaxed floating-point contract used by O0340-O0345/O0343. The ordinary
  /// optimization objective keeps strict FP semantics, the conservative size budget, and preserves
  /// empty loops because they may be intentional delay loops.
  /// </para>
  /// <para>
  /// <paramref name="dataLayoutTarget"/> supplies facts that are not properties of target-neutral IR:
  /// pointer storage width, vector width and cache geometry. O0324-O0326 stay disabled when those facts
  /// are absent rather than guessing a target. The remaining O0320-O0323 and O0327-O0329 are guarded
  /// entirely by IR provenance/escape/dependence proofs and therefore run on every optimized target.
  /// </para>
  /// <para>
  /// <paramref name="enableFpLookupTables"/> is a backend capability, not another numerical mode. It
  /// allows O0343 to materialize typed floating constant tables when the selected backend can carry
  /// them; range-specialized polynomial kernels remain available under SPEED without it.
  /// </para>
  /// </summary>
  public static IrPassManager Standard(bool optimizeForSpeed = false, bool includeModulePasses = true,
      IrDataLayoutTarget? dataLayoutTarget = null, bool enableFpLookupTables = false)
    => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    // O0320-O0329 have to see the explicit memory graph and the original counted-loop shape. Run the
    // aggregate transforms before AoS->SoA destroys record identity, then the loop/data transforms,
    // and only then unroll. Every one declines escaped/opaque storage rather than speculating aliasing.
    .Add("structpack", StructurePackingByRange.Run)
    .Add("fieldreorder", FieldReordering.Run)
    .Add("hotcold", HotColdFieldSplitting.Run)
    .Add("aos2soa", ArrayOfStructsToStructOfArrays.Run)
    .Add("transpose", DataTransposition.Run)
    .Add("arrayfusion", TemporaryArrayFusion.Run)
    .Add("arraycontract", ArrayContraction.Run)
    .AddWhen(dataLayoutTarget?.PointerBits > 16, "ptrcompress",
      fn => PointerCompression.Run(fn, dataLayoutTarget!.PointerBits))
    .AddWhen(dataLayoutTarget?.CacheSizeBytes > 0, "cachepad",
      fn => CacheConflictPadding.Run(fn, dataLayoutTarget!.CacheSizeBytes, dataLayoutTarget.CacheLineBytes))
    .AddWhen(dataLayoutTarget?.VectorBytes > 1, "arraypad",
      fn => ArrayPaddingAlignment.Run(fn, dataLayoutTarget!.VectorBytes))
    // unrolling goes early, right after values reach SSA: a fully unrolled loop turns its counter
    // into a constant in every copy, which is what gives the rest of the pipeline something to fold
    .Add("unroll", LoopUnroll.Run)
    .Add("instcombine", InstCombine.Run)
    // Demand is a property of uses, so run it after canonicalization has exposed truncations and
    // before value numbering hashes work that may disappear entirely. It is SPEED-only because the
    // ordinary objective is deliberately conservative about compile-time/code-shape expansion.
    .AddWhen(optimizeForSpeed, "demandedbits", DemandedBits.Run)
    .Add("sccp", Sccp.Run)
    .Add("correlate", CorrelatedValueProp.Run)
    // O0351 shares the dominator-scoped edge facts with correlation, but only explicit pointer-null
    // tests count: dereferencing address zero is not a fault on PB's DOS memory model.
    .Add("ptrcheck", PointerCheckElim.Run)
    // AFTER sccp and correlate, and the order is the whole of it: the range analysis reasons about
    // what an expression CAN be, so it wants the values that are already known to be one thing folded
    // in first - a bounds check against a subscript sccp has resolved is not a range question at all.
    // What is left after those two is the class this answers: a loop counter, an IF-joined variable,
    // a masked or divided index - none of which is a constant, and all of which are bounded.
    .Add("rangefold", RangeCheckElim.Run)
    // O0352 is the NaN-aware adjunct to the integer lattice. It deliberately handles only floats
    // whose provenance proves they are ordinary numbers (not an arbitrary float that could be NaN).
    .Add("conversion-rangefold", ConversionRangeCheckElim.Run)
    // O0350 runs after the proofs that can delete individual Error 6 checks. Only the remaining
    // consecutive guards need coalescing, and the pass itself refuses to speculate side effects.
    .Add("overflow-coalesce", OverflowCheckCoalescing.Run)
    // after SCCP, because a subscript is only constant once the index arithmetic has folded - and
    // before the value passes, so the elements it exposes get propagated like any other value
    .Add("sroa", ScalarReplaceArrays.Run)
    // packed TYPE storage is also an alloca i8,N, but its fields are typed byte regions rather than
    // homogeneous elements. Keep the proofs separate: arrays use element stride, aggregates use
    // region bounds and reject overlap so UNION aliasing remains shared storage.
    .Add("aggregate-sroa", ScalarReplaceAggregates.Run)
    .Add("mem2reg2", Mem2Reg.Run)
    // O0346/O0347 consume strict FP facts here, including branch-refined integer ranges at conversion
    // sites. SPEED supplies its explicit no-NaN/no-inf assumptions without changing strict defaults.
    .Add("fpsimplify", fn => FpSimplify.Run(fn,
      optimizeForSpeed ? IrFastMathFlags.Fast : IrFastMathFlags.None))
    // canonicalizes associative chains so GVN hashes two equal expressions the same way; it must
    // come after SCCP (which supplies the constants it folds together) and before GVN (which is the
    // pass that benefits)
    .Add("reassociate", Reassociate.Run)
    // O0340-O0345 are legal only after SPEED grants the relaxed FP contract. The pass annotates each
    // operation with only the flags that apply to it; targets remain responsible for target-specific
    // FMA/reciprocal/rsqrt lowering.
    .AddWhen(optimizeForSpeed, "fpfast", fn => FpFastMath.Run(fn, IrFastMathFlags.Fast))
    // O0354: unlike the sequential canonicalizers above, local equality saturation keeps several
    // equivalent pure-integer forms alive under a hard budget and extracts the cheapest result.
    .Add("eqsat", EqualitySaturation.Run)
    // O0359: arithmetic identities used for lowering DIV/MOD and non-trivial constant multiplies are
    // admitted only after exhaustive verification over the complete 16-bit input domain.
    .Add("verified-arith", VerifiedArithmeticLowering.Run)
    // Horner recovery wants the canonical integer expression after reassociation, while its result is
    // still early enough for GVN and DCE to collect the now-dead literal power tree.
    .Add("polynomial", PolynomialEvaluation.Run)
    // GVN cannot number a phi - a loop phi's operands include the value coming back round the latch,
    // which is derived from the phi itself - so congruent induction variables survive it untouched
    // after mem2reg has made the counter a phi, and before the value passes, so the integer form is
    // what they see
    .Add("demote", FloatDemotion.Run)
    .Add("phicong", PhiCongruence.Run)
    .Add("gvn", Gvn.Run)
    // tiny intrinsic expansion is deliberately after GVN: one canonical memcpy/memset call is easier
    .Add("memopt", RedundantMemory.Run)
    .Add("dse", DeadStoreElim.Run)
    // O0122 must see the original two-counter affine address arithmetic. LICM is allowed to hoist the
    // outer-counter part of an inner-loop address into its preheader, which is profitable but turns a
    // rectangular affine expression into a loop-local pointer root this first interchange slice quite
    // correctly refuses. Interchange therefore goes immediately before LICM.
    .Add("interchange", LoopInterchange.Run)
    .Add("licm", Licm.Run)
    // Exact reciprocal reuse runs after LICM, so an invariant divisor that is already representable as
    // a constant has reached the place where the repeated divisions are visible together.
    .Add("reciprocal-reuse", ReciprocalSequenceReuse.Run)
    // AFTER licm, and that ordering is the whole composition: `IF mode THEN` inside a loop lowers to
    // a COMPARE computed in the loop, and a condition defined inside the region cannot be specialized
    // by cloning - each clone gets its own copy of the compare, so binding the original to a constant
    // reaches nothing. LICM hoists it out first, which is what makes the value substitutable.
    .Add("unswitch", LoopUnswitch.Run)
    .Add("dce", Dce.Run)
    // AFTER dce: IntegerRecovery leaves the float-shaped arithmetic it replaced standing beside the
    // integer form, and until that shadow is collected the accumulator still has a reader inside the
    // loop - which is exactly the condition this pass requires to be absent
    .Add("closed-form", RecurrenceClosedForm.Run)
    // and AFTER closed-form, which is what empties the loop it deletes: the accumulator's final value
    // moves to the exit block, the counter is left turning for nobody, and only then is there nothing
    // inside anyone reads
    .AddWhen(optimizeForSpeed, "deadloop", DeadLoopElimination.Run)
    .Add("ifconv", IfConversion.Run)
    .Add("simplifycfg", SimplifyCfg.Run)
    // AFTER simplifycfg, which is what puts a self-call and its return next to each other, and after
    // mem2reg, without which the parameters are still allocas and there is nothing to phi. It runs in
    // the pipeline rather than beside it so that the sweep FOLLOWING the inliner sees it: mutual
    // recursion is inlined into self-recursion first, and this is what then turns it into a loop.
    .Add("tailrec", TailRecursion.Run)
    // FunctionSummaries.RemoveDeadPureCalls deliberately does NOT run here. The analysis is right and
    // the removal is sound - a call to a body that writes nothing, whose result nothing reads, is not
    // observable - but DIFF113 declares `SUB Opaque(v&)` with an EMPTY body precisely to be an
    // optimization barrier, and dropping the call hands the direct emitter's own optimizer code it
    // could not previously see through. What it then does with it differs from the original, which is
    // a finding about that optimizer and not about this pass. Until that is chased down, the summaries
    // are available to callers and this consumer is off.
    // SPEED inlining is a module pass so it can see the call graph after the first function fixpoint;
    // every successful inline immediately triggers another function sweep over the exposed body.
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "inline-speed",
      module => Inliner.Run(module, optimizeForSpeed: true))
    // The advanced data/search passes run before the string passes: searches need the original static
    // table shape, bitset packing needs whole-module escape information, and generated tables must
    // exist before the ordinary global cleanup gets a chance to remove what became unused.
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "libcalls", LibraryCallRecognition.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "static-search", StaticSearchRecognition.Run)
    .AddModulePassWhen(includeModulePasses, "bitsets", BitsetSubstitution.Run)
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "lutgen", LookupTableGeneration.Run)
    // O0343 shares the existing integer range lattice. A discrete integer-backed FP domain becomes a
    // typed table only when the backend advertises that storage capability; narrow continuous domains
    // can still become Taylor/Horner kernels under SPEED.
    .AddModulePassWhen(includeModulePasses && optimizeForSpeed, "fpdomain",
      module => FpDomainSpecialization.Run(module, enableFpLookupTables))
    .AddModulePassWhen(includeModulePasses, "lutelim", LookupTableElimination.Run)
    // The string passes are module passes because they mint module-level things - a runtime
    // declaration, a pooled literal - which a function pass has no handle on. They run last, after
    // the value passes have folded whatever the arguments were going to fold into.
    .AddModulePassWhen(includeModulePasses, "strfold", StringConstantFold.Run)
    .AddModulePassWhen(includeModulePasses, "strchain", StringConcatChain.Run)
    .AddModulePassWhen(includeModulePasses, "strappend", StringAppendInPlace.Run)
    // O0353 consumes the exact-trip append shape produced immediately above and batches its suffix
    // into REPEAT$ + one concatenation in the preheader, so no per-iteration capacity check remains.
    .AddModulePassWhen(includeModulePasses, "strcapacity", StringCapacityHoisting.Run)
    .AddModulePassWhen(includeModulePasses, "strbyte", StringByteRead.Run)
    .AddModulePassWhen(includeModulePasses, "strcmpeq", StringCompareEquality.Run)
    .AddModulePassWhen(includeModulePasses, "strempty", StringEmptinessTest.Run)
    .AddModulePassWhen(includeModulePasses, "readonly-globals", ReadOnlyGlobals.Run)
    .AddModulePassWhen(includeModulePasses, "localize-globals", LocalizeGlobals.Run)
    .AddModulePassWhen(includeModulePasses, "ipconstprop", IpConstantProp.Run);
}
