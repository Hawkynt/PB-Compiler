using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Backend;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;
using PowerBasic.Compiler.Semantics;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// A private IR-only definition synthesized by the middle end. Unlike a source procedure it has no
  /// <see cref="ProcedureSymbol"/> to recover ABI or a public/export label from, so both travel with
  /// the IR function itself. O0283 is the first producer, but nothing here is specific to its constant
  /// facts: this is the generic routed representation for generated definitions.
  /// </summary>
  private sealed record BackendGeneratedFunction(
    IrFunction Ir,
    MFunction Machine,
    IReadOnlyDictionary<int, Reg> Allocation,
    bool ElideFrame,
    X86DefinitionStackLayout StackLayout);

  private Dictionary<string, BackendGeneratedFunction>? _backendGenerated;
  private readonly Dictionary<string, Label> _backendGeneratedLabels = new(StringComparer.OrdinalIgnoreCase);
  private bool _backendGeneratedEmitted;

  /// <summary>The successfully selected+allocated generated definitions, by their private IR name.</summary>
  private IEnumerable<string> BackendGeneratedNames
    => this._backendGenerated is null ? [] : this._backendGenerated.Keys;

  /// <summary>
  /// Private middle-end definitions the x86-16 backend will actually emit. This is intentionally a
  /// routing diagnostic like <c>BackendRoutedNames</c>, not part of the program's public symbol table:
  /// generated names are implementation details, but tests and coverage tooling need an honest way to
  /// distinguish "O0283 created a clone" from "the backend emitted that clone".
  /// </summary>
  public IEnumerable<string> BackendGeneratedRoutedNames {
    get {
      _ = this.BackendMain();
      return this.BackendGeneratedNames.ToArray();
    }
  }

  /// <summary>
  /// Selects and allocates every O0283 definition before source-procedure call-graph pruning. They are
  /// provisional at this point: <see cref="PruneBackendGenerated"/> later requires the original source
  /// definition and every generated callee to have survived source allocation too.
  /// </summary>
  private void PrepareBackendGenerated(IrModule module) {
    this._backendGenerated = new(StringComparer.OrdinalIgnoreCase);
    this._backendGeneratedLabels.Clear();
    this._backendGeneratedEmitted = false;

    foreach (var function in module.Functions.Where(ContextSensitiveCloning.IsGeneratedClone)) {
      if (!X86CallAbi.TryDefinitionStackLayout(function, out var layout, out var abiDecline)) {
        this._backendDeclines.Add((function.Name, "filter: " + (abiDecline ?? "unsupported generated definition ABI")));
        continue;
      }
      if (this.ExternalCalleeDecline(function) is { } externalDecline) {
        this._backendDeclines.Add((function.Name, externalDecline));
        continue;
      }
      if (!this.DataGlobalsResolve(function, out var unaddressable)) {
        this._backendDeclines.Add((function.Name, $"routing: global '{unaddressable}' has no cell the emitter can address"));
        continue;
      }
      if (InstructionSelector.TrySelect(function, out var declineReason, this.SelectionTarget) is not { } machine) {
        this._backendDeclines.Add((function.Name, "selection: " + (declineReason ?? "unknown")));
        continue;
      }
      if (UndefinedRuntimeCallee(machine) is { } undefined) {
        this._backendDeclines.Add((function.Name, $"routing: calls '{undefined}', which the DOS runtime does not define"));
        continue;
      }

      MachineScheduler.Schedule(machine);
      if (LinearScanAllocator.Allocate(machine, this.SelectionTarget, out var noRegisters) is not { } allocation) {
        this._backendDeclines.Add((function.Name, "allocation: " + (noRegisters ?? "unknown")));
        continue;
      }

      this._backendGenerated[function.Name] = new BackendGeneratedFunction(
        function, machine, allocation, this.Optimize && FrameElision.IsCandidate(function), layout);
    }
  }

  /// <summary>Whether a defined IR callee has a machine body that will actually be emitted.</summary>
  private bool IsBackendFunctionRouted(string name)
    => this._backendGenerated?.ContainsKey(name) == true
       || this._backendProcs?.Keys.Any(proc => proc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

  /// <summary>
  /// Removes generated bodies whose original source definition or another defined callee failed to
  /// route. Requiring the source definition is intentionally stronger than mere codegen convenience:
  /// a clone and its original may share DATA/dynamic-array/static storage, and routing only one side
  /// would split ownership between the IR and direct emitters.
  /// </summary>
  private bool PruneBackendGenerated(IrModule module) {
    if (this._backendGenerated is null || this._backendGenerated.Count == 0)
      return false;

    var changed = false;
    for (var again = true; again;) {
      again = false;
      foreach (var generated in this._backendGenerated.Values.ToList()) {
        var source = ContextSensitiveCloning.SourceOfGeneratedClone(module, generated.Ir);
        string? stranded = null;
        if (source is null || !this.IsBackendFunctionRouted(source.Name))
          stranded = source?.Name ?? "its source definition";
        else
          stranded = CalleeNames(generated.Ir)
            .FirstOrDefault(name => !this.IsBackendFunctionRouted(name) && !this.CanCallDirectCallee(name));
        if (stranded is null)
          continue;

        this._backendDeclines.Add((generated.Ir.Name, $"routing: calls/depends on '{stranded}', which is not routed"));
        this._backendGenerated.Remove(generated.Ir.Name);
        changed = again = true;
      }
    }
    return changed;
  }

  /// <summary>Private label for a routed generated definition, or null when that definition declined.</summary>
  private Label? GeneratedCalleeLabel(string name) {
    if (this._backendGenerated?.ContainsKey(name) != true)
      return null;
    if (!this._backendGeneratedLabels.TryGetValue(name, out var label)) {
      label = this._asm.DefineLabel("ir_" + name);
      this._backendGeneratedLabels[name] = label;
    }
    return label;
  }

  /// <summary>
  /// Emits every private generated definition exactly once. Forward references are harmless: both
  /// source and generated callees resolve to assembler labels before their bodies need to be marked.
  /// The definition's own IR convention decides stack order (already reflected in StackLayout) and
  /// cleanup ownership, so CDECL emits bare RET while STDCALL/BASIC/PASCAL emit RET n.
  /// </summary>
  private void EmitBackendGeneratedFunctions() {
    if (this._backendGeneratedEmitted || this._backendGenerated is null)
      return;
    this._backendGeneratedEmitted = true;

    foreach (var generated in this._backendGenerated.Values) {
      if (this.Optimize && this.Cpu486)
        this._asm.AlignCode(16);
      this._asm.MarkLabel(this.GeneratedCalleeLabel(generated.Ir.Name)!);
      var abi = X86CallAbi.For(generated.Ir.Convention);
      var cleanupBytes = abi.StackCleanup == X86StackCleanup.Caller ? 0 : generated.StackLayout.ParameterBytes;
      MachineEmitter.EmitFunction(this._asm, generated.Machine, generated.Allocation,
        generated.StackLayout.ParameterOffsets, cleanupBytes, this.CalleeLabel, this.DataCellOf,
        alignLoops: this.Optimize && this.Cost.AlignHotLoops, allowFrameElision: generated.ElideFrame);
    }
  }

  /// <summary>Clears generated routing when DATA/shared-descriptor ownership forces a second decision.</summary>
  private void ResetBackendGenerated() {
    this._backendGenerated = null;
    this._backendGeneratedLabels.Clear();
    this._backendGeneratedEmitted = false;
  }
}
