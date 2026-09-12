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
  /// Every private definition the middle end synthesized, and therefore the ones the routing cannot
  /// find through <c>model.ProcedureList</c>: an O0283 context clone, an O0275 outlined cold region.
  ///
  /// <para>
  /// The rule is the PROPERTY that makes a function synthetic - it is defined, it is not the module
  /// body, and no source procedure carries its name - rather than one name pattern per producer, so
  /// the next pass that appends a helper is routed by this bridge instead of growing a third copy of
  /// it. O0284's shared bodies are the single exception and keep their own: the merge that mints them
  /// has to RUN inside the routing (a source-visible procedure ABI may not change), so they do not
  /// exist until <see cref="PrepareBackendSemanticMerges"/> has already claimed them.
  /// </para>
  /// </summary>
  private bool IsGeneratedDefinition(IrFunction function)
    => !function.IsDeclaration
       && !function.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
       && !this.IsBackendSemanticMerge(function.Name)
       && !model.ProcedureList.Any(procedure =>
         procedure.Name.Equals(function.Name, StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Selects and allocates every generated definition before source-procedure call-graph pruning. They
  /// are provisional at this point: <see cref="PruneBackendGenerated"/> later requires every generated
  /// callee - and, for an O0283 clone, the original source definition - to have survived source
  /// allocation too.
  ///
  /// <para>
  /// O0275's extraction is SPECULATIVE here, which is the one thing a clone is not. A clone exists
  /// because a caller was rebound onto it and is worth nothing without that caller; an outlined region
  /// was lifted out of a body that was perfectly routable WITH it, so a helper this bridge cannot take
  /// would cost the caller its routing for no gain at all. <c>tests/diff/DIFF36.BAS</c> produces one
  /// the register allocator cannot finish. Such a helper goes back where it came from and the caller
  /// is routed as though it had never been outlined; putting one back changes a body this sweep has
  /// already taken a selection from, so the sweep restarts, and it can only restart as often as there
  /// are helpers to remove.
  /// </para>
  /// </summary>
  private void PrepareBackendGenerated(IrModule module) {
    this._backendGenerated = new(StringComparer.OrdinalIgnoreCase);
    this._backendGeneratedLabels.Clear();
    this._backendGeneratedEmitted = false;

    List<(string Name, string Reason)> declines;
    do {
      declines = [];
      this._backendGenerated.Clear();
    } while (!this.RouteGeneratedDefinitions(module, declines));

    this._backendDeclines.AddRange(declines);
  }

  /// <summary>
  /// One sweep over the generated definitions. Answers false when it put an outlined region back,
  /// because the body it came from is one every selection taken so far may have been read from.
  /// </summary>
  private bool RouteGeneratedDefinitions(IrModule module, List<(string Name, string Reason)> declines) {
    foreach (var function in module.Functions.Where(this.IsGeneratedDefinition).ToList()) {
      if (this.TryRouteBackendGenerated(function, out var decline))
        continue;
      if (ColdCodeOutlining.Reinline(module, function))
        return false;
      declines.Add((function.Name, decline));
    }
    return true;
  }

  /// <summary>
  /// Puts one generated definition through the selection/scheduling/allocation a source procedure gets,
  /// answering whether it can be routed and, when it cannot, the routing's own reason for it.
  /// </summary>
  private bool TryRouteBackendGenerated(IrFunction function, out string decline) {
    if (!X86CallAbi.TryDefinitionStackLayout(function, out var layout, out var abiDecline)) {
      decline = "filter: " + (abiDecline ?? "unsupported generated definition ABI");
      return false;
    }
    if (this.ExternalCalleeDecline(function) is { } externalDecline) {
      decline = externalDecline;
      return false;
    }
    if (!this.DataGlobalsResolve(function, out var unaddressable)) {
      decline = $"routing: global '{unaddressable}' has no cell the emitter can address";
      return false;
    }
    if (InstructionSelector.TrySelect(function, out var declineReason, this.SelectionTarget) is not { } machine) {
      decline = "selection: " + (declineReason ?? "unknown");
      return false;
    }
    if (UndefinedRuntimeCallee(machine) is { } undefined) {
      decline = $"routing: calls '{undefined}', which the DOS runtime does not define";
      return false;
    }

    MachineScheduler.Schedule(machine);
    if (LinearScanAllocator.Allocate(machine, this.SelectionTarget, out var noRegisters) is not { } allocation) {
      decline = "allocation: " + (noRegisters ?? "unknown");
      return false;
    }

    this._backendGenerated![function.Name] = new BackendGeneratedFunction(
      function, machine, allocation, this.Optimize && FrameElision.IsCandidate(function), layout);
    decline = string.Empty;
    return true;
  }

  /// <summary>
  /// The body a generated definition was COPIED from, or null when it is not a clone.
  ///
  /// <para>
  /// A cloner records the provenance on the IR function (<see cref="IrFunction.ClonedFrom"/>), which
  /// is what keeps the rule above about cloning rather than about one producer's names. O0283 predates
  /// the field and is recovered from its marker instead - the same answer by a different route, and
  /// the reason this is a query rather than a property read.
  /// </para>
  /// </summary>
  private static string? CloneSourceName(IrModule module, IrFunction generated)
    => generated.ClonedFrom
       ?? ContextSensitiveCloning.SourceOfGeneratedClone(module, generated)?.Name;

  /// <summary>Whether a name belongs to a generated definition the backend will actually emit.</summary>
  private bool IsBackendGeneratedDefinition(string name)
    => this._backendGenerated?.ContainsKey(name) == true;

  /// <summary>
  /// Removes generated bodies whose defined callees - and, for a CLONE, whose original source
  /// definition - failed to route. Requiring the source definition is intentionally stronger than mere
  /// codegen convenience: a clone and its original may share DATA/dynamic-array/static storage, and
  /// routing only one side would split ownership between the IR and direct emitters. It is a rule
  /// about CLONING rather than about generated definitions, so an outlined region - which shares no
  /// storage with anything, having been lifted out of a single body - is held to the callee rule only.
  /// </summary>
  private bool PruneBackendGenerated(IrModule module) {
    if (this._backendGenerated is null || this._backendGenerated.Count == 0)
      return false;

    var changed = false;
    for (var again = true; again;) {
      again = false;
      foreach (var generated in this._backendGenerated.Values.ToList()) {
        string? stranded;
        if (CloneSourceName(module, generated.Ir) is { } cloneSource)
          stranded = this.BackendNameIsRouted(cloneSource) ? null : cloneSource;
        else
          stranded = ContextSensitiveCloning.IsGeneratedClone(generated.Ir) ? "its source definition" : null;
        stranded ??= CalleeNames(generated.Ir)
          .FirstOrDefault(name => !this.BackendNameIsRouted(name) && !this.CanCallDirectCallee(name));
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
