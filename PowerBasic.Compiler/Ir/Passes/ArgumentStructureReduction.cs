using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0280 — reduces fully visible read-only aggregate-pointer parameters to the scalar fields the callee uses.
///
/// <para>
/// Aggregate identity is reconstructed from whole-program pointer provenance rather than guessed from a
/// pointer type: local/global byte aggregates are concrete roots, constant GEPs preserve that root, and a
/// forwarded formal is accepted only when every visible caller recursively resolves to such storage.
/// </para>
/// <para>
/// BYVAL records are recognized by lowering's entry copy (<c>llvm.memcpy</c> into a private byte alloca).
/// Their selected fields are already a call-entry snapshot, so the copy can disappear. BYREF fields require
/// the stronger proof that no store or nested call can modify the selected concrete locations; the query is
/// location-sensitive across direct internal calls and uses <see cref="FunctionSummaries"/> to dismiss calls
/// proven not to write memory. Writable aggregate parameters remain O0281 territory.
/// </para>
/// </summary>
public static class ArgumentStructureReduction {

  /// <summary>Conservative per-aggregate signature-growth cap for this target-neutral pass.</summary>
  private const int _MAX_SCALAR_PARAMETERS = 3;

  private readonly record struct ConcreteLocation(IrValue Root, long Offset);

  private sealed record FieldRegion(long Offset, IrType Type, int Size) {
    public List<IrLoad> Loads { get; } = [];
    public List<IrGep> Addresses { get; } = [];
    public long End => this.Offset + this.Size;
  }

  private sealed record ParameterPlan(
    int Index,
    IrArgument Parameter,
    IReadOnlyList<FieldRegion> Regions,
    IrCall? CopyIn,
    IrAlloca? CopyStorage);

  private sealed record FunctionPlan(
    IrFunction Function,
    IReadOnlyList<IrCall> Calls,
    IReadOnlyList<ParameterPlan> Parameters);

  /// <summary>Scalarizes eligible aggregate parameters in <paramref name="module"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    // The reduction is an ABI change: the callee stops taking a pointer and starts taking the fields
    // behind it, and only the direct calls in THIS module are rewritten to match. A consumer that can
    // still build a call from the source declaration therefore has to be able to turn it off.
    if (!module.OwnsProcedureAbi)
      return 0;

