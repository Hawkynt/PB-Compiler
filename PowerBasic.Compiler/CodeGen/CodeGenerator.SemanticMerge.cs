using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private sealed record BackendSemanticMerge(
    IrFunction Function,
    MFunction Machine,
    IReadOnlyDictionary<int, Reg> Allocation,
    int[] ParameterOffsets,
    int ParameterBytes,
    bool ElideFrame);

  private Dictionary<string, BackendSemanticMerge>? _backendSemanticMerges;
  private bool _backendSemanticMergesEmitted;

  /// <summary>
  /// O0284's native-x86 bridge. The hybrid backend cannot change a source-visible procedure ABI because
  /// a caller may still be emitted by the legacy path. The IR pass therefore keeps each original entry
  /// as a thunk and places the parameterized body in a private synthetic helper. Only that helper uses
  /// the new BASIC stack ABI, and it is selected/allocated here exactly like any other routed body.
  /// </summary>
  private void PrepareBackendSemanticMerges(IrModule module) {
    this._backendSemanticMerges = new(StringComparer.OrdinalIgnoreCase);
    this._backendSemanticMergesEmitted = false;
    if (!this.UseExperimentalBackend || !this.Optimize || !this.OptimizeSize)
      return;

    var sourceFunctions = new Dictionary<string, ProcedureSymbol>(StringComparer.OrdinalIgnoreCase);
    var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var procedure in model.ProcedureList) {
      if (!sourceFunctions.TryAdd(procedure.Name, procedure))
        ambiguous.Add(procedure.Name);
    }
    foreach (var name in ambiguous)
      sourceFunctions.Remove(name);

    bool Candidate(IrFunction function)
      => sourceFunctions.TryGetValue(function.Name, out var procedure)
         && BackendFilterReason(procedure) is null;

    bool CallTarget(IrFunction function)
      => !function.IsDeclaration
         && sourceFunctions.TryGetValue(function.Name, out var procedure)
         && IsBackendAbiConvention(procedure);

    var merged = SemanticFunctionMerging.RunWithEntryThunks(
      module, Candidate, allowCallTargetDifferences: true, callTargetFilter: CallTarget);

    foreach (var helper in merged.Helpers) {
      if (!TrySemanticParameterLayout(helper, out var parameterOffsets, out var parameterBytes))
        continue;
      if (this.ExternalCalleeDecline(helper) is not null || !this.DataGlobalsResolve(helper, out _))
        continue;
      if (InstructionSelector.TrySelect(helper, out _, this.SelectionTarget) is not { } machine
          || UndefinedRuntimeCallee(machine) is not null)
        continue;

      MachineScheduler.Schedule(machine);
      if (LinearScanAllocator.Allocate(machine, this.SelectionTarget, out _) is not { } allocation)
        continue;

      this._backendSemanticMerges[helper.Name] = new BackendSemanticMerge(
        helper, machine, allocation, parameterOffsets, parameterBytes,
        this.Optimize && FrameElision.IsCandidate(helper));
    }
  }

  /// <summary>The synthetic helpers that are currently valid routing targets.</summary>
  private IEnumerable<string> BackendSemanticMergeNames
    => this._backendSemanticMerges is { } helpers ? helpers.Keys : [];

  private bool IsBackendSemanticMerge(string name)
    => this._backendSemanticMerges?.ContainsKey(name) == true;

  private bool BackendNameIsRouted(string name)
    => this.IsBackendSemanticMerge(name)
       || this.IsBackendGeneratedDefinition(name)
       || this._backendProcs?.Keys.Any(procedure =>
         procedure.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

  /// <summary>
  /// Removes synthetic helpers whose defined callees did not survive native routing. The source thunks
  /// that call a removed helper are then removed by BackendProcs' ordinary call-closure fixpoint, so
  /// they fall back to the direct emitter with their untouched source ABI/body semantics.
  /// </summary>
  private bool PruneBackendSemanticMerges() {
    if (this._backendSemanticMerges is not { Count: > 0 } helpers)
      return false;

    var changed = false;
    foreach (var (name, helper) in helpers.ToList())
      if (CalleeNames(helper.Function)
          .Any(callee => !this.BackendNameIsRouted(callee) && !this.CanCallDirectCallee(callee))) {
        helpers.Remove(name);
        changed = true;
      }

    // A helper that no routed source thunk calls would only add dead bytes. Helpers never call other
    // O0284 helpers: they clone the pre-thunk source body, so their user is always a source entry.
    foreach (var name in helpers.Keys.ToList())
      if (this._backendProcs is null || !this._backendProcs.Keys
          .Select(proc => this._backendModule?.FindFunction(proc.Name))
          .OfType<IrFunction>()
          .Any(function => CalleeNames(function).Contains(name, StringComparer.OrdinalIgnoreCase))) {
        helpers.Remove(name);
        changed = true;
      }

    return changed;
  }

  private void ResetBackendSemanticMerges() {
    this._backendSemanticMerges = null;
    this._backendSemanticMergesEmitted = false;
  }

  /// <summary>Emits the private O0284 shared bodies once, after routing is final.</summary>
  private void EmitBackendSemanticMerges() {
    if (this._backendSemanticMergesEmitted || this._backendSemanticMerges is not { Count: > 0 } helpers)
      return;
    this._backendSemanticMergesEmitted = true;

    foreach (var helper in helpers.Values.OrderBy(entry => entry.Function.Name, StringComparer.Ordinal)) {
      if (this.Optimize && this.Cpu486)
        this._asm.AlignCode(16);
      this._asm.MarkLabel(this._asm.Lbl(helper.Function.Name));
      MachineEmitter.EmitFunction(
        this._asm, helper.Machine, helper.Allocation, helper.ParameterOffsets, helper.ParameterBytes,
        this.CalleeLabel, this.DataCellOf,
        alignLoops: this.Optimize && this.Cost.AlignHotLoops,
        allowFrameElision: helper.ElideFrame);
    }
  }

  /// <summary>Names exposed for focused routing tests; source-procedure coverage remains separate.</summary>
  public IEnumerable<string> BackendSemanticMergeHelpers
    => this._backendSemanticMerges is { } helpers ? helpers.Keys : [];

  private static bool TrySemanticParameterLayout(
      IrFunction helper, out int[] parameterOffsets, out int parameterBytes) {
    parameterOffsets = new int[helper.Parameters.Count];
    var offset = 4;
    for (var i = helper.Parameters.Count - 1; i >= 0; --i) {
      if (SemanticParameterBytes(helper.Parameters[i].Type) is not { } bytes) {
        parameterBytes = 0;
        return false;
      }
      parameterOffsets[i] = offset;
      offset += bytes;
    }
    parameterBytes = offset - 4;
    return true;
  }

  private static int? SemanticParameterBytes(IrType type) => type.Kind switch {
    IrTypeKind.Int when type.Bits is > 0 and <= 16 => 2,
    IrTypeKind.Int when type.Bits == 32 => 4,
    IrTypeKind.Float when type.IsIeeeFloat && type.Bits == 32 => 4,
    IrTypeKind.Float when type.IsIeeeFloat && type.Bits == 64 => 8,
    IrTypeKind.Ptr when type.AddressSpace == 0 => 2,
    _ => null,
  };
}
