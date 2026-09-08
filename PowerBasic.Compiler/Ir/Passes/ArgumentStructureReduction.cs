namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0280 — reduces fully visible read-only aggregate-pointer parameters to the scalar fields the callee uses.
///
/// <para>
/// The IR no longer distinguishes a BYREF record from every other pointer parameter, so this pass does
/// not infer aggregate-ness from the callee alone. Every rewritten call must pass a direct local byte
/// aggregate (<c>alloca i8, N</c>), every use in the callee must be a scalar load from a constant byte
/// offset, and the whole function must be visible through direct calls. That deliberately leaves
/// forwarded/global aggregates and arbitrary pointer parameters to a later provenance-aware extension.
/// </para>
/// <para>
/// Moving field loads from the callee to the call site is legal only while their values cannot change
/// between entry and the original loads. The first implementation therefore admits leaf callees only
/// and rejects every store that is not rooted in callee-local storage. This is conservative, but it
/// makes alias safety a proof instead of a guess. Writable aggregate parameters belong to O0281.
/// </para>
/// </summary>
public static class ArgumentStructureReduction {

  /// <summary>LLVM's default arg-promotion profitability bound, applied per aggregate parameter.</summary>
  private const int _MAX_SCALAR_PARAMETERS = 3;

  private sealed record FieldRegion(long Offset, IrType Type, int Size) {
    public List<IrLoad> Loads { get; } = [];
    public List<IrGep> Addresses { get; } = [];
    public long End => this.Offset + this.Size;
  }

  private sealed record ParameterPlan(int Index, IrArgument Parameter, IReadOnlyList<FieldRegion> Regions);
  private sealed record FunctionPlan(
    IrFunction Function,
    IReadOnlyList<IrCall> Calls,
    IReadOnlyList<ParameterPlan> Parameters);

  /// <summary>Scalarizes eligible aggregate parameters in <paramref name="module"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var changed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (BuildPlan(module, function) is not { } plan)
        continue;
      Rewrite(plan);
      changed += plan.Parameters.Count;
    }
    return changed;
  }

  private static FunctionPlan? BuildPlan(IrModule module, IrFunction function) {
    if (function.IsDeclaration || function.IsVarArgs || function.HasErrorHandler || function.HasInlineAsm
        || function.Name.Equals("main", StringComparison.OrdinalIgnoreCase)
        || !IsFullyVisible(module, function))
      return null;

    var calls = CallsTo(function).ToList();
    if (calls.Count == 0 || function.AllInstructions.OfType<IrCall>().Any())
      return null;                                    // leaf-only v1: no hidden aliasing side effects between loads
    if (function.AllInstructions.OfType<IrStore>().Any(store => !IsLocalPointer(store.Pointer, function)))
      return null;                                    // a nonlocal write could alias the aggregate being snapshotted

    var parameters = new List<ParameterPlan>();
    for (var i = 0; i < function.Parameters.Count; ++i)
      if (TryPlanParameter(function.Parameters[i], calls) is { } parameter)
        parameters.Add(parameter);

    return parameters.Count == 0 ? null : new(function, calls, parameters);
  }

  private static ParameterPlan? TryPlanParameter(IrArgument parameter, IReadOnlyList<IrCall> calls) {
    if (!parameter.Type.IsPointer)
      return null;

    var regions = new List<FieldRegion>();
    foreach (var user in parameter.Users.ToList())
      switch (user) {
        case IrLoad load:
          if (!TryAddRegion(regions, 0, load, null))
            return null;
          break;

        case IrGep { ElementType: null, ByteOffset: IrConstantInt offset } address when offset.Value >= 0:
          if (address.Users.Count == 0)
            return null;
          foreach (var addressUser in address.Users.ToList()) {
            if (addressUser is not IrLoad fieldLoad || !TryAddRegion(regions, offset.Value, fieldLoad, address))
              return null;
          }
          break;

        default:
          return null;                                // dynamic indexing, writes and pointer escapes stay aggregate
      }

    if (regions.Count is 0 or > _MAX_SCALAR_PARAMETERS)
      return null;

    regions.Sort((left, right) => left.Offset.CompareTo(right.Offset));
    foreach (var call in calls)
      if (!CanReadAtCallSite(call, parameter.Index, regions))
        return null;
    return new(parameter.Index, parameter, regions);
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

  private static bool CanReadAtCallSite(IrCall call, int parameterIndex, IReadOnlyList<FieldRegion> regions) {
    if (parameterIndex >= call.ArgCount
        || call.Parent?.Parent is not { } caller
        || caller.HasErrorHandler || caller.HasInlineAsm
        || call.GetOperand(parameterIndex + 1) is not IrAlloca aggregate
        || !ReferenceEquals(aggregate.Parent?.Parent, caller)
        || !aggregate.Allocated.SameStorage(IrType.I8) || aggregate.Count <= 1)
      return false;

    return regions.All(region => region.End <= aggregate.Count);
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

  private static bool IsLocalPointer(IrValue pointer, IrFunction function) {
    while (pointer is IrGep address)
      pointer = address.BasePtr;
    return pointer is IrAlloca alloca && ReferenceEquals(alloca.Parent?.Parent, function);
  }

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

    foreach (var parameter in plan.Parameters)
      foreach (var region in parameter.Regions) {
        foreach (var load in region.Loads)
          if (load.Parent is not null)
            load.EraseFromParent();
        foreach (var address in region.Addresses)
          if (address.Parent is not null && address.HasNoUsers)
            address.EraseFromParent();
      }

    plan.Function.ReplaceParameters(fresh);
  }

  private static string ScalarName(IrArgument parameter, long offset) {
    var stem = parameter.Name ?? $"arg{parameter.Index}";
    return $"{stem}.{offset}";
  }
}
