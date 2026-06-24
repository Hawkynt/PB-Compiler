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
    IrPassManager.Standard().RunOnModule(module);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        IntegerRecovery.Run(f);                  // PB integral +/-/* are float in the IR; recover the integer form
    IrPassManager.Standard().RunOnModule(module);  // clean up the now-dead float ops
    var byName = new Dictionary<string, IrFunction>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var f in module.Functions)
      if (!f.IsDeclaration)
        byName[f.Name] = f;

    foreach (var proc in model.ProcedureList) {
      if (!proc.IsFunction || proc.IsExternal
          || proc.ReturnType is not ScalarType { ByteSize: 2, Signed: true, IsFloat: false }
          || !proc.Parameters.All(p => p.ByVal && p.Type is ScalarType { ByteSize: 2, IsFloat: false })
          || proc.Body is null || ContainsErrorHandling(proc.Body))
        continue;
      if (!byName.TryGetValue(proc.Name, out var irFn) || InstructionSelector.TrySelect(irFn) is not { } mfn)
        continue;
      MachineScheduler.Schedule(mfn);             // schedule first, then allocate the final order
      if (LinearScanAllocator.Allocate(mfn) is not { } alloc)
        continue;
      this._backendProcs[proc] = (mfn, alloc);
    }

    return this._backendProcs;
  }

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
    MachineEmitter.EmitFunction(asm, mfn, alloc, paramOffsets, paramBytes);
  }
}
