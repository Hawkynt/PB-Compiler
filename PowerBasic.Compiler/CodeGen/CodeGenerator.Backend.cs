using System.Linq;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // eligible functions compiled by the x86-16 back end, with their selected+scheduled machine IR and
  // register allocation (computed once); null until first queried. Empty unless UseExperimentalBackend.
  private Dictionary<ProcedureSymbol, (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)>? _backendProcs;

  /// <summary>The module body compiled by the x86-16 back end, when the whole of it selects and allocates.</summary>
  private (MFunction Fn, IReadOnlyDictionary<int, Reg> Alloc)? _backendMain;

  private bool _backendMainKnown;

  // the IR module the routed functions came from - a back-end reference to a string literal names the
  // IR's global (".str0"), and the bytes behind it are what map it onto this codegen's literal pool
  private IrModule? _backendModule;

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
    // emits. An external callee is handled by the routing fixpoint already - a function may only be
    // routed if every callee is routed, so a call to an imported procedure excludes it by
    // construction rather than by this flag.
    if (!this.UseExperimentalBackend)
      return this._backendProcs;

    var module = IrLowering.TryLowerModule(model);
    if (module is null)
      return this._backendProcs;
    this._backendModule = module;
    // Recovery runs BEFORE the optimizer as well as after. PB's integral arithmetic is float-shaped
    // in the IR, and constant folding on a float tree is lossy where the integer answer is not:
    // 32767 * 32767 is 1073676289, which an f32's 24-bit mantissa cannot hold, so folding it as a
    // float answered 1073676288. Recovering first lets the folding happen in integers, exactly as the
    // direct emitter's x87 temporary (64 bits of mantissa) computes it.
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);
    IrPassManager.Standard(this.OptimizeSpeed).RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);                  // again: the optimizer can expose trees the first pass could not see
    IrPassManager.Standard(this.OptimizeSpeed).RunOnModule(module);  // clean up the now-dead float ops

    // O0006 inlining. It runs LAST of the module-level steps and is followed by another full pass
    // sweep, because the point of inlining is not the call overhead - it is that the callee's body
    // becomes visible to the caller's optimizer, and nothing sees it until the passes run again.
    // A function whose only caller inlines it is then dead, which GlobalDce collects.
    if (Inliner.Run(module) > 0) {
      IrPassManager.Standard(this.OptimizeSpeed).RunOnModule(module);
      foreach (var f in module.Functions)
        if (!f.IsDeclaration)
          IntegerRecovery.Run(f);
      IrPassManager.Standard(this.OptimizeSpeed).RunOnModule(module);
    }
    // GlobalDce deliberately does NOT run here, though inlining leaves callees unreferenced and it
    // is the obvious next step. In this pipeline the IR module is not the whole program: anything
    // not routed is still emitted by the direct path, so deleting an inlined-away function from the
    // IR does not delete it from the image - it only stops it being ROUTED. Measured, it cost six
    // corpus comparisons and saved nothing. It belongs where the IR IS the program, which is what
    // pbc --emit-c and --emit-llvm are, and that is where it runs.
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
      // Strings still stay out: they are runtime handles with ownership rules the back end does not
      // model, and the selector declines them on their own.
      if (proc.IsExternal || proc.Body is null || ContainsErrorHandling(proc.Body))
        continue;
      if (proc.IsFunction && proc.ReturnType is not ScalarType { IsFloat: false, ByteSize: 2 or 4 }
                          and not ScalarType { IsFloat: true, ByteSize: 4 or 8 })
        continue;
      if (!proc.Parameters.All(p => p.ByVal && p.Type is
            ScalarType { IsFloat: false, ByteSize: 2 or 4 }
            or ScalarType { IsFloat: true, ByteSize: 4 or 8 }))
        continue;
      if (!byName.TryGetValue(proc.Name, out var irFn) || InstructionSelector.TrySelect(irFn, this._rt.Cpu386) is not { } mfn)
        continue;
      candidates.Add((proc, irFn, mfn));
    }

    // A selected function may CALL another procedure, and the two sides have to agree on the ABI.
    // The back end emits (and expects) the stack convention, while OptRegParm may convert a
    // directly-emitted procedure to the register convention - and it decides that AFTER this set is
    // known (it skips exactly the routed procedures). The sound rule is therefore that a routed
    // function may only call routed functions: both are then excluded from the conversion by
    // construction. Dropping one can invalidate its callers, so it iterates to a fixpoint.
    var routable = candidates.Select(c => c.Proc.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
    for (var changed = true; changed;) {
      changed = false;
      for (var i = candidates.Count - 1; i >= 0; --i) {
        if (CalleeNames(candidates[i].Fn).All(routable.Contains))
          continue;
        routable.Remove(candidates[i].Proc.Name);
        candidates.RemoveAt(i);
        changed = true;
      }
    }

    foreach (var (proc, _, mfn) in candidates) {
      MachineScheduler.Schedule(mfn);             // schedule first, then allocate the final order
      if (LinearScanAllocator.Allocate(mfn) is not { } alloc)
        continue;                                 // a value live across a CALL has no register - decline
      this._backendProcs[proc] = (mfn, alloc);
    }

    // an allocation failure can strand a caller whose callee is no longer routed - re-check
    for (var changed = true; changed;) {
      changed = false;
      foreach (var (proc, fn, _) in candidates)
        if (this._backendProcs.ContainsKey(proc)
            && !CalleeNames(fn).All(n => this._backendProcs.Keys.Any(p => p.Name.Equals(n, System.StringComparison.OrdinalIgnoreCase)))) {
          this._backendProcs.Remove(proc);
          changed = true;
        }
    }

    return this._backendProcs;
  }

  /// <summary>
  /// <summary>
  /// The module body, compiled by the x86-16 back end - the step from "the back end compiles some
  /// functions" to "the back end compiles a whole program". It is the same pipeline every routed
  /// procedure goes through, with three differences that all follow from main not being a procedure:
  /// it takes no arguments, it has no caller to RET to (it falls into the runtime's exit), and it is
  /// not in <c>ProcedureList</c>, so the routing has to look it up by name.
  ///
  /// Everything it calls must itself be routed, for the ABI reason the procedure fixpoint already
  /// covers: <c>OptRegParm</c> may convert a directly-emitted procedure to the register convention,
  /// and the back end emits the stack one. Error handling and CHAIN disqualify it outright - both are
  /// emitted around the body by the direct path, not inside it.
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
    if (!this.UseExperimentalBackend || this._isUnit || this._allowExternalCalls
        || this._backendModule is null
        || model.MainBody.Any(s => s is Syntax.Ast.ChainStmt))
      return null;
    if (this._backendModule.FindFunction("main") is not { IsDeclaration: false } main)
      return null;
    if (!CalleeNames(main).All(n => routed.Keys.Any(p => p.Name.Equals(n, System.StringComparison.OrdinalIgnoreCase))))
      return null;
    if (InstructionSelector.TrySelect(main, this._rt.Cpu386) is not { } machine)
      return null;
    MachineScheduler.Schedule(machine);
    if (LinearScanAllocator.Allocate(machine) is not { } alloc)
      return null;
    return this._backendMain = (machine, alloc);
  }

  /// <summary>Emits the module body from the back end, ending in the implicit END the direct path also emits.</summary>
  private void EmitBackendMain() {
    var (machine, alloc) = this._backendMain!.Value;
    MachineEmitter.EmitFunction(this._asm, machine, alloc, [], 0, this.CalleeLabel, this.DataCellOf,
      asm => {
        asm.Mov(Asm.Reg.AL, (Asm.Imm)0);
        asm.Jmp(this._rt.Exit);
      });
  }

  /// The label a back-end-emitted CALL targets. A user procedure's label is the one the whole-program
  /// codegen bound for it; a runtime routine's is the named label the runtime marks, which is also
  /// what seeds the pb36 runtime trimmer - so a section only the routed function calls is kept.
  /// </summary>
  private Asm.Label? CalleeLabel(string name) {
    if (name.StartsWith("rt_", System.StringComparison.Ordinal))
      return this._asm.Lbl(name);
    var proc = model.ProcedureList.FirstOrDefault(p =>
      p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase) && this.BackendProcs().ContainsKey(p));
    return proc is null ? null : this.ProcLabelOf(proc);
  }

  /// <summary>
  /// The cell a back-end-emitted access to a module variable resolves to: exactly the one the direct
  /// emitter uses for that symbol, so the two paths address the same storage. The IR names a global
  /// <c>g.&lt;name&gt;</c> and a STATIC local <c>static.&lt;procedure&gt;.&lt;name&gt;</c>.
  /// </summary>
  private Asm.Mem? DataCellOf(string name) {
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
    // a string constant the IR interned (".str0"): its bytes go through this codegen's own literal
    // pool, so the routed PRINT and a directly-emitted one share the identical pooled bytes
    if (name.StartsWith(".str", System.StringComparison.Ordinal)
        && this._backendModule?.FindGlobal(name) is { Bytes: { } bytes })
      return Asm.Mem.Word(this.LiteralOf(System.Text.Encoding.ASCII.GetString(bytes)));
    // a float literal: the back end names it by its bits, and it resolves through this codegen's own
    // constant pool - which stores every float as a qword double, whatever its source precision
    if (name.StartsWith(".fc.", System.StringComparison.Ordinal)
        && long.TryParse(name[4..], System.Globalization.NumberStyles.HexNumber,
             System.Globalization.CultureInfo.InvariantCulture, out var bits))
      return Asm.Mem.Qword(this.FloatConstOf(System.BitConverter.Int64BitsToDouble(bits)));
    // a runtime data cell (rt_curout, rt_col, rt_colptr): the runtime binds these named labels, and
    // the back end addresses the very same ones the direct emitter does
    if (name.StartsWith("rt_", System.StringComparison.Ordinal))
      return Asm.Mem.Word(this._asm.Lbl(name));
    return null;   // a synthesized IR global like .data_cursor is not addressable here yet
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
    MachineEmitter.EmitFunction(asm, mfn, alloc, paramOffsets, paramBytes, this.CalleeLabel, this.DataCellOf);
  }
}
