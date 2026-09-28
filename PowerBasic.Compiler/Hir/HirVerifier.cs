namespace PowerBasic.Compiler.Hir;

/// <summary>Checks structural contracts of the current high-level function representation.</summary>
public static class HirVerifier {

  public static IReadOnlyList<string> Verify(HirModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var errors = new List<string>();
    if (module.Functions.Count == 0 || !module.Functions[0].IsEntryPoint)
      errors.Add("HIR module must start with its program entry point");
    if (module.Functions.Count(function => function.IsEntryPoint) != 1)
      errors.Add("HIR module must contain exactly one program entry point");

    var procedures = new HashSet<object>(ReferenceEqualityComparer.Instance);
    foreach (var function in module.Functions) {
      if (function.Procedure is { } procedure && !procedures.Add(procedure))
        errors.Add($"HIR module contains procedure '{procedure.Name}' more than once");
      if (function.Body.Any(statement => statement is HirBoundStatement { Source: null }
          or HirEndStatement { Source: null }
          or HirAssignmentStatement { Source: null }
          or HirDirectCallStatement { Source: null }
          or HirDirectCallStatement { Call: null }
          or HirArrayResizeStatement { Source: null }
          or HirArrayResizeStatement { Operations: null }
          or HirArrayEraseStatement { Source: null }
          or HirArrayEraseStatement { Operations: null }))
        errors.Add($"HIR function '{function.Name}' contains an operation without a source node");
      if (function.IsExternal && function.Body.Count != 0)
        errors.Add($"external HIR function '{function.Name}' must not contain a body");
    }
    return errors;
  }
}
