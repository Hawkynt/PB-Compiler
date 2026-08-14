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
  /// What the x86-16 back end needs run before it can compile a function AT ALL, as opposed to what
  /// makes the result better. This is the pipeline a <c>--no-optimize</c> routed build gets, so that
  /// "the optimizer is off" is true of the routed path as well as the direct one.
  ///
  /// <para>
  /// Four passes, and each is here because a CONSUMER demands the form it produces rather than
  /// because it removes work. Each one was also LEFT OUT and the corpus re-measured, because an
  /// argument for a pass is not evidence that anything needs it - the numbers below are functions
  /// that select and allocate, out of 262, with that one pass removed (258 with all four):
  /// </para>
  /// <list type="bullet">
  ///   <item><b>mem2reg</b> - SSA construction. It is a change of REPRESENTATION, not a strength
  ///     reduction: the selector mints one virtual register per value and the allocator reasons over
  ///     live intervals of those registers, and a program whose values all live in allocas has no
  ///     intervals to reason about. It also makes the pipeline CHEAPER rather than dearer here, which
  ///     is the tell - the raw lowering of a corpus main body carries 314 virtual registers and
  ///     promoting brings it to 244, and the corpus takes three times as long to compile without it.
  ///     <b>250 without.</b></item>
  ///   <item><b>instcombine</b> - canonicalization, and the one that had to be MEASURED rather than
  ///     argued. The selector is a pattern matcher, and its patterns are written against the canonical
  ///     form: <see cref="Backend.InstructionSelector"/>'s <c>WordSizedRange</c> walks pure dataflow
  ///     through five opcodes to decide whether a 32-bit value fits one word, and until the casts PB's
  ///     float-shaped arithmetic leaves behind are folded together there is no such chain to walk.
  ///     Without it <c>CHR$(64 + i%)</c> declines with "rt_str_chr takes a 32-bit value in a word
  ///     register" and the whole module body goes back to the direct emitter. It is the least tidy
  ///     member of the set - it folds constants too, which is optimization by any reading - but the
  ///     alternative is a back end whose COVERAGE depends on the optimizer, which
  ///     <c>BackendWordNarrowingTests</c> exists to rule out. <b>234 without.</b></item>
  ///   <item><b>dce</b> - the second half of <see cref="IntegerRecovery"/>, which the routed path runs
  ///     unconditionally. That rewrite leaves the float-shaped arithmetic it replaced STANDING beside
  ///     the integer form it minted, so without a collector every recovered expression exists twice
  ///     and the second copy is carried all the way into instruction selection. It is here as the
  ///     other half of a transform, not as an optimization in its own right - and it is the weakest
  ///     member of the set on the numbers, worth one module body. <b>257 without.</b></item>
  ///   <item><b>simplifycfg</b> - and this one is here only BECAUSE instcombine is, which is worth
  ///     stating because the reverse was assumed first and measured false. An unreachable block costs
  ///     the selector nothing (it orders blocks by reverse postorder and leaves the rest where the
  ///     function lists them), so on the raw lowering there is nothing to clean up. Once instcombine
  ///     folds a comparison to a constant, though, the branch reading it is <c>condbr true, A, B</c> -
  ///     and the selector has no encoding for a branch on something that is not a register, so it
  ///     declines the whole function. It did, 44 times over the corpus. Collapsing such a branch is
  ///     what SimplifyCfg does first. <b>214 without.</b></item>
  /// </list>
  ///
  /// <para>
  /// <b><c>sroa</c> + a second <c>mem2reg</c> were here and are not, which is the point of measuring.</b>
  /// The argument for them is the same one mem2reg wins on - an alloca the lowering emits for a
  /// fixed-size aggregate is one memory object, and splitting it is what lets the elements be promoted
  /// at all - and it is a perfectly good argument that buys <b>nothing</b>: 258 and 157 with them,
  /// 258 and 157 without, to the function. Running an optimization with the optimizer off wants better
  /// evidence than a story.
  /// </para>
  /// <para>
  /// Everything else in <see cref="Standard"/> is optimization and is off: unrolling, sccp, correlate,
  /// rangefold, sroa, mem2reg2, reassociate, demote, phicong, gvn, memopt, dse, licm, unswitch,
  /// closed-form, deadloop, ifconv, tailrec and the string/global module passes. So are the two steps
  /// the caller runs around the pipeline - <c>Inliner</c> and <c>SwitchFormation</c>.
  /// </para>
  /// </summary>
  public static IrPassManager Legalize() => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    .Add("instcombine", InstCombine.Run)
    .Add("dce", Dce.Run)
    .Add("simplifycfg", SimplifyCfg.Run);

  /// <summary>
  /// The default optimization pipeline: promote memory to registers, then iterate
  /// simplification, conditional constant propagation, value numbering and dead-code
  /// elimination to a fixpoint.
  ///
  /// <para>
  /// <paramref name="optimizeForSpeed"/> reflects <c>$OPTIMIZE SPEED</c>. Only one pass reads it and
  /// the reason is specific to that pass rather than to a size/speed trade: a loop that does nothing
  /// can be a delay loop, and <see cref="DeadLoopElimination"/> is the one transform here whose
  /// correctness argument rests on the author not having meant it.
  /// </para>
  /// </summary>
  public static IrPassManager Standard(bool optimizeForSpeed = false) => new IrPassManager()
    .Add("mem2reg", Mem2Reg.Run)
    // unrolling goes early, right after values reach SSA: a fully unrolled loop turns its counter
    // into a constant in every copy, which is what gives the rest of the pipeline something to fold
    .Add("unroll", LoopUnroll.Run)
    .Add("instcombine", InstCombine.Run)
    .Add("sccp", Sccp.Run)
    .Add("correlate", CorrelatedValueProp.Run)
    // AFTER sccp and correlate, and the order is the whole of it: the range analysis reasons about
    // what an expression CAN be, so it wants the values that are already known to be one thing folded
    // in first - a bounds check against a subscript sccp has resolved is not a range question at all.
    // What is left after those two is the class this answers: a loop counter, an IF-joined variable,
    // a masked or divided index - none of which is a constant, and all of which are bounded.
    .Add("rangefold", RangeCheckElim.Run)
    // after SCCP, because a subscript is only constant once the index arithmetic has folded - and
    // before the value passes, so the elements it exposes get propagated like any other value
    .Add("sroa", ScalarReplaceArrays.Run)
    .Add("mem2reg2", Mem2Reg.Run)
    // canonicalizes associative chains so GVN hashes two equal expressions the same way; it must
    // come after SCCP (which supplies the constants it folds together) and before GVN (which is the
    // pass that benefits)
    .Add("reassociate", Reassociate.Run)
    // GVN cannot number a phi - a loop phi's operands include the value coming back round the latch,
    // which is derived from the phi itself - so congruent induction variables survive it untouched
    // after mem2reg has made the counter a phi, and before the value passes, so the integer form is
    // what they see
    .Add("demote", FloatDemotion.Run)
    .Add("phicong", PhiCongruence.Run)
    .Add("gvn", Gvn.Run)
    .Add("memopt", RedundantMemory.Run)
    .Add("dse", DeadStoreElim.Run)
    .Add("licm", Licm.Run)
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
    // The string passes are module passes because they mint module-level things - a runtime
    // declaration, a pooled literal - which a function pass has no handle on. They run last, after
    // the value passes have folded whatever the arguments were going to fold into.
    .AddModulePass("strfold", StringConstantFold.Run)
    .AddModulePass("strchain", StringConcatChain.Run)
    .AddModulePass("strappend", StringAppendInPlace.Run)
    .AddModulePass("strbyte", StringByteRead.Run)
    .AddModulePass("strcmpeq", StringCompareEquality.Run)
    .AddModulePass("strempty", StringEmptinessTest.Run)
    .AddModulePass("readonly-globals", ReadOnlyGlobals.Run)
    .AddModulePass("localize-globals", LocalizeGlobals.Run)
    .AddModulePass("ipconstprop", IpConstantProp.Run);
}
