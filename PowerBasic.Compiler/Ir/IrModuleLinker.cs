namespace PowerBasic.Compiler.Ir;

/// <summary>Raised when target-neutral IR modules cannot be combined into one whole-program module.</summary>
public sealed class IrLinkException(string message) : Exception(message);

/// <summary>
/// Combines target-neutral <see cref="IrModule"/> instances before the middle-end runs.
///
/// <para>
/// Function declarations and definitions are resolved case-insensitively, matching BASIC's symbol
/// rules. Every input symbol is remapped to one canonical output symbol before bodies are cloned, so
/// calls that crossed a compilation-unit boundary become ordinary intra-module calls. Existing
/// interprocedural passes can then reason over the complete call graph without special LTO variants.
/// </para>
/// <para>
/// Globals have no external-declaration form in the IR and are therefore module-private storage. Name
/// collisions are kept distinct by deterministic suffixing instead of accidentally coalescing two
/// unrelated cells. The input modules are never mutated.
/// </para>
/// </summary>
public static class IrModuleLinker {

  /// <summary>Thin-links <paramref name="main"/> and <paramref name="linkedModules"/> into a fresh module.</summary>
  public static IrModule Link(IrModule main, IEnumerable<IrModule> linkedModules) {
    ArgumentNullException.ThrowIfNull(main);
    ArgumentNullException.ThrowIfNull(linkedModules);

    var modules = new List<IrModule> { main };
    foreach (var module in linkedModules) {
      ArgumentNullException.ThrowIfNull(module);
      modules.Add(module);
    }

    foreach (var module in modules.Skip(1))
      if (module.EffectiveDialect != main.EffectiveDialect)
        throw new IrLinkException(
          $"cannot link IR module '{module.Name}' using runtime dialect {module.EffectiveDialect} "
          + $"with '{main.Name}' using {main.EffectiveDialect}");

    var result = new IrModule($"{main.Name}+lto", main.Dialect, main.EffectiveDialect);
    var valueMap = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);

    var functionGroups = new Dictionary<string, List<IrFunction>>(StringComparer.OrdinalIgnoreCase);
    var functionOrder = new List<string>();
    var owners = new Dictionary<IrFunction, string>(ReferenceEqualityComparer.Instance);
    foreach (var module in modules)
      foreach (var function in module.Functions) {
        owners[function] = module.Name;
        if (!functionGroups.TryGetValue(function.Name, out var group)) {
          group = [];
          functionGroups.Add(function.Name, group);
          functionOrder.Add(function.Name);
        }
        group.Add(function);
      }

    var definitions = new Dictionary<string, IrFunction?>(StringComparer.OrdinalIgnoreCase);
    var linkedFunctions = new Dictionary<string, IrFunction>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in functionOrder) {
      var group = functionGroups[name];
      var representative = group[0];
      foreach (var candidate in group.Skip(1))
        if (!SameSignature(representative, candidate))
          throw new IrLinkException(
            $"signature mismatch for IR symbol '{name}' between '{owners[representative]}' and '{owners[candidate]}'");

      var defined = group.Where(function => !function.IsDeclaration).ToList();
      if (defined.Count > 1)
        throw new IrLinkException(
          $"duplicate IR definition for '{name}' in '{owners[defined[0]]}' and '{owners[defined[1]]}'");

      var source = defined.FirstOrDefault() ?? representative;
      var parameters = source.Parameters
        .Select(parameter => new IrArgument(parameter.Type, parameter.Index, parameter.Name))
        .ToArray();
      var linked = result.AddFunction(new IrFunction(source.Name, source.ReturnType, parameters) {
        IsVarArgs = source.IsVarArgs,
        // NOINLINE is a programmer contract. If any declaration carries it, the combined symbol does too.
        NoInline = group.Any(function => function.NoInline),
      });

      definitions[name] = defined.FirstOrDefault();
      linkedFunctions[name] = linked;
      foreach (var function in group)
        valueMap[function] = linked;
    }

    var usedNames = result.Functions.Select(function => function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    for (var moduleIndex = 0; moduleIndex < modules.Count; ++moduleIndex)
      foreach (var global in modules[moduleIndex].Globals) {
        var name = UniqueGlobalName(global.Name, moduleIndex, usedNames);
        var linked = result.AddGlobal(new IrGlobalVariable(name, global.ValueType) {
          IsZeroInitialized = global.IsZeroInitialized,
          Bytes = global.Bytes is { } bytes ? [.. bytes] : null,
          FloatingValues = global.FloatingValues is { } values ? [.. values] : null,
          Count = global.Count,
        });
        valueMap[global] = linked;
      }

    foreach (var name in functionOrder) {
      var source = definitions[name];
      if (source is null)
        continue;

      var linked = linkedFunctions[name];
      linked.HasErrorHandler = source.HasErrorHandler;
      linked.HasInlineAsm = source.HasInlineAsm;

      var seed = new Dictionary<IrValue, IrValue>(valueMap, ReferenceEqualityComparer.Instance);
      for (var i = 0; i < source.Parameters.Count; ++i)
        seed[source.Parameters[i]] = linked.Parameters[i];
      IrCloner.Clone(linked, source.Blocks, seed, labelPrefix: "");
    }

    var errors = IrVerifier.Verify(result);
    if (errors.Count > 0)
      throw new IrLinkException($"linked IR failed verification: {string.Join("; ", errors)}");

    return result;
  }

  private static bool SameSignature(IrFunction left, IrFunction right) {
    if (!left.ReturnType.Equals(right.ReturnType)
        || left.IsVarArgs != right.IsVarArgs
        || left.Parameters.Count != right.Parameters.Count)
      return false;

    for (var i = 0; i < left.Parameters.Count; ++i)
      if (!left.Parameters[i].Type.Equals(right.Parameters[i].Type))
        return false;
    return true;
  }

  private static string UniqueGlobalName(string requested, int moduleIndex, HashSet<string> usedNames) {
    if (usedNames.Add(requested))
      return requested;

    var suffix = 0;
    string candidate;
    do {
      candidate = $"{requested}.lto{moduleIndex}{(suffix == 0 ? "" : $".{suffix}")}";
      ++suffix;
    } while (!usedNames.Add(candidate));
    return candidate;
  }
}