    var changed = 0;
    for (var progress = true; progress;) {
      progress = false;
      var summaries = FunctionSummaries.Compute(module);
      foreach (var function in module.Functions.ToList()) {
        if (BuildPlan(module, function, summaries) is not { } plan)
          continue;
        Rewrite(plan);
        changed += plan.Parameters.Count;
        progress = true;
      }
    }
    return changed;
  }

  private static FunctionPlan? BuildPlan(IrModule module, IrFunction function, FunctionSummaries summaries) {
    if (function.IsDeclaration || function.IsVarArgs || function.HasErrorHandler || function.HasInlineAsm
        || function.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
        || !IsFullyVisible(module, function))
      return null;

    var calls = CallsTo(function).ToList();
    if (calls.Count == 0)
      return null;

    var parameters = new List<ParameterPlan>();
    for (var i = 0; i < function.Parameters.Count; ++i)
      if (TryPlanParameter(module, function, function.Parameters[i], calls, summaries) is { } parameter)
        parameters.Add(parameter);

    return parameters.Count == 0 ? null : new(function, calls, parameters);
  }

  private static ParameterPlan? TryPlanParameter(IrModule module, IrFunction function, IrArgument parameter,
      IReadOnlyList<IrCall> calls, FunctionSummaries summaries) {
    if (!parameter.Type.IsPointer)
      return null;

    var readRoot = (IrValue)parameter;
    IrCall? copyIn = null;
    IrAlloca? copyStorage = null;
    var requiredBytes = 0L;
    if (TryFindByValCopyIn(function, parameter, out var copy, out var local, out var copiedBytes)) {
      copyIn = copy;
      copyStorage = local;
      readRoot = local;
      requiredBytes = copiedBytes;
    }

    if (!TryCollectRegions(readRoot, copyIn, out var regions)
        || regions.Count is 0 or > _MAX_SCALAR_PARAMETERS)
      return null;

    regions.Sort((left, right) => left.Offset.CompareTo(right.Offset));
    requiredBytes = Math.Max(requiredBytes, regions[^1].End);
    foreach (var call in calls)
      if (!CanReadAtCallSite(module, call, parameter.Index, requiredBytes))
        return null;

    // A BYVAL entry copy is already the snapshot this transformation materializes at the caller.
    // BYREF has no such snapshot: prove every selected concrete field remains unchanged for the call.
    if (copyIn is null && !FieldsStayUnmodified(module, function, parameter, regions, calls, summaries))
      return null;

    return new(parameter.Index, parameter, regions, copyIn, copyStorage);
  }

  private static bool TryFindByValCopyIn(IrFunction function, IrArgument parameter,
      out IrCall copy, out IrAlloca storage, out long copiedBytes) {
    copy = null!;
    storage = null!;
    copiedBytes = 0;
    if (parameter.Users.Count != 1 || parameter.Users[0] is not IrCall candidate
        || candidate.Callee is not IrFunction { Name: "llvm.memcpy.p0.p0.i32" }
        || candidate.ArgCount != 4
        || candidate.Parent?.Parent is not { } owner || !ReferenceEquals(owner, function)
        || !ReferenceEquals(candidate.GetOperand(2), parameter)
        || candidate.GetOperand(1) is not IrAlloca local
        || !ReferenceEquals(local.Parent?.Parent, function)
        || !local.Allocated.SameStorage(IrType.I8) || local.Count <= 1
        || candidate.GetOperand(3) is not IrConstantInt size || size.Value != local.Count
        || candidate.GetOperand(4) is not IrConstantInt { Value: 0 })
      return false;

    copy = candidate;
    storage = local;
    copiedBytes = size.Value;
    return true;
  }

  private static bool TryCollectRegions(IrValue root, IrInstruction? ignoredUser, out List<FieldRegion> regions) {
    regions = [];
    foreach (var user in root.Users.ToList()) {
      if (ReferenceEquals(user, ignoredUser))
        continue;
      switch (user) {
        case IrLoad load:
          if (!TryAddRegion(regions, 0, load, null))
            return false;
          break;

        case IrGep { ElementType: null, ByteOffset: IrConstantInt offset } address when offset.Value >= 0:
          if (address.Users.Count == 0)
            return false;
          foreach (var addressUser in address.Users.ToList()) {
            if (addressUser is not IrLoad fieldLoad || !TryAddRegion(regions, offset.Value, fieldLoad, address))
              return false;
          }
          break;

        default:
          return false;                                // dynamic indexing, writes and pointer escapes stay aggregate
      }
    }
    return true;
  }

  private static bool TryAddRegion(List<FieldRegion> regions, long offset, IrLoad load, IrGep? address) {
    var size = SizeOf(load.Type);
    if (size == 0 || offset < 0 || offset > long.MaxValue - size)
      return false;
    var end = offset + size;

    foreach (var region in regions) {
      if (region.Offset == offset) {
        if (region.Size != size || !region.Type.Equals(load.Type))
          return false;                               // one byte range cannot become two differently typed formals
        region.Loads.Add(load);
        if (address is not null && !region.Addresses.Contains(address))
          region.Addresses.Add(address);
        return true;
      }
      if (offset < region.End && region.Offset < end)
        return false;                                 // overlapping fields preserve shared storage/UNION semantics
    }

    var added = new FieldRegion(offset, load.Type, size);
    added.Loads.Add(load);
    if (address is not null)
      added.Addresses.Add(address);
    regions.Add(added);
    return true;
  }

  private static bool CanReadAtCallSite(IrModule module, IrCall call, int parameterIndex, long requiredBytes) {
    if (requiredBytes <= 0 || parameterIndex >= call.ArgCount
        || call.Parent?.Parent is not { } caller
        || caller.HasErrorHandler || caller.HasInlineAsm)
      return false;

    return TryResolveProvenance(module, caller, call.GetOperand(parameterIndex + 1), [], out var locations)
      && locations.Count > 0
      && locations.All(location => IsValidAggregateLocation(location, requiredBytes));
  }

  private static bool FieldsStayUnmodified(IrModule module, IrFunction function, IrArgument parameter,
      IReadOnlyList<FieldRegion> regions, IReadOnlyList<IrCall> calls, FunctionSummaries summaries) {
    foreach (var call in calls) {
      if (call.Parent?.Parent is not { } caller)
        return false;

      var bindings = BuildBindings(module, function, call, caller);
      if (!bindings.TryGetValue(parameter, out var bases))
        return false;

      foreach (var region in regions) {
        if (!TryOffsetLocations(bases, region.Offset, out var targets))
          return false;
        if (MayModifyLocations(module, function, bindings, targets, region.Type, summaries,
              new HashSet<IrFunction>(ReferenceEqualityComparer.Instance)))
          return false;
      }
    }
    return true;
  }

  private static Dictionary<IrArgument, IReadOnlyList<ConcreteLocation>> BuildBindings(
      IrModule module, IrFunction callee, IrCall call, IrFunction caller) {
    var result = new Dictionary<IrArgument, IReadOnlyList<ConcreteLocation>>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < callee.Parameters.Count && i < call.ArgCount; ++i) {
      var parameter = callee.Parameters[i];
      if (!parameter.Type.IsPointer)
        continue;
      if (TryResolveProvenance(module, caller, call.GetOperand(i + 1), [], out var locations))
        result[parameter] = locations;
    }
    return result;
  }

  /// <summary>
  /// Conservative location-sensitive mod query. Pure/read-only calls are discharged by the existing
  /// call-graph summary. Writable internal calls are inspected recursively with pointer formals mapped
  /// to the concrete objects supplied by this call site; unknown/external writes remain a wall.
  /// </summary>
  private static bool MayModifyLocations(IrModule module, IrFunction function,
      IReadOnlyDictionary<IrArgument, IReadOnlyList<ConcreteLocation>> bindings,
      IReadOnlyList<ConcreteLocation> targets, IrType targetType, FunctionSummaries summaries,
      HashSet<IrFunction> active) {
    if (!active.Add(function))
      return summaries.For(function).WritesMemory;     // a writable recursive cycle stays conservative

    try {
      foreach (var instruction in function.AllInstructions)
        switch (instruction) {
          case IrStore store:
            if (!TryResolveInContext(function, store.Pointer, bindings, out var writes)
                || LocationsMayOverlap(writes, store.Value.Type, targets, targetType))
              return true;
            break;

          case IrCall call:
            if (!MayCallModifyLocations(module, function, call, bindings, targets, targetType, summaries, active))
              continue;
            return true;
        }
      return false;
    } finally {
      active.Remove(function);
    }
  }

  private static bool MayCallModifyLocations(IrModule module, IrFunction caller, IrCall call,
      IReadOnlyDictionary<IrArgument, IReadOnlyList<ConcreteLocation>> callerBindings,
      IReadOnlyList<ConcreteLocation> targets, IrType targetType, FunctionSummaries summaries,
      HashSet<IrFunction> active) {
    if (call.Callee is not IrFunction callee)
      return true;
    if (!summaries.For(callee).WritesMemory)
      return false;
    if (callee.IsDeclaration || callee.HasErrorHandler || callee.HasInlineAsm || !module.Functions.Contains(callee))
      return true;

    var nested = new Dictionary<IrArgument, IReadOnlyList<ConcreteLocation>>(ReferenceEqualityComparer.Instance);
    for (var i = 0; i < callee.Parameters.Count && i < call.ArgCount; ++i) {
      var parameter = callee.Parameters[i];
      if (!parameter.Type.IsPointer)
        continue;
      if (TryResolveInContext(caller, call.GetOperand(i + 1), callerBindings, out var locations))
        nested[parameter] = locations;
    }
    return MayModifyLocations(module, callee, nested, targets, targetType, summaries, active);
  }

  private static bool TryResolveInContext(IrFunction function, IrValue value,
      IReadOnlyDictionary<IrArgument, IReadOnlyList<ConcreteLocation>> bindings,
      out List<ConcreteLocation> locations) {
    locations = [];
    if (!TryDecomposePointer(value, out var root, out var offset))
      return false;

    switch (root) {
      case IrAlloca alloca when ReferenceEquals(alloca.Parent?.Parent, function):
        locations.Add(new(alloca, offset));
        return true;
      case IrGlobalVariable global:
        locations.Add(new(global, offset));
        return true;
      case IrArgument argument when ReferenceEquals(argument.Parent, function)
                                     && bindings.TryGetValue(argument, out var bound):
        return TryOffsetLocations(bound, offset, out locations);
      default:
        return false;
    }
  }

  /// <summary>
  /// Resolves a pointer to concrete local/global roots. A forwarded argument is valid only when every
  /// direct caller is visible and recursively resolves; a cycle without a concrete base is not proof.
  /// </summary>
  private static bool TryResolveProvenance(IrModule module, IrFunction function, IrValue value,
      HashSet<IrArgument> active, out List<ConcreteLocation> locations) {
    locations = [];
    if (!TryDecomposePointer(value, out var root, out var offset))
      return false;

    switch (root) {
      case IrAlloca alloca when ReferenceEquals(alloca.Parent?.Parent, function):
        locations.Add(new(alloca, offset));
        return true;
      case IrGlobalVariable global:
        locations.Add(new(global, offset));
        return true;
      case IrArgument argument:
        if (!ReferenceEquals(argument.Parent, function) || !active.Add(argument)
            || !IsFullyVisible(module, function))
          return false;
        try {
          var callers = CallsTo(function).ToList();
          if (callers.Count == 0)
            return false;
          foreach (var call in callers) {
            if (argument.Index >= call.ArgCount || call.Parent?.Parent is not { } caller
                || !TryResolveProvenance(module, caller, call.GetOperand(argument.Index + 1), active, out var incoming)
                || !TryOffsetLocations(incoming, offset, out var shifted))
              return false;
            locations.AddRange(shifted);
          }
          return locations.Count > 0;
        } finally {
          active.Remove(argument);
        }
      default:
        return false;
    }
  }

  private static bool TryDecomposePointer(IrValue value, out IrValue root, out long offset) {
    root = value;
    offset = 0;
    while (true)
      switch (root) {
        case IrCast { Op: IrCastOp.BitCast } cast when cast.Type.IsPointer && cast.Value.Type.IsPointer:
          root = cast.Value;
          continue;
        case IrGep gep:
          if (!TryGepOffset(gep, out var displacement) || !TryAdd(offset, displacement, out offset))
            return false;
          root = gep.BasePtr;
          continue;
        default:
          return root.Type.IsPointer;
      }
  }

  private static bool TryGepOffset(IrGep gep, out long offset) {
    offset = 0;
    if (gep.ByteOffset is not IrConstantInt index)
      return false;
    if (gep.ElementType is null) {
      offset = index.Value;
      return true;
    }
    if (IrAliasAnalysis.StorageBytes(gep.ElementType) is not { } elementBytes)
      return false;
    try {
      offset = checked(index.Value * elementBytes);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool IsValidAggregateLocation(ConcreteLocation location, long requiredBytes) {
    if (location.Offset < 0 || requiredBytes <= 0 || !TryAdd(location.Offset, requiredBytes, out var end))
      return false;
    return location.Root switch {
      IrAlloca alloca => alloca.Allocated.SameStorage(IrType.I8) && alloca.Count > 1 && end <= alloca.Count,
      IrGlobalVariable global => global.Bytes is null && global.ValueType.SameStorage(IrType.I8)
                                 && global.Count > 1 && end <= global.Count,
      _ => false,
    };
  }

  private static bool LocationsMayOverlap(IReadOnlyList<ConcreteLocation> writes, IrType writeType,
      IReadOnlyList<ConcreteLocation> targets, IrType targetType) {
    var writeBytes = IrAliasAnalysis.StorageBytes(writeType);
    var targetBytes = IrAliasAnalysis.StorageBytes(targetType);
    if (writeBytes is not { } w || targetBytes is not { } t)
      return true;

    foreach (var write in writes)
      foreach (var target in targets) {
        if (!ReferenceEquals(write.Root, target.Root))
          continue;
        if (!TryAdd(write.Offset, w, out var writeEnd) || !TryAdd(target.Offset, t, out var targetEnd))
          return true;
        if (write.Offset < targetEnd && target.Offset < writeEnd)
          return true;
      }
    return false;
  }

  private static bool TryOffsetLocations(IReadOnlyList<ConcreteLocation> source, long delta,
      out List<ConcreteLocation> shifted) {
    shifted = new List<ConcreteLocation>(source.Count);
    foreach (var location in source) {
      if (!TryAdd(location.Offset, delta, out var offset)) {
        shifted = [];
        return false;
      }
      shifted.Add(new(location.Root, offset));
    }
    return true;
  }

  private static bool TryAdd(long left, long right, out long result) {
    try {
      result = checked(left + right);
      return true;
    } catch (OverflowException) {
      result = 0;
      return false;
    }
  }

  private static bool IsFullyVisible(IrModule module, IrFunction function) {
    foreach (var user in function.Users) {
      if (user is not IrCall call || !ReferenceEquals(call.Callee, function)
          || call.Args.Any(argument => ReferenceEquals(argument, function)))
        return false;
      var owner = call.Parent?.Parent;
      if (owner is null || !module.Functions.Contains(owner))
        return false;
    }
    return true;
  }

  private static IEnumerable<IrCall> CallsTo(IrFunction function)
    => function.Users.OfType<IrCall>().Where(call => ReferenceEquals(call.Callee, function));

  private static int SizeOf(IrType type) => type.Kind switch {
    IrTypeKind.Int or IrTypeKind.Float => Math.Max(1, (type.Bits + 7) / 8),
    _ => 0,                                           // pointer width is target-dependent
  };

  private static void Rewrite(FunctionPlan plan) {
    RewriteCalls(plan);
    RewriteSignatureAndBody(plan);
  }

  private static void RewriteCalls(FunctionPlan plan) {
    var replacements = plan.Parameters.ToDictionary(parameter => parameter.Index);
    foreach (var call in plan.Calls) {
      var block = call.Parent!;
      var arguments = new List<IrValue>();
      for (var i = 0; i < plan.Function.Parameters.Count; ++i) {
        var original = call.GetOperand(i + 1);
        if (!replacements.TryGetValue(i, out var parameter)) {
          arguments.Add(original);
          continue;
        }

        foreach (var region in parameter.Regions) {
          IrValue address = original;
          if (region.Offset != 0)
            address = block.InsertBefore(new IrGep(
              original, new IrConstantInt(IrType.I32, region.Offset)), call);
          arguments.Add(block.InsertBefore(new IrLoad(region.Type, address), call));
        }
      }

      var replacement = block.InsertBefore(new IrCall(call.Type, plan.Function, arguments, call.Convention) {
        Name = call.Name,
        FastMathFlags = call.FastMathFlags,
      }, call);
      call.ReplaceAllUsesWith(replacement);
      call.EraseFromParent();
    }
  }

  private static void RewriteSignatureAndBody(FunctionPlan plan) {
    var replacements = plan.Parameters.ToDictionary(parameter => parameter.Index);
    var fresh = new List<IrArgument>();

    for (var i = 0; i < plan.Function.Parameters.Count; ++i) {
      var original = plan.Function.Parameters[i];
      if (!replacements.TryGetValue(i, out var parameter)) {
        var replacement = new IrArgument(original.Type, fresh.Count, original.Name);
        original.ReplaceAllUsesWith(replacement);
        fresh.Add(replacement);
        continue;
      }

      foreach (var region in parameter.Regions) {
        var scalar = new IrArgument(region.Type, fresh.Count, ScalarName(original, region.Offset));
        fresh.Add(scalar);
        foreach (var load in region.Loads)
          load.ReplaceAllUsesWith(scalar);
      }
    }

    foreach (var parameter in plan.Parameters) {
      foreach (var region in parameter.Regions) {
        foreach (var load in region.Loads)
          if (load.Parent is not null)
            load.EraseFromParent();
        foreach (var address in region.Addresses)
          if (address.Parent is not null && address.HasNoUsers)
            address.EraseFromParent();
      }

      if (parameter.CopyIn is { Parent: not null } copy)
        copy.EraseFromParent();
      if (parameter.CopyStorage is { Parent: not null, HasNoUsers: true } storage)
        storage.EraseFromParent();
    }

    plan.Function.ReplaceParameters(fresh);
    plan.Function.SignatureRewritten = true;
  }

  private static string ScalarName(IrArgument parameter, long offset) {
    var stem = parameter.Name ?? $"arg{parameter.Index}";
    return $"{stem}.{offset}";
  }
}
