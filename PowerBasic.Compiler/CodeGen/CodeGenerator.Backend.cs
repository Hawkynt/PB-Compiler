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
    if (!this.UseExperimentalBackend || this._isUnit || this._allowExternalCalls)
      return this._backendProcs;

    var module = IrLowering.TryLowerModule(model);
    if (module is null)
      return this._backendProcs;
    this._backendModule = module;
    IrPassManager.Standard().RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);                  // PB integral +/-/* are float in the IR; recover the integer form
    IrPassManager.Standard().RunOnModule(module);  // clean up the now-dead float ops
    var byName = new Dictionary<string, IrFunction>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        byName[f.Name] = f;

    var candidates = new List<(ProcedureSymbol Proc, IrFunction Fn, MFunction Machine)>();
    foreach (var proc in model.ProcedureList) {
      if (!proc.IsFunction || proc.IsExternal
          || proc.ReturnType is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false }
          || !proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { ByteSize: 2, IsFloat: false })
          || proc.Body is null || ContainsErrorHandling(proc.Body))
        continue;
      if (!byName.TryGetValue(proc.Name, out var irFn) || InstructionSelector.TrySelect(irFn) is not { } mfn)
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
  /// <c>g.&lt;name&gt;</c> and a STATIC local <c>static.&lt;name&gt;</c>.
  /// </summary>
  private Asm.Mem? DataCellOf(string name) {
    if (name.StartsWith("g.", System.StringComparison.Ordinal))
      return model.ModuleVariables.TryGetValue(name[2..], out var symbol)
        ? this.TryDirectCell(symbol)
        : null;
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
    return null;   // a STATIC local, or a synthesized IR global like .data_cursor - not addressable here yet
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
  public IEnumerable<string> BackendRoutedNames => this.BackendProcs().Keys.Select(p => p.Name);

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
