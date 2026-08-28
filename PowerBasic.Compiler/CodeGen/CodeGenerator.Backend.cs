using System.Linq;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // eligible functions compiled by the x86-16 back end, with their selected+scheduled machine IR and
  // register allocation (computed once); null until first queried. Empty unless UseExperimentalBackend.
  private Dictionary<ProcedureSymbol, (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)>? _backendProcs;

  /// <summary>
  /// What the x86-16 selector is compiling for: the instruction set the directives declared and the
  /// objective they asked for. It is assembled here rather than passed piecemeal because both answers
  /// have to match the direct emitter's exactly - the two paths emit into ONE image, so a routed
  /// function assuming a 386 while a directly-emitted one does not is a program with two targets in it.
  /// </summary>
  private Backend.SelectionTarget SelectionTarget => new(
    Cpu386: this._rt.Cpu386, Optimize: this.Optimize,
    OptimizeSpeed: this.OptimizeSpeed, OptimizeSize: this.OptimizeSize,
    Cost: this.SelectionCost);

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
    // Error handling in a PROCEDURE, unlike in the module body: the direct path saves and restores
    // the caller's handler triple around such a body, and the routed prologue/epilogue has no
    // equivalent bookkeeping yet.
    if (ContainsErrorHandling(proc.Body))
      return "filter: error handling in a procedure body (ON ERROR / RESUME / TRY)";
    return BackendAbiReason(proc);
  }

  /// <summary>
  /// Why a procedure definition cannot use the routed BASIC/PASCAL frame ABI, or null when it can.
  /// Kept apart from <see cref="BackendFilterReason"/> because an EXTERNAL declaration has no body to
  /// route but its signature still governs a routed caller's argument order, cleanup and result.
  /// </summary>
  private static string? BackendAbiReason(ProcedureSymbol proc) {
    // The back end emits ONE ABI - left-to-right stack arguments, callee-cleans - so a procedure
    // declared WATCALL/FASTCALL/CDECL/STDCALL is not routable, and silently was. Its frame is laid
    // out for the declared convention while the routed prologue/epilogue implement the default one:
    // a register convention's parameters end up at negative offsets nothing fills, CDECL/STDCALL's
    // reversed push order swaps them, and CDECL's args get popped by both sides. See
    // IsBackendAbiConvention.
    if (!IsBackendAbiConvention(proc))
      return $"filter: calling convention outside the routed ABI ({proc.CallConv})";
    return BackendAbiShapeReason(proc);
  }

  /// <summary>
  /// Why a call site cannot use a declared external ABI. All near stack conventions are selectable;
  /// FASTCALL/WATCALL still decline until the selector stages their register arguments.
  /// </summary>
  private static string? BackendCallAbiReason(ProcedureSymbol proc) {
    if (proc.CallConv is CallConvention.Fastcall or CallConvention.Watcall)
      return $"filter: register calling convention outside the routed call ABI ({proc.CallConv})";
    return BackendAbiShapeReason(proc);
  }

  private static string? BackendAbiShapeReason(ProcedureSymbol proc) {
    // a FUNCTION with no resolved return type is refused along with the rest, exactly as the pattern
    // this replaced did - `null is not ScalarType{...}` was true, and the shape has no ABI either way
    if (proc.IsFunction && (proc.ReturnType is not { } returnType || !IsBackendAbiType(returnType)))
      return $"filter: return type outside the routed ABI "
        + $"({(proc.ReturnType is null ? "unresolved" : DescribeType(proc.ReturnType))})";
    foreach (var parameter in proc.Parameters) {
      // A near BYREF argument is always one pointer word on this ABI. The pointee still has to be a
      // value shape the selector can load and store exactly; dynamic strings now use that word as a
      // handle with ownership expressed in the IR, while records and other layout-bearing types stay
      // fenced until their own ABI work lands.
      if (!parameter.ByVal && !IsBackendAbiType(parameter.Type))
        return $"filter: BYREF parameter ({DescribeType(parameter.Type)})";
      if (parameter.ByVal && !IsBackendAbiType(parameter.Type))
        return $"filter: parameter type outside the routed ABI ({DescribeType(parameter.Type)})";
    }
    return null;
  }

  /// <summary>
  /// The value shapes the routed calling sequence can pass and return: a 16- or 32-bit integer (AX or
  /// DX:AX), a SINGLE or DOUBLE (ST(0)), and a dynamic-string handle (AX). The same shapes may be the
  /// storage behind a one-word near BYREF pointer. Everything else - QUAD and BYTE among the scalars,
  /// FIX/BCD, records and arrays - has no routed convention yet.
  /// </summary>
  private static bool IsBackendAbiType(PbType type)
    => type is ScalarType { IsFloat: false, ByteSize: 2 or 4 }
            or ScalarType { IsFloat: true, ByteSize: 4 or 8 }
            or StringType;

  /// <summary>
  /// The functions the x86-16 back end will compile in place of the direct codegen (docs/X86-BACKEND.md).
  /// A function qualifies when it is a pure INTEGER (signed-16) function with INTEGER BYVAL parameters
  /// and no error handling, and - after IntegerRecovery turns PB's float-form integer arithmetic back
  /// into integer ops - its SSA IR fully selects + allocates (which declines calls, division, float).
  /// The back end OWNS the whole function via the IR (SSA - no shared memory cells), so it never reads
  /// an optimizer-stale cell; the function is excluded from inlining and the register-parameter
  /// convention so its emitted stack ABI matches the call sites. Gated on the opt-in flag.
  /// </summary>
  private Dictionary<ProcedureSymbol, (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)> BackendProcs() {
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
    var pipeline = this.Optimize
      ? () => IrPassManager.Standard(this.OptimizeSpeed)
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

    var byName = new Dictionary<string, IrFunction>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        byName[f.Name] = f;

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
      if (UndefinedRuntimeCallee(mfn) is { } undefined) {
        this._backendDeclines.Add((proc.Name, $"routing: calls '{undefined}', which the DOS runtime does not define"));
        continue;
      }
      candidates.Add((proc, irFn, mfn));
    }

    // A selected function may CALL another procedure, and the two sides have to agree on the ABI.
    // The back end emits (and expects) the BASIC/PASCAL stack convention. SPEED optimization can
    // convert a directly-emitted procedure through OptRegParm after this set is known, so that callee
    // must route too. Otherwise an unambiguous direct BASIC/PASCAL callee remains stack-compatible.
    // Dropping one can invalidate its callers, so this iterates.
    var routable = candidates.Select(c => c.Proc.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
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

    foreach (var (proc, _, mfn) in candidates) {
      MachineScheduler.Schedule(mfn);             // schedule first, then allocate the final order
      if (LinearScanAllocator.Allocate(mfn, this.SelectionTarget, out var noRegisters) is not { } alloc) {
        this._backendDeclines.Add((proc.Name, "allocation: " + (noRegisters ?? "unknown")));
        continue;                                 // a value live across a CALL has no register - decline
      }
      this._backendProcs[proc] = (mfn, alloc);
    }

    // an allocation failure can strand a caller whose callee is no longer routed - re-check
    for (var changed = true; changed;) {
      changed = false;
      foreach (var (proc, fn, _) in candidates)
        if (this._backendProcs.ContainsKey(proc)
            && CalleeNames(fn).FirstOrDefault(name =>
              !this._backendProcs.Keys.Any(p => p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
              && !this.CanCallDirectCallee(name)) is { } stranded) {
          this._backendDeclines.Add((proc.Name, $"routing: calls '{stranded}', which is not routed"));
          this._backendProcs.Remove(proc);
          changed = true;
        }
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
    this._backendMainKnown = true;
    var routed = this.BackendProcs();               // also lowers the module and fills _backendModule
    // Error handling used to disqualify the module body outright. It no longer does: the selector
    // expands the ON ERROR intrinsics inline (arming captures the CURRENT BP/SP, so a CALL would
    // capture its own), and a handler is named by its block's offset. A PROCEDURE that arms one is
    // still excluded - the direct path additionally saves and restores the caller's handler triple
    // around such a body, and that bookkeeping has no equivalent here yet.
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
          !routed.Keys.Any(p => p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
          && !this.CanCallDirectCallee(name)) is { } stranded)
      return this.DeclineMain($"routing: calls '{stranded}', which is not routed");
    if (this.ExternalCalleeDecline(main) is { } externalDecline)
      return this.DeclineMain(externalDecline);
    if (!this.DataGlobalsResolve(main, out var unaddressable))
      return this.DeclineMain($"routing: global '{unaddressable}' has no cell the emitter can address");
    if (InstructionSelector.TrySelect(main, out var declineReason, this.SelectionTarget) is not { } machine)
      return this.DeclineMain("selection: " + (declineReason ?? "unknown"));
    if (UndefinedRuntimeCallee(machine) is { } undefined)
      return this.DeclineMain($"routing: calls '{undefined}', which the DOS runtime does not define");
    MachineScheduler.Schedule(machine);
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

  /// <summary>
  /// Emits the IR's DATA pool and read cursor, when a routed function asked for them. The cursor is
  /// a DWORD because the IR types it i32 and reads it back at that width; it starts at zero, which
  /// is the INDEX of the first item rather than an address.
  /// </summary>
  private void EmitBackendDataPool(Asm.Assembler asm) {
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
  }

  /// <summary>
  /// Whether the back end may own the DATA pool: only when no procedure the direct emitter might
  /// still compile reads from one. The two paths keep SEPARATE pools and cursors, so a program that
  /// read through both would advance one and consult the other.
  /// </summary>
  private bool BackendOwnsData()
    => model.ProcedureList.All(p => p.Body is null || !ContainsDataRead(p.Body));

  private static bool ContainsDataRead(IReadOnlyList<Syntax.Ast.Statement> body) {
    foreach (var statement in body)
      switch (statement) {
        case Syntax.Ast.ReadStmt or Syntax.Ast.RestoreStmt:
          return true;
        case Syntax.Ast.IfStmt i when ContainsDataRead(i.Then)
            || i.ElseIfs.Any(a => ContainsDataRead(a.Body)) || (i.Else is { } e && ContainsDataRead(e)):
          return true;
        case Syntax.Ast.ForStmt f when ContainsDataRead(f.Body):
          return true;
        case Syntax.Ast.DoLoopStmt d when ContainsDataRead(d.Body):
          return true;
        case Syntax.Ast.SelectStmt sel when sel.Arms.Any(a => ContainsDataRead(a.Body)):
          return true;
      }
    return false;
  }

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
    return matches.Count == 1 && IsBackendAbiConvention(matches[0]) ? matches[0] : null;
  }

  /// <summary>
  /// The procedures the x86-16 back end compiled, by name. This is what a test asks instead of
  /// inferring routing from "the image changed" - the honest question is whether the back end took
  /// the function, and the answer must not depend on its output happening to differ.
  /// </summary>
  public IEnumerable<string> BackendRoutedNames =>
    this.BackendProcs().Keys.Select(p => p.Name).Concat(this.BackendMain() is null ? [] : ["main"]);

  /// <summary>True when <paramref name="proc"/> is compiled by the x86-16 back end (so it is excluded from inlining and the register-parameter convention, and emitted via the back end).</summary>
  private bool IsBackendRouted(ProcedureSymbol proc) => this.UseExperimentalBackend && this.BackendProcs().ContainsKey(proc);

  /// <summary>Emits a back-end-compiled function: its standard stack-ABI prologue/body/epilogue from the selected, allocated machine IR.</summary>
  private void EmitBackendFunction(ProcedureSymbol proc) {
    var (mfn, alloc) = this.BackendProcs()[proc];
    var asm = this._asm;
    var paramBytes = this.LayoutFrame(proc);       // assigns each parameter its [BP+offset] and returns the byte count to clean
    if (this.Optimize && this.Cpu486)
      asm.AlignCode(16);
    asm.MarkLabel(this.ProcLabelOf(proc));
    var paramOffsets = proc.Parameters.Select(p => p.Offset).ToArray();
    // a CALL needs the label the whole-program codegen bound for the callee (procedure labels live in
    // a different registry than Assembler.Lbl); the routing guarantees every callee is itself routed
    MachineEmitter.EmitFunction(asm, mfn, alloc, paramOffsets, paramBytes, this.CalleeLabel, this.DataCellOf,
      alignLoops: this.Optimize && this.Cost.AlignHotLoops);
  }
}
