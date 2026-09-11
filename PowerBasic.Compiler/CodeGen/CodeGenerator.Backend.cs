using System.Linq;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // eligible functions compiled by the x86-16 back end, with their selected+scheduled machine IR,
  // register allocation, and the middle-end proof that no fixed local frame storage survived.
  // null until first queried. Empty unless UseExperimentalBackend.
  private Dictionary<ProcedureSymbol, (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc, bool ElideFrame)>? _backendProcs;

  /// <summary>
  /// What the x86-16 selector is compiling for: the instruction set the directives declared and the
  /// objective they asked for. It is assembled here rather than passed piecemeal because both answers
  /// have to match the direct emitter's exactly - the two paths emit into ONE image, so a routed
  /// function assuming a 386 while a directly-emitted one does not is a program with two targets in it.
  /// </summary>
  private Backend.SelectionTarget SelectionTarget => new(
    Optimize: this.Optimize,
    OptimizeSpeed: this.OptimizeSpeed, OptimizeSize: this.OptimizeSize,
    Cost: this.SelectionCost, CpuLevel: this._rt.Target.CpuLevel);

  /// <summary>The module body compiled by the x86-16 back end, when the whole of it selects and allocates.</summary>
  private (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)? _backendMain;

  // every procedure (and the module body) the routing considered and did not take, with the reason
  // the routing itself gave. Filled by BackendProcs/BackendMain as they decide; see BackendDeclines.
  private readonly List<(string Name, string Reason)> _backendDeclines = [];

  private bool _backendMainKnown;

  // the IR module the routed functions came from - a back-end reference to a string literal names the
  // IR's global (".str0"), and the bytes behind it are what map it onto this codegen's literal pool
  private IrModule? _backendModule;

  /// <summary>
  /// How a type reads in a decline message. The census ranks the remaining work by these names, so
  /// they are the SOURCE spellings rather than the class names of the type model.
  /// </summary>
  private static string DescribeType(PbType type) => type switch {
    ScalarType s => s.Kind.ToString().ToUpperInvariant(),
    StringType => "STRING",
    FixedStringType => "STRING * n",
    AsciizType => "ASCIIZ * n",
    FlexType => "FLEX",
    BcdType b => b.IsFixedPoint ? "FIX" : "BCD",
    WideIntType w => $"INT{w.ByteSize * 8}",
    PointerType => "pointer",
    ProcPtrType => "delegate",
    UdtType u => u.IsUnion ? "UNION" : "TYPE",
    ArrayType => "array",
    MbfType => "MBF float",
    AnyType => "ANY",
    _ => type.GetType().Name,
  };

  /// <summary>
  /// Why <paramref name="proc"/> may not be OFFERED to the x86-16 back end - the SHAPE test that runs
  /// before the lowering's verdict is consulted at all - or null when it is eligible.
  ///
  /// <para>
  /// This is a named function rather than the chain of <c>continue</c>s it used to be inside
  /// <see cref="BackendProcs"/>, because the coverage census has to be able to ASK it. A procedure
  /// this filter rejects reaches neither selection nor allocation, so it appears in neither of their
  /// histograms - and a census built on those alone counts it as neither a success nor a decline.
  /// That is how a coverage number reaches 262/262 while whole constructs (a QUAD parameter, a BYTE
  /// one, a string one) silently fall back to the direct emitter. After <c>CodeGen/</c> is retired
  /// there is no fallback, so every reason below is a compile failure in waiting; the census counts
  /// them as declines for exactly that reason.
  /// </para>
  /// </summary>
  public static string? BackendFilterReason(ProcedureSymbol proc) {
    if (proc.IsExternal || proc.Body is null)
      return "filter: external declaration - there is no body here to route";
    // Procedure-local ON ERROR / RESUME / TRY no longer changes eligibility. The lowering already
    // emits the handler intrinsics and selection expands them inline; ProcedureErrorHandlerPreservation
    // adds the one ABI rule the module body does not need - save the caller's handler triple on entry
    // and restore it before each return.
    return BackendAbiReason(proc);
  }

  /// <summary>
  /// Why a procedure definition cannot use the routed frame ABI, or null when it can. All near
  /// conventions are supported: BASIC/PASCAL push left-to-right, CDECL/STDCALL push right-to-left,
  /// CDECL is caller-clean, and FASTCALL/WATCALL take their leading arguments in registers which the
  /// routed prologue pushes into the negative frame cells LayoutFrame assigned them.
  ///
  /// <para>
  /// A register convention keeps the same word-sized restriction the CALL side has. That is not a
  /// routing limitation: the direct emitter refuses a multiword register argument too, because the
  /// per-compiler rules for splitting one across a register pair differ between FASTCALL and WATCALL
  /// and neither is modelled. Sharing <see cref="HasUnsupportedRegisterParam"/> with the call side is
  /// what keeps a definition and its call sites from disagreeing about which shapes exist.
  /// </para>
  /// </summary>
  private static string? BackendAbiReason(ProcedureSymbol proc) {
    if (HasUnsupportedRegisterParam(proc))
      return $"filter: {proc.CallConv} register-convention arguments must be word-sized";
    return BackendAbiShapeReason(proc);
  }

  /// <summary>
  /// Why a call site cannot use a declared external ABI. Every near convention is selectable for
  /// its implemented value shapes; register conventions deliberately remain limited to one-word
  /// arguments until their compiler-specific pair/float allocation rules are modelled.
  /// </summary>
  private static string? BackendCallAbiReason(ProcedureSymbol proc) {
    if (HasUnsupportedRegisterParam(proc))
      return $"filter: {proc.CallConv} register-convention arguments must be word-sized";
    return BackendAbiShapeReason(proc);
  }

  private static string? BackendAbiShapeReason(ProcedureSymbol proc) {
    // a FUNCTION with no resolved return type is refused along with the rest, exactly as the pattern
    // this replaced did - `null is not ScalarType{...}` was true, and the shape has no ABI either way
    if (proc.IsFunction && (proc.ReturnType is not { } returnType || !IsBackendAbiType(returnType)))
      return $"filter: return type outside the routed ABI "
        + $"({(proc.ReturnType is null ? "unresolved" : DescribeType(proc.ReturnType))})";
    foreach (var parameter in proc.Parameters) {
      // A BYREF record crosses the ABI as exactly one near pointer. The record's layout never crosses
      // the call boundary: IrLowering binds that pointer as the caller's storage and lowers every
      // member use to ordinary typed GEP/load/store operations. BYVAL records remain fenced because
      // their required copy-in is different ABI semantics.
      if (!parameter.ByVal && parameter.Type is UdtType)
        continue;
      // Every other near BYREF argument is one pointer word too, but its pointee must be a value shape
      // the routed lowering already models. Dynamic strings use the word as a handle-cell pointer.
      if (!parameter.ByVal && !IsBackendAbiType(parameter.Type))
        return $"filter: BYREF parameter ({DescribeType(parameter.Type)})";
      if (parameter.ByVal && !IsBackendByValParameterAbiType(parameter.Type))
        return $"filter: parameter type outside the routed ABI ({DescribeType(parameter.Type)})";
    }
    return null;
  }

  /// <summary>
  /// The value shapes a routed procedure definition can receive or return: BYTE/SBYTE/INTEGER in
  /// AX (the byte forms consume/produce AL while retaining PB's word-sized stack slot), LONG in DX:AX,
  /// SINGLE/DOUBLE/EXT reals in ST(0), and a dynamic-string handle in AX. EXT arguments keep their
  /// native ten-byte TBYTE stack representation. Records are supported only BYREF (their ABI value is
  /// one near pointer). BYVAL FIX is admitted separately because its stack representation is the raw
  /// scaled i64 cell; FIX results, BCD values and array values still need routed ABI work.
  /// </summary>
  /// <summary>
  /// Value shapes a BYVAL stack parameter can carry. FIX is special: its call representation is the
  /// scaled i64 CELL, not the numeric value, so the existing qword stack transport is exactly its ABI.
  /// The callee converts that cell through rt_fix_down when the parameter is read. This deliberately
  /// does not make FIX a general result shape: returning the raw i64 would expose the scaled integer.
  /// </summary>
  private static bool IsBackendByValParameterAbiType(PbType type)
    => IsBackendAbiType(type) || type is BcdType { IsFixedPoint: true };

  private static bool IsBackendAbiType(PbType type)
    => type is ScalarType { IsFloat: false, ByteSize: 1 or 2 or 4 or 8 }
            or ScalarType { IsFloat: true, ByteSize: 4 or 8 or 10 }
            or StringType;

  /// <summary>
  /// The functions the x86-16 back end will compile in place of the direct codegen (docs/X86-BACKEND.md).
  /// A function qualifies when its ABI shape is supported and its SSA IR fully selects + allocates.
  /// Procedure-local error handling is lowered like the module body's and then wrapped by the machine
  /// ABI preservation pass so a callee cannot overwrite its caller's active handler. The back end OWNS
  /// the whole function via the IR (SSA - no shared memory cells), so it never reads an optimizer-stale
  /// cell; the function is excluded from inlining and the register-parameter convention so its emitted
  /// stack ABI matches the call sites. Gated on the opt-in flag.
  /// </summary>
  private Dictionary<ProcedureSymbol, (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc, bool ElideFrame)> BackendProcs() {
    if (this._backendProcs is not null)
      return this._backendProcs;
    this._backendProcs = new(ReferenceEqualityComparer.Instance);
    // A $COMPILE UNIT can be routed. It was excluded along with _allowExternalCalls, and the reason
    // does not hold for procedures: a unit exports its procedures with the STACK convention (they are
    // called from outside, so OptRegParm never converts them), which is exactly the ABI this back end
    // emits. Imported calls are checked individually after lowering: a linked BASIC/PASCAL
    // declaration crosses a selectable stack ABI, while a missing link input or register convention
    // declines its caller before selection.
    if (!this.UseExperimentalBackend)
      return this._backendProcs;

    var module = IrLowering.TryLowerModule(model, out var moduleDeclinedBecause);
    if (module is null) {
      // Every procedure in the program goes with it, and each is recorded rather than left out: a
      // whole-module lowering failure costs the same coverage as a procedure-by-procedure one, and
      // a census that only sees the module-level reason cannot say how much it cost.
      foreach (var proc in model.ProcedureList)
        this._backendDeclines.Add((proc.Name, "lowering: " + (moduleDeclinedBecause ?? "the module did not lower to IR")));
      return this._backendProcs;
    }
    this._backendModule = module;
    // The routed path honours the optimizer flag like every other part of the compiler. Without this
    // a --no-optimize build of a routed function was still fully optimized, which made the two builds
    // of a size comparison ONE build and made "optimizer off means vintage behaviour" - the promise
    // the historic dialects rest on - true only of the functions the back end happened not to take.
    // IrPassManager.Legalize states which passes survive the flag and why each one is not a choice.
    // O0057 proves the narrower representation; the smallest cell worth materializing is a backend
    // decision. A 386 keeps a LONG in a dword register, so narrowing it to a word costs a partial
    // register there rather than saving anything; only a 16-bit target profits from word storage.
    var narrowestStorageBits = this.Has32BitCpu ? 32 : 16;
    var pipeline = this.Optimize
      ? () => IrPassManager.Standard(this.OptimizeSpeed, arithmeticCostModel: this.SelectionCost,
          minimumIntegerStorageBits: narrowestStorageBits)
      : (Func<IrPassManager>)IrPassManager.Legalize;
    // Recovery runs BEFORE the optimizer as well as after. PB's integral arithmetic is float-shaped
    // in the IR, and constant folding on a float tree is lossy where the integer answer is not:
    // 32767 * 32767 is 1073676289, which an f32's 24-bit mantissa cannot hold, so folding it as a
    // float answered 1073676288. Recovering first lets the folding happen in integers, exactly as the
    // direct emitter's x87 temporary (64 bits of mantissa) computes it.
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    pipeline().RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);                  // again: the optimizer can expose trees the first pass could not see
    pipeline().RunOnModule(module);              // clean up the now-dead float ops

    // O0006 inlining. It runs LAST of the module-level steps and is followed by another full pass
    // sweep, because the point of inlining is not the call overhead - it is that the callee's body
    // becomes visible to the caller's optimizer, and nothing sees it until the passes run again.
    // A function whose only caller inlines it is then dead, which GlobalDce collects.
    //
    // $OPTIMIZE SIZE never inlines, and the routed half of an image may not answer the directive
    // differently from the directly-emitted half: the direct emitter declines every call site under
    // it (see the note on O6's purge in CodeGenerator.Optimize.cs, which had to stop purging a callee
    // it would no longer absorb), so a routed caller that absorbed its callee anyway would be one
    // program compiled to two objectives.
    if (this.Optimize && !this.OptimizeSize && Inliner.Run(module) > 0) {
      pipeline().RunOnModule(module);
      foreach (var f in module.Functions)
        if (!f.IsDeclaration)
          IntegerRecovery.Run(f);
      pipeline().RunOnModule(module);
    }
    // GlobalDce deliberately does NOT run here, though inlining leaves callees unreferenced and it
    // is the obvious next step. In this pipeline the IR module is not the whole program: anything
    // not routed is still emitted by the direct path, so deleting an inlined-away function from the
    // IR does not delete it from the image - it only stops it being ROUTED. Measured, it cost six
    // corpus comparisons and saved nothing. It belongs where the IR IS the program, which is what
    // pbc --emit-c and --emit-llvm are, and that is where it runs.

    // LAST of all, and after every other pass has run: a SELECT CASE that survived as a chain of
    // compares becomes one IrSwitch, which is the only form the selector can turn into a table, a hash
    // or a mask. It runs here rather than inside the standard pipeline because it is the shape the
    // x86-16 dispatch selection consumes, and because it wants the chain in its FINAL form - SCCP may
    // have folded arms away and the inliner may have brought new ones in. SimplifyCfg then collects the
    // now-unreachable remains of the chain and Dce the compares that fed it.
    if (this.Optimize)
      foreach (var f in module.Functions)
        if (!f.IsDeclaration && SwitchFormation.Run(f) > 0) {
          SimplifyCfg.Run(f);
          Dce.Run(f);
        }

    // O0287 runs here, not inside the standard pipeline, because what it produces is x86-16 shaped
    // rather than target-neutral: a dynamic string is a runtime HANDLE, and the raw-print ABI this
    // pass rewrites to takes a DS offset (RuntimeAbi's ArgKind.Offset), which is why it has to stage
    // the SS frame object through a module-level buffer. On a hosted target that staging copy is pure
    // cost, and in the IR->BASIC writer it is a frame object with no name in the language. It wants
    // the canonical bounded builders, so it goes after the string canonicalizers have all run, and
    // before O0339 so the two copies it mints are specialized like any other small transfer.
    if (this.Optimize)
      StringStackPromotion.Run(module);

    // O0339 runs here rather than inside the standard pipeline for the same reason
    // SwitchFormation does: it wants the FINAL shape. Expanding a tiny memcpy into byte
    // loads and stores hides the aggregate behind it from scalar replacement, which would
    // otherwise delete the copy and its storage outright - a strictly better answer than
    // open-coding it. Once the optimizer has had every chance at the copy, whatever is left
    // is a real transfer worth specializing.
    if (this.Optimize)
      foreach (var f in module.Functions)
        if (!f.IsDeclaration)
          MemoryRoutineSpecialization.Run(f, this.Cost);

    // O0284 on native x86 uses ABI-preserving entry thunks. The source-visible procedures keep their
    // original signatures while private helpers carry the one varying context parameter.
    this.PrepareBackendSemanticMerges(module);

    var byName = new Dictionary<string, IrFunction>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        byName[f.Name] = f;

    // Middle-end generated definitions have no ProcedureSymbol. Select/allocate them from their IR
    // signature and definition ABI now; source/generated call-graph pruning below decides whether the
    // provisional bodies can actually coexist with the direct fallback.
    this.PrepareBackendGenerated(module);

    var candidates = new List<(ProcedureSymbol Proc, IrFunction Fn, MFunction Machine)>();
    foreach (var proc in model.ProcedureList) {
      // The filter admits a SHAPE the ABI can express; whether the body can be compiled at all is the
      // selector's question, and it declines what it cannot do. It used to demand a signed 16-bit
      // function with signed 16-bit parameters - the truth when the back end knew only integers. It
      // now returns LONGs in DX:AX and reals on ST(0), and a SUB returns nothing.
      //
      // A local ARRAY used to keep a procedure out. The exclusion was bought by CODEGEN.BAS printing
      // "accumulate-32283" where the direct emitter prints "accumulate 3", and it was blamed on the
      // frame layout - but the frame was never the problem. Two real defects were: a multi-slot
      // alloca pointed at the TOP of its block rather than the bottom, so element 0 sat at the block's
      // high end and every later one climbed out of the frame (see InstructionSelector.SelectAlloca);
      // and the routed prologue never zeroed the frame, which PB requires and the direct path does
      // with REP STOSW (see MachineEmitter.EmitFunction). Both are fixed, both show only on an array -
      // a scalar is one slot and is written before it is read - and the whole corpus now agrees.
      // Dynamic strings use one-word handles too. Their ownership transfers and releases are made
      // explicit by IrLowering, so the selector sees the same ordinary pointer load/store/call shapes
      // it already handles rather than having to invent a second lifetime model.
      //
      // Every one of these rejections is RECORDED rather than merely skipped. A skipped procedure
      // falls back to the direct emitter today and will be a compile failure once CodeGen/ is gone,
      // so it belongs in the same census as a selection decline - see BackendFilterReason.
      if (BackendFilterReason(proc) is { } filtered) {
        this._backendDeclines.Add((proc.Name, filtered));
        continue;
      }
      if (!byName.TryGetValue(proc.Name, out var irFn)) {
        this._backendDeclines.Add((proc.Name, module.ProcedureLoweringDeclines.TryGetValue(proc.Name, out var loweringWhy)
          ? "lowering: " + loweringWhy
          : "lowering: the IR module has no defined function of this name"));
        continue;
      }
      if (this.ExternalCalleeDecline(irFn) is { } externalDecline) {
        this._backendDeclines.Add((proc.Name, externalDecline));
        continue;
      }
      if (!this.DataGlobalsResolve(irFn, out var unaddressable)) {
        this._backendDeclines.Add((proc.Name, $"routing: global '{unaddressable}' has no cell the emitter can address"));
        continue;
      }
      if (InstructionSelector.TrySelect(irFn, out var declineReason, this.SelectionTarget) is not { } mfn) {
        this._backendDeclines.Add((proc.Name, "selection: " + (declineReason ?? "unknown")));
        continue;
      }
      // The handler itself is already machine IR at this point. What a procedure adds over main is
      // one invocation-level ABI promise: whatever handler triple its caller had armed must be back
      // when the procedure returns. Add that save/restore before scheduling and allocation so its
      // frame slot and scratch registers participate in the ordinary machine analyses.
      if (ContainsErrorHandling(proc.Body!))
        ProcedureErrorHandlerPreservation.Run(mfn);
      if (UndefinedRuntimeCallee(mfn) is { } undefined) {
        this._backendDeclines.Add((proc.Name, $"routing: calls '{undefined}', which the DOS runtime does not define"));
        continue;
      }
      candidates.Add((proc, irFn, mfn));
    }

    // A selected function may CALL another procedure, and the two sides have to agree on the ABI.
    // Stack-only conventions are represented on IrCall and selected from X86CallAbi. SPEED
    // optimization can still convert a directly-emitted procedure through OptRegParm after this set
    // is known, so an unrouted local callee must remain one of the direct-compatible conventions.
    // Generated definitions participate in the exact same reachability set: if a source caller was
    // rebound to an O0283 clone, that clone is now a real private ABI partner rather than a stranded
    // name which forces the caller back to the direct emitter.
    var routable = candidates.Select(c => c.Proc.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
    routable.UnionWith(this.BackendSemanticMergeNames);
    routable.UnionWith(this.BackendGeneratedNames);
    for (var changed = true; changed;) {
      changed = false;
      for (var i = candidates.Count - 1; i >= 0; --i) {
        if (CalleeNames(candidates[i].Fn)
            .FirstOrDefault(name => !routable.Contains(name) && !this.CanCallDirectCallee(name))
            is not { } stranded)
          continue;
        this._backendDeclines.Add((candidates[i].Proc.Name, $"routing: calls '{stranded}', which is not routed"));
        routable.Remove(candidates[i].Proc.Name);
        candidates.RemoveAt(i);
        changed = true;
      }
    }

    foreach (var (proc, irFn, mfn) in candidates) {
      MachineScheduler.Schedule(mfn, this.SelectionTarget);             // schedule first, then allocate the final order
      if (LinearScanAllocator.Allocate(mfn, this.SelectionTarget, out var noRegisters) is not { } alloc) {
        this._backendDeclines.Add((proc.Name, "allocation: " + (noRegisters ?? "unknown")));
        continue;                                 // a value live across a CALL has no register - decline
      }
      // O0070 is optimizer-gated here, after the last middle-end sweep. The IR proof deliberately
      // says nothing about the ABI or future spills; MachineEmitter re-checks both against the final
      // machine function before actually omitting BP.
      this._backendProcs[proc] = (mfn, alloc, this.Optimize && FrameElision.IsCandidate(irFn));
    }

    // An allocation failure can strand a source caller, and a removed source callee can strand an
    // O0284 helper. Conversely removing that helper strands its entry thunks. An O0283 generated
    // definition is stranded by the same edges, in both directions: it needs its source definition and
    // every defined callee routed, and dropping it strands whichever caller was rebound onto it.
    // Settle all three sets together, one round catching the reverse edge of the last.
    for (var changed = true; changed;) {
      changed = this.PruneBackendSemanticMerges();
      foreach (var (proc, fn, _) in candidates)
        if (this._backendProcs.ContainsKey(proc)
            && CalleeNames(fn).FirstOrDefault(name =>
              !this.BackendNameIsRouted(name) && !this.CanCallDirectCallee(name)) is { } stranded) {
          this._backendDeclines.Add((proc.Name, $"routing: calls '{stranded}', which is not routed"));
          this._backendProcs.Remove(proc);
          changed = true;
        }
      changed |= this.PruneBackendGenerated(module);
    }

    return this._backendProcs;
  }

  /// <summary>
  /// The module body, compiled by the x86-16 back end - the step from "the back end compiles some
  /// functions" to "the back end compiles a whole program". It is the same pipeline every routed
  /// procedure goes through, with three differences that all follow from main not being a procedure:
  /// it takes no arguments, it has no caller to RET to (it falls into the runtime's exit), and it is
  /// not in <c>ProcedureList</c>, so the routing has to look it up by name.
  ///
  /// Under SPEED optimization, everything it calls must itself be routed, for the ABI reason the
  /// procedure fixpoint already covers: <c>OptRegParm</c> may convert a direct procedure to registers.
  /// Otherwise a locally defined BASIC/PASCAL callee keeps the same stack ABI and may remain on the
  /// direct emitter. CHAIN still disqualifies main outright.
  /// </summary>
  private (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)? BackendMain() {
    if (this._backendMainKnown)
      return this._backendMain;
    var answer = this.RouteMain();
    // The DATA invariant, checked where the last routing decision has been taken and not before.
    // BackendOwnsData grants the pool optimistically when more than one function reads from it,
    // because whether they all route is not knowable until selection and allocation have had their
    // say - and refusing in advance is what used to decline the very functions whose declining was
    // the only conflict. If the readers ended up SPLIT, the whole routing is decided again with the
    // pool left to the direct emitter, which is the state that was assumed before. Nothing has been
    // emitted at this point: the routing resolves data cells in probe mode, and the labels and bytes
    // are minted by DataCellOf during emission, which happens after this returns.
    // The same bargain is struck for a SHARED dynamic array's descriptor, and it is checked here for
    // the same reason: the routed cells and the direct emitter's packed block are two descriptions of
    // one array, so a split set of users would have a REDIM on one side and a UBOUND on the other.
    var dataSplit = !this._backendDataOwnershipDenied && !this.DataReadersRouteTogether();
    var dynSplit = !this._backendDynArrayOwnershipDenied && !this.SharedDynArrayUsersRouteTogether();
    if (!dataSplit && !dynSplit)
      return answer;
    this._backendDataOwnershipDenied |= dataSplit;
    this._backendDynArrayOwnershipDenied |= dynSplit;
    this._backendProcs = null;
    this.ResetBackendGenerated();
    this._backendModule = null;
    this._backendMain = null;
    this.ResetBackendSemanticMerges();
    this._backendDeclines.Clear();
    return this.RouteMain();
  }

  /// <summary>
  /// Whether every function that reads DATA reached the same side. A reader left on the direct
  /// emitter alongside a routed one is two cursors over one program: the IR's is an index into its
  /// own blob and <c>rt_dataptr</c> is an absolute pointer, so each would advance a cell the other
  /// never consults.
  /// </summary>
  private bool DataReadersRouteTogether() {
    if (!this.UseExperimentalBackend || this._backendProcs is null)
      return true;
    var routed = 0;
    var direct = 0;
    if (ContainsDataRead(model.MainBody)) {
      if (this._backendMain is null)
        ++direct;
      else
        ++routed;
    }
    foreach (var proc in model.ProcedureList) {
      if (proc.Body is not { } body || !ContainsDataRead(body))
        continue;
      if (this._backendProcs.ContainsKey(proc))
        ++routed;
      else
        ++direct;
    }
    return routed == 0 || direct == 0;
  }

  private (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)? RouteMain() {
    this._backendMainKnown = true;
    var routed = this.BackendProcs();               // also lowers the module and fills _backendModule
    // Error handling in main is selected inline just like it is in a procedure. The only difference
    // is the procedure boundary: ProcedureErrorHandlerPreservation saves/restores the caller's handler
    // triple there, while main has no caller and therefore needs no wrapper.
    if (!this.UseExperimentalBackend)
      return null;
    // The module body's own filter, recorded for the same reason a procedure's is: 161/161 owned
    // bodies is a claim about the bodies the routing ATTEMPTED, and a main that calls an unrouted
    // procedure inherits every blind spot the procedure filter has.
    if (this._isUnit)
      return this.DeclineMain("filter: a $COMPILE UNIT has no module body to own");
    if (this._backendModule is null)
      return this.DeclineMain("lowering: the module did not lower to IR");
    if (model.MainBody.Any(s => s is Syntax.Ast.ChainStmt))
      return this.DeclineMain("filter: CHAIN is emitted around the body by the direct path");
    if (this._backendModule.FindFunction("main") is not { IsDeclaration: false } main)
      return this.DeclineMain("lowering: the IR module has no main");
    if (CalleeNames(main).FirstOrDefault(name =>
          !this.BackendNameIsRouted(name) && !this.CanCallDirectCallee(name)) is { } stranded)
      return this.DeclineMain($"routing: calls '{stranded}', which is not routed");
    if (this.ExternalCalleeDecline(main) is { } externalDecline)
      return this.DeclineMain(externalDecline);
    if (!this.DataGlobalsResolve(main, out var unaddressable))
      return this.DeclineMain($"routing: global '{unaddressable}' has no cell the emitter can address");
    if (InstructionSelector.TrySelect(main, out var declineReason, this.SelectionTarget) is not { } machine)
      return this.DeclineMain("selection: " + (declineReason ?? "unknown"));
    if (UndefinedRuntimeCallee(machine) is { } undefined)
      return this.DeclineMain($"routing: calls '{undefined}', which the DOS runtime does not define");
    MachineScheduler.Schedule(machine, this.SelectionTarget);
    if (LinearScanAllocator.Allocate(machine, this.SelectionTarget, out var noRegisters) is not { } alloc)
      return this.DeclineMain("allocation: " + (noRegisters ?? "unknown"));
    return this._backendMain = (machine, alloc);
  }

  /// <summary>Records why the module body was not routed and answers "not routed", in one expression.</summary>
  private (MFunction, IReadOnlyDictionary<int, Reg>)? DeclineMain(string reason) {
    this._backendDeclines.Add(("main", reason));
    return null;
  }

  /// <summary>
  /// Every procedure the IR lowering produced that the back end did NOT route, with the reason the
  /// routing itself gave - "filter: ..." for a shape never offered to the selector, "selection: ..."
  /// for one the selector refused, "allocation: ..." for one that selected without allocating, and
  /// "routing: ..." for one stranded by a callee or an unaddressable symbol. The module body appears
  /// as <c>main</c>.
  ///
  /// <para>
  /// This exists so a coverage census can be a report of the PRODUCTION decision instead of a second
  /// implementation of it. A census that re-derives the routing rule measures the rule it re-derived,
  /// which is how "262/262 functions selected" came to be quoted for a back end that never attempted
  /// a QUAD, a BYTE or a string parameter at all.
  /// </para>
  /// </summary>
  public IReadOnlyList<(string Name, string Reason)> BackendDeclines {
    get {
      _ = this.BackendProcs();
      _ = this.BackendMain();
      return this._backendDeclines;
    }
  }

  /// <summary>Emits the module body from the back end, ending in the implicit END the direct path also emits.</summary>
  private void EmitBackendMain() {
    var (machine, alloc) = this._backendMain!.Value;
    MachineEmitter.EmitFunction(this._asm, machine, alloc, [], 0, this.CalleeLabel, this.DataCellOf,
      asm => {
        asm.Mov(Asm.Reg.AL, (Asm.Imm)0);
        asm.Jmp(this._rt.Exit);
      }, alignLoops: this.Optimize && this.Cost.AlignHotLoops);
    this.EmitBackendSemanticMerges();
    this.EmitBackendGeneratedFunctions();
  }

  /// <summary>
  /// The cost model the instruction selector may spend bytes against, or null to keep the compact
  /// form. It is handed over only under <c>$OPTIMIZE SPEED</c>, which is the same gate the direct
  /// emitter's own byte-for-cycles trades sit behind: the two paths emit into one image and must make
  /// the same trade, or the objective would mean one thing for a routed procedure and another for its
  /// neighbour.
  /// </summary>
  private TargetCost? SelectionCost => this.Optimize && this.OptimizeSpeed ? this.Cost : null;

  private Asm.Label? _irDataPool;
  private Asm.Label? _irDataCursor;
  private byte[]? _irDataBytes;

  /// <summary>O0287's DS staging block, minted on demand together with the byte count it asked for.</summary>
  private Asm.Label? _irPrintBuf;
  private int _irPrintBufBytes;

  /// <summary>
  /// Emits the IR's DATA pool and read cursor, when a routed function asked for them. The cursor is
  /// a DWORD because the IR types it i32 and reads it back at that width; it starts at zero, which
  /// is the INDEX of the first item rather than an address.
  /// </summary>
  private void EmitBackendDataPool(Asm.Assembler asm) {
    // One dword per shared dynamic-array descriptor field: the far pointer is a segment/offset pair
    // and both the lower bound and the extent are 32-bit, which is what the IR loads and stores
    // through them. Zeroed, because an unallocated array's descriptor reads as a null block and the
    // bounds are written before any read of them.
    foreach (var (_, label) in this._irDynCells.OrderBy(e => e.Key, System.StringComparer.Ordinal)) {
      asm.Align(2);
      asm.MarkLabel(label);
      asm.Dw(0);
      asm.Dw(0);
    }
    if (this._irDataCursor is { } cursor) {
      asm.Align(2);
      asm.MarkLabel(cursor);
      asm.Dw(0);
      asm.Dw(0);
    }
    if (this._irDataPool is { } pool) {
      asm.Align(2);
      asm.MarkLabel(pool);
      asm.Db(this._irDataBytes ?? []);
    }
    // O0287 copies a finished frame object here and prints it in the same breath, so nothing reads
    // the block before it is written; zeroed only because BSS has to be some byte.
    if (this._irPrintBuf is { } printBuf) {
      asm.Align(2);
      asm.MarkLabel(printBuf);
      asm.Db(new byte[this._irPrintBufBytes]);
    }
  }

  /// <summary>
  /// Whether the back end may own the DATA pool. The two paths keep SEPARATE pools and cursors - the
  /// IR's cursor is an INDEX into its own blob and the direct emitter's <c>rt_dataptr</c> is an
  /// absolute pointer into <c>rt_datapool</c> - so what must never happen is a program reading
  /// through BOTH: one advances while the other is consulted.
  ///
  /// <para>
  /// The rule is therefore about how many FUNCTIONS read DATA, not about which ones. With at most
  /// one reader in the whole program there is nothing to disagree with: the reader either routes and
  /// uses the IR's pair, or it does not and the IR's pair is never referenced, in which case the
  /// codegen never even mints the labels (<see cref="ResolveDataCell"/> materializes them lazily, and
  /// only for a routed reference).
  /// </para>
  /// <para>
  /// This used to be "no PROCEDURE reads DATA", which is the same rule for the case where main is the
  /// only reader and a self-defeating one everywhere else: a <c>SUB</c> containing a <c>READ</c> made
  /// <c>.data_cursor</c> unaddressable, which declined the SUB, which was the only reason the pools
  /// could have disagreed. Both the SUB and the module body were lost to a conflict neither would
  /// have had.
  /// </para>
  /// <para>
  /// With TWO or more readers the answer is granted OPTIMISTICALLY, because it cannot be taken here:
  /// it has to hold for every reader after selection and allocation have had their say, and this
  /// question is asked before them. <see cref="DataReadersRouteTogether"/> checks it once the last
  /// decision is in, and a split routing is decided again with this denied - which is the state the
  /// old rule assumed in advance.
  /// </para>
  /// </summary>
  private bool BackendOwnsData() => !this._backendDataOwnershipDenied;

  /// <summary>The IR's descriptor cells for shared dynamic arrays, one per field, minted on demand.</summary>
  private readonly Dictionary<string, Asm.Label> _irDynCells = new(System.StringComparer.Ordinal);

  /// <summary>
  /// Whether the routed path may keep its own descriptors for shared dynamic arrays - granted
  /// optimistically and withdrawn if the users turn out to be split. See
  /// <see cref="SharedDynArrayUsersRouteTogether"/>.
  /// </summary>
  private bool BackendOwnsSharedDynArrays() => !this._backendDynArrayOwnershipDenied;

  /// <summary>
  /// Set when a first routing pass left the users of a shared dynamic array split, so the second must
  /// leave its descriptor to the direct emitter. Moves only from false to true, which bounds the
  /// re-decision at one repeat.
  /// </summary>
  private bool _backendDynArrayOwnershipDenied;

  /// <summary>
  /// Whether every reader of every SHARED dynamic array ended up on the same side of the routing.
  ///
  /// <para>
  /// The routed descriptor is the IR's own cells (<c>.dyn.g.a.data</c> and friends) and the direct
  /// emitter's is its packed block; each is written by the code that allocates through it. A routed
  /// <c>REDIM</c> and a directly emitted <c>UBOUND</c> would therefore consult different descriptions
  /// of the same array, which is the very defect the old unconditional decline was protecting
  /// against - so a split set is refused here and the whole array goes back to the direct emitter.
  /// </para>
  /// <para>
  /// It is asked AFTER selection and allocation, because until those have had their say there is no
  /// answer: a function can still be dropped for a reason that has nothing to do with arrays.
  /// </para>
  /// </summary>
  private bool SharedDynArrayUsersRouteTogether() {
    if (!this.UseExperimentalBackend || this._backendProcs is null)
      return true;
    foreach (var symbol in model.ModuleVariables.Values) {
      if (symbol.Type is not ArrayType { IsDynamic: true })
        continue;
      var routed = 0;
      var direct = 0;
      if (ReferencesVariable(model.MainBody, symbol.Name)) {
        if (this._backendMain is null)
          ++direct;
        else
          ++routed;
      }
      foreach (var proc in model.ProcedureList) {
        if (proc.Body is not { } body || !ReferencesVariable(body, symbol.Name))
          continue;
        if (this._backendProcs.ContainsKey(proc))
          ++routed;
        else
          ++direct;
      }
      if (routed > 0 && direct > 0)
        return false;
    }
    return true;
  }

  /// <summary>
  /// Whether a statement list names <paramref name="name"/> anywhere inside it, at any nesting depth.
  ///
  /// It walks with <see cref="OptReachability.DescendantNodes"/> rather than naming the compound
  /// statements to descend into, for the reason <see cref="ContainsDataRead"/> records: a hand-written
  /// walk knew about IF, FOR, DO and SELECT and therefore not about TRY, and the one answer that turns
  /// a guard like this into a miscompile is a false "not used here".
  /// </summary>
  private static bool ReferencesVariable(IReadOnlyList<Syntax.Ast.Statement> body, string name) {
    bool Names(object node) => node switch {
      Syntax.Ast.NameExpr n => n.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase),
      Syntax.Ast.CallOrIndexExpr c => c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase),
      // ERASE needs no case of its own: it holds its arrays as NameExpr, which the walk already sees.
      Syntax.Ast.DimStmt d => d.Variables.Any(v => v.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)),
      _ => false,
    };
    return body.Any(statement => Names(statement) || OptReachability.DescendantNodes(statement).Any(Names));
  }

  /// <summary>
  /// Set when a first routing pass left the DATA readers split, so the second must leave the pool to
  /// the direct emitter. It only ever moves from false to true, which is what bounds the re-decision
  /// at one repeat.
  /// </summary>
  private bool _backendDataOwnershipDenied;

  /// <summary>
  /// Whether a statement list contains a <c>READ</c> or <c>RESTORE</c> anywhere inside it, at any
  /// nesting depth. It walks with <see cref="OptReachability.DescendantNodes"/> rather than by naming
  /// the compound statements it should descend into: the hand-written version knew about IF, FOR,
  /// DO and SELECT and therefore not about TRY, and a <c>READ</c> inside a <c>TRY</c> block read as a
  /// body with no DATA in it - which is the one answer that turns this guard into a miscompile.
  /// </summary>
  private static bool ContainsDataRead(IReadOnlyList<Syntax.Ast.Statement> body)
    => body.Any(statement => statement is Syntax.Ast.ReadStmt or Syntax.Ast.RestoreStmt
      || OptReachability.DescendantNodes(statement).Any(n => n is Syntax.Ast.ReadStmt or Syntax.Ast.RestoreStmt));

  /// <summary>
  /// The label a back-end-emitted CALL targets. A user procedure's label is the one the whole-program
  /// codegen bound for it; a runtime routine's is the named label the runtime marks, which is also
  /// what seeds the pb36 runtime trimmer - so a section only the routed function calls is kept.
  /// </summary>
  private Asm.Label? CalleeLabel(string name) {
    // ...but only when the runtime really has it. Asm.Lbl MINTS a label for any name, so a wrong or
    // stale RuntimeAbi row used to hand back a perfectly good Label that nothing would ever bind, and
    // the failure surfaced as "referenced but never bound" at LINK time - after every routing
    // decision, a long way from the row that caused it, and with the whole compilation to lose. The
    // probe emission the pb36 trimmer already keeps knows exactly which rt_ labels the runtime
    // defines, so the question is asked here, where the answer can still be a decline that costs one
    // function.
    if (name.StartsWith("rt_", System.StringComparison.Ordinal))
      return RuntimeTrimmer.Instance.ProviderOf.ContainsKey(name) ? this._asm.Lbl(name) : null;
    if (this.IsBackendSemanticMerge(name))
      return this._asm.Lbl(name);
    if (this.GeneratedCalleeLabel(name) is { } generated)
      return generated;
    var proc = model.ProcedureList.FirstOrDefault(p =>
      p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase) && this.BackendProcs().ContainsKey(p));
    proc ??= this.DirectCalleeWithCompatibleAbi(name);
    // ...or an EXTERNAL procedure, which has no body here to route and needs none: ProcLabelOf gives
    // it the link symbol its ALIAS names, exactly as a directly-emitted call to it would get. An
    // unoptimized local direct callee was resolved above through DirectCalleeWithCompatibleAbi.
    //
    // Only when external calls are ENABLED, though. Without that, ProcLabelOf hands back an ordinary
    // p_<name> that nothing will ever bind, and the assembler discovers it at the end - so the label
    // has to be refused here, where refusing costs one function instead of the whole compilation.
    if (proc is null && this._allowExternalCalls)
      proc = model.ProcedureList.FirstOrDefault(p =>
        p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase) && p.IsExternal);
    return proc is null ? null : this.ProcLabelOf(proc);
  }

  /// <summary>
  /// The first <c>rt_</c> routine <paramref name="machine"/> CALLs that the DOS runtime does not
  /// define, or null when every one of them exists.
  ///
  /// <para>
  /// Asked after SELECTION rather than of the IR, because the label a call carries is the selector's
  /// choice and not the IR's name: the bridge maps <c>rt_str_from_i16</c> onto the runtime's
  /// <c>rt_str_i16</c>, and the selector also names routines no IR declaration mentions at all
  /// (<c>rt_trunc</c>, <c>rt_lmul</c>, <c>rt_pow2</c>). Asking the machine code is what makes the
  /// question cover both without a second list to keep in step.
  /// </para>
  /// <para>
  /// It has to be asked HERE and not left to emission, which is what
  /// <see cref="ExternalCalleeDecline"/> already does for a user procedure - it skips <c>rt_</c> names
  /// entirely, so a stale bridge row reached <c>MachineEmitter</c>, where nothing can decline any
  /// more.
  /// </para>
  /// </summary>
  private static string? UndefinedRuntimeCallee(Backend.MFunction machine)
    => machine.AllInstructions
      .SelectMany(instruction => instruction.Operands)
      .OfType<Backend.MOperand.LabelRef>()
      .Select(label => label.Name)
      .Where(name => name.StartsWith("rt_", System.StringComparison.Ordinal))
      .Distinct(System.StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault(name => !RuntimeTrimmer.Instance.ProviderOf.ContainsKey(name));

  /// <summary>
  /// Every routine <see cref="Backend.RuntimeAbi"/> claims the DOS runtime provides and it does not.
  ///
  /// <para>
  /// Empty is the invariant, and it is exposed rather than merely asserted because the alternative
  /// place to find out is a link error inside whichever program first calls the routine - long after
  /// the routing decision, and naming a symbol rather than the table row that invented it. A fixture
  /// reading this catches a stale row the moment it is written; <see cref="CalleeLabel"/> catches the
  /// same thing at compile time and turns it into one declined function.
  /// </para>
  /// </summary>
  public static IReadOnlyList<string> UnboundRuntimeCallees =>
    [.. Backend.RuntimeAbi.Labels.Where(name => !RuntimeTrimmer.Instance.ProviderOf.ContainsKey(name))];

  /// <summary>
  /// The cell a back-end-emitted access to a module variable resolves to: exactly the one the direct
  /// emitter uses for that symbol, so the two paths address the same storage. The IR names a global
  /// <c>g.&lt;name&gt;</c> and a STATIC local <c>static.&lt;procedure&gt;.&lt;name&gt;</c>.
  /// </summary>
  private Asm.Mem? DataCellOf(string name) => this.ResolveDataCell(name, materialize: true);

  /// <summary>
  /// Whether <see cref="DataCellOf"/> would find a cell for <paramref name="name"/> - the same
  /// decision, taken at ROUTING time, where the answer can still be a decline.
  ///
  /// It runs the resolver in probe mode rather than duplicating its conditions, because the two must
  /// never drift: the whole point is that what routing admits is exactly what emission can address.
  /// Probe mode is needed because resolving MATERIALIZES - it mints the IR data pool's label, interns
  /// a string literal, adds a float to the constant pool - and a function that resolves one reference
  /// and then declines on the next must leave none of that behind in the image.
  /// </summary>
  private bool DataCellResolves(string name) => this.ResolveDataCell(name, materialize: false) is not null;

  /// <summary>A stand-in for probe mode: only its NULLNESS is ever read there.</summary>
  private static readonly Asm.Mem _ProbeCell = Asm.Mem.Word(0);

  private Asm.Mem? ResolveDataCell(string name, bool materialize) {
    if (name.StartsWith("g.", System.StringComparison.Ordinal)) {
      var sourceName = name[2..];
      if (model.ModuleVariables.TryGetValue(sourceName, out var exact))
        return this.TryDirectCell(exact);
      // IR globals use the source spelling without its type suffix for readability, while the
      // binder's module table is keyed by the canonical suffixed spelling (total%, total&, ...).
      // Resolve that spelling only when it identifies one symbol; two differently typed globals
      // with the same base name are ambiguous and must remain unroutable rather than aliasing.
      var matches = model.ModuleVariables.Values
        .Where(symbol => symbol.Name.Equals(sourceName, System.StringComparison.OrdinalIgnoreCase))
        .Take(2)
        .ToList();
      return matches.Count == 1 ? this.TryDirectCell(matches[0]) : null;
    }
    if (name.StartsWith("static.", System.StringComparison.Ordinal)) {
      VariableSymbol? match = null;
      foreach (var procedure in model.ProcedureList)
        foreach (var symbol in procedure.Variables.Values)
          if (symbol.Storage == VariableStorage.Static
              && IrLowering.StaticGlobalName(procedure, symbol).Equals(name, System.StringComparison.Ordinal)) {
            if (match is not null && !ReferenceEquals(match, symbol))
              return null;
            match = symbol;
          }
      foreach (var symbol in model.ModuleVariables.Values)
        if (symbol.Storage == VariableStorage.Static
            && IrLowering.StaticGlobalName(null, symbol).Equals(name, System.StringComparison.Ordinal)) {
          if (match is not null && !ReferenceEquals(match, symbol))
            return null;
          match = symbol;
        }
      return match is null ? null : this.TryDirectCell(match);
    }
    // The IR's own DATA pool and read cursor. The direct emitter has its own pair - rt_datapool with
    // an ABSOLUTE rt_dataptr - and these are deliberately NOT those: the IR's cursor is a
    // blob-relative INDEX, so sharing the cell would make a routed READ and a directly-emitted
    // RESTORE disagree about what the number means. Two independent pairs are only safe because
    // nothing may use both, which BackendOwnsData enforces.
    if (name is ".data" or ".data_cursor" && !this.BackendOwnsData())
      return null;                                 // a procedure the direct emitter keeps reads DATA too
    if (name == ".data" && this._backendModule?.FindGlobal(".data") is { Bytes: { } dataBytes }) {
      if (!materialize)
        return _ProbeCell;
      this._irDataPool ??= this._asm.DefineLabel("ir_datapool");
      this._irDataBytes ??= dataBytes;
      return Asm.Mem.Word(this._irDataPool);
    }
    if (name == ".data_cursor") {
      if (!materialize)
        return _ProbeCell;
      this._irDataCursor ??= this._asm.DefineLabel("ir_dataptr");
      return Asm.Mem.Word(this._irDataCursor);
    }
    // A shared dynamic array's descriptor field. Like the DATA pool these are the IR's OWN cells and
    // not the direct emitter's packed descriptor, so the two must never both be live for one array -
    // SharedDynArrayUsersRouteTogether is what enforces that, and denying it here is what makes the
    // second routing pass leave the array to the direct emitter.
    if (name.StartsWith(".dyn.", System.StringComparison.Ordinal)) {
      if (!this.BackendOwnsSharedDynArrays())
        return null;
      if (!materialize)
        return _ProbeCell;
      if (!this._irDynCells.TryGetValue(name, out var label)) {
        label = this._asm.DefineLabel("ir_" + name[1..].Replace('.', '_'));
        this._irDynCells[name] = label;
      }
      return Asm.Mem.Word(label);
    }
    // O0287's DS staging block. It is the routed path's own storage and carries no value between
    // the copy that fills it and the print that reads it, so one module-wide cell serves every
    // promotion - and its size is the one the pass asked for rather than a second constant here.
    if (name == ".o0287.printbuf") {
      if (this._backendModule?.FindGlobal(name) is not { Count: > 0 } staging)
        return null;
      if (!materialize)
        return _ProbeCell;
      this._irPrintBuf ??= this._asm.DefineLabel("ir_o0287_printbuf");
      this._irPrintBufBytes = System.Math.Max(this._irPrintBufBytes, staging.Count);
      return Asm.Mem.Word(this._irPrintBuf);
    }
    // a string constant the IR interned (".str0"): its bytes go through this codegen's own literal
    // pool, so the routed PRINT and a directly-emitted one share the identical pooled bytes
    if (name.StartsWith(".str", System.StringComparison.Ordinal)
        && this._backendModule?.FindGlobal(name) is { Bytes: { } bytes })
      return materialize
        ? Asm.Mem.Word(this.LiteralOf(System.Text.Encoding.ASCII.GetString(bytes)))
        : _ProbeCell;
    // a float literal: the back end names it by its bits, and it resolves through this codegen's own
    // constant pool - which stores every float as a qword double, whatever its source precision
    if (name.StartsWith(".fc.", System.StringComparison.Ordinal)
        && long.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber,
             System.Globalization.CultureInfo.InvariantCulture, out var bits))
      return materialize
        ? Asm.Mem.Qword(this.FloatConstOf(System.BitConverter.Int64BitsToDouble(bits)))
        : _ProbeCell;
    // a runtime data cell (rt_curout, rt_col, rt_colptr): the runtime binds these named labels, and
    // the back end addresses the very same ones the direct emitter does
    if (name.StartsWith("rt_", System.StringComparison.Ordinal))
      return Asm.Mem.Word(this._asm.Lbl(name));
    return null;   // any other synthesized IR global is not addressable here yet
  }

  /// <summary>
  /// Whether EVERY global <paramref name="fn"/> names has a cell the emitter can address. Asked at
  /// ROUTING time and not at emission, because by emission the only answer left is an exception -
  /// <see cref="DataCellOf"/> handing back null there means "the routing admitted a reference it
  /// cannot address", and a decline is what that should have been.
  ///
  /// <para>
  /// It used to ask only about the DATA pool, and the rest of the resolver's refusals reached emission
  /// and ended the compilation. The one a program can actually provoke is the ambiguous global: the IR
  /// names a module variable by its source spelling WITHOUT the type suffix, and the binder's table is
  /// keyed WITH it, so <c>DIM total%</c> beside <c>DIM total&amp;</c> gives two symbols for one
  /// <c>g.total</c>. Resolving that to either would alias two variables onto one cell, so the resolver
  /// is right to refuse - it simply had nowhere to say so. A rank-2 <c>SHARED</c> pair like that, read
  /// and written from a SUB, raised "no data cell for global 'g.total'" out of
  /// <c>MachineEmitter.ResolveData</c> in both optimizer modes.
  /// </para>
  /// </summary>
  private bool DataGlobalsResolve(IrFunction fn, out string? unaddressable) {
    unaddressable = fn.Blocks.SelectMany(b => b.Instructions)
      .SelectMany(i => i.Operands)
      .OfType<IrGlobalVariable>()
      .Select(g => g.Name)
      .Distinct(System.StringComparer.Ordinal)
      .FirstOrDefault(name => !this.DataCellResolves(name));
    return unaddressable is null;
  }

  /// <summary>
  /// Why an EXTERNAL procedure called by <paramref name="fn"/> cannot cross the routed ABI or resolve
  /// to a linker-visible label, or null when every declaration is callable.
  ///
  /// The selector routes such a call the way it routes a defined one, because an imported procedure
  /// has a source-declared signature and convention - but only the code generator knows whether link
  /// inputs are enabled and whether that ABI is selectable. A declaration that has neither a compatible
  /// call shape nor a linker-visible label declines here, before emission can fail or miscompile it.
  /// </summary>
  private string? ExternalCalleeDecline(IrFunction fn) {
    foreach (var callee in fn.Blocks.SelectMany(b => b.Instructions)
        .OfType<IrCall>()
        .Select(c => c.Callee)
        .OfType<IrFunction>()
        .Where(f => f.IsDeclaration
                    && !f.Name.StartsWith("rt_", System.StringComparison.Ordinal)
                    && !f.Name.StartsWith("llvm.", System.StringComparison.Ordinal))) {
      var external = model.ProcedureList.FirstOrDefault(p => p.IsExternal
        && p.Name.Equals(callee.Name, System.StringComparison.OrdinalIgnoreCase));
      if (external is not null && BackendCallAbiReason(external) is { } abiReason)
        return $"routing: external callee '{callee.Name}' {abiReason["filter: ".Length..]}";
      if (this.CalleeLabel(callee.Name) is null)
        return "routing: a callee has no link symbol - it is EXTERNAL, or its own body did not lower";
    }
    return null;
  }

  /// <summary>The names of the defined functions <paramref name="fn"/> calls directly (its ABI partners).</summary>
  private static IEnumerable<string> CalleeNames(IrFunction fn)
    => fn.Blocks.SelectMany(b => b.Instructions)
        .OfType<IrCall>()
        .Select(c => c.Callee)
        .OfType<IrFunction>()
        .Where(f => !f.IsDeclaration)   // a runtime routine has a fixed ABI of its own - it is not converted
        .Select(f => f.Name);

  /// <summary>
  /// Whether every defined callee uses the stack ABI emitted at this call site. Speed-optimized
  /// direct callees are excluded because <see cref="OptRegParm"/> may convert them after routing is
  /// decided; otherwise an unambiguous BASIC/PASCAL procedure remains stack-compatible.
  /// </summary>
  private bool CalleesHaveCompatibleAbi(IrFunction fn, Func<string, bool> isRouted)
    => CalleeNames(fn).All(name => isRouted(name) || this.CanCallDirectCallee(name));

  private bool CanCallDirectCallee(string name) => this.DirectCalleeWithCompatibleAbi(name) is not null;

  private ProcedureSymbol? DirectCalleeWithCompatibleAbi(string name) {
    if (this.Optimize && this.OptimizeSpeed)
      return null;
    var matches = model.ProcedureList
      .Where(proc => !proc.IsExternal && proc.Body is not null
        && proc.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
      .Take(2)
      .ToList();
    // A shared stack convention is necessary but not sufficient: the routed caller must also be able
    // to transport every argument/result VALUE shape. Otherwise a direct FIX-returning callee, for
    // example, hands back its scaled cell through an ABI the routed call cannot interpret.
    return matches.Count == 1
      && IsBackendAbiConvention(matches[0])
      && BackendAbiShapeReason(matches[0]) is null
        ? matches[0]
        : null;
  }

  /// <summary>
  /// The procedures the x86-16 back end compiled, by name. This is what a test asks instead of
  /// inferring routing from "the image changed" - the honest question is whether the back end took
  /// the function, and the answer must not depend on its output happening to differ.
  /// </summary>
  public IEnumerable<string> BackendRoutedNames =>
    this.BackendProcs().Keys.Select(p => p.Name).Concat(this.BackendMain() is null ? [] : ["main"]);

  /// <summary>True when <paramref name="proc"/> is compiled by the x86-16 back end (so it is excluded from inlining and the register-parameter convention, and emitted via the back end).</summary>
  /// <summary>
  /// Whether the back end took <paramref name="proc"/>. It settles the MODULE BODY first, which looks
  /// redundant and is not: the DATA re-decision in <see cref="BackendMain"/> can discard and recompute
  /// the whole procedure set, and this question's first caller is <c>OptRegParm</c>, which MUTATES
  /// the model's calling conventions on the strength of the answer. Recomputing after that would
  /// lower a model the first pass never saw.
  /// </summary>
  private bool IsBackendRouted(ProcedureSymbol proc) {
    if (!this.UseExperimentalBackend)
      return false;
    _ = this.BackendMain();
    return this.BackendProcs().ContainsKey(proc);
  }

  /// <summary>Emits a back-end-compiled function, eliding its BP frame only when O0070's IR and final-machine proofs both hold.</summary>
  private void EmitBackendFunction(ProcedureSymbol proc) {
    var (mfn, alloc, elideFrame) = this.BackendProcs()[proc];
    var asm = this._asm;
    var paramBytes = this.LayoutFrame(proc);       // assigns each parameter its [BP+offset]
    if (this.Optimize && this.Cpu486)
      asm.AlignCode(16);
    asm.MarkLabel(this.ProcLabelOf(proc));
    var paramOffsets = proc.Parameters.Select(p => p.Offset).ToArray();
    // Source procedures still get their public/export frame from ProcedureSymbol. Generated private
    // definitions use the equivalent IR-derived layout in CodeGenerator.BackendGenerated.cs.
    var calleeCleanupBytes = CallerCleansStack(proc) ? 0 : paramBytes;
    // paramBytes counts only the STACK parameters, so a register convention's RET n is already right:
    // its leading arguments never reached the stack, and the pushes that spilled them are discarded by
    // the epilogue's MOV SP,BP rather than popped.
    var spillRegs = ConventionRegisters(proc.CallConv)[..RegisterParamCount(proc)];
    MachineEmitter.EmitFunction(asm, mfn, alloc, paramOffsets, calleeCleanupBytes, this.CalleeLabel, this.DataCellOf,
      alignLoops: this.Optimize && this.Cost.AlignHotLoops, allowFrameElision: elideFrame, registerSpills: spillRegs);
    this.EmitBackendSemanticMerges();
    this.EmitBackendGeneratedFunctions();
  }
}
