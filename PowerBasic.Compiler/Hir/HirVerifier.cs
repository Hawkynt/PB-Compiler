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
      foreach (var statement in EnumerateStatements(function.Body))
        if (statement is HirBoundStatement { Source: null }
            or HirEndStatement { Source: null }
            or HirAssignmentStatement { Source: null }
            or HirDirectCallStatement { Source: null }
            or HirDirectCallStatement { Call: null }
            or HirArrayResizeStatement { Source: null }
            or HirArrayResizeStatement { Operations: null }
            or HirArrayEraseStatement { Source: null }
            or HirArrayEraseStatement { Operations: null }
            or HirIfStatement { Source: null }
            or HirIfStatement { ThenBody: null }
            or HirIfStatement { ElseIfs: null }
            or HirForStatement { Source: null }
            or HirForStatement { Body: null }
            or HirDoLoopStatement { Source: null }
            or HirDoLoopStatement { Body: null }
            or HirSelectStatement { Source: null }
            or HirSelectStatement { Subject: null }
            or HirSelectStatement { Arms: null })
          errors.Add($"HIR function '{function.Name}' contains an operation without its required data");
        else if (statement is HirIfStatement conditional
            && (conditional.Source.Then.Count != 0
              || conditional.Source.ElseIfs.Any(clause => clause.Body.Count != 0)
              || conditional.Source.Else is { Count: > 0 }))
          errors.Add($"HIR IF in '{function.Name}' retained nested bound statement bodies");
        else if (statement is HirForStatement forLoop && forLoop.Source.Body.Count != 0)
          errors.Add($"HIR FOR in '{function.Name}' retained its bound statement body");
        else if (statement is HirDoLoopStatement doLoop && doLoop.Source.Body.Count != 0)
          errors.Add($"HIR DO in '{function.Name}' retained its bound statement body");
        else if (statement is HirSelectStatement selection
            && selection.Source.Arms.Any(arm => arm.Body.Count != 0))
          errors.Add($"HIR SELECT CASE in '{function.Name}' retained bound statement bodies");
      if (function.IsExternal && function.Body.Count != 0)
        errors.Add($"external HIR function '{function.Name}' must not contain a body");
    }
    return errors;
  }

  private static IEnumerable<HirStatement> EnumerateStatements(IEnumerable<HirStatement>? statements) {
    if (statements is null)
      yield break;
    foreach (var statement in statements) {
      if (statement is null)
        continue;
      yield return statement;
      switch (statement) {
        case HirIfStatement conditional:
          foreach (var nested in EnumerateStatements(conditional.ThenBody))
            yield return nested;
          foreach (var clause in conditional.ElseIfs ?? [])
            foreach (var nested in EnumerateStatements(clause.Body))
              yield return nested;
          if (conditional.ElseBody is { } elseBody)
            foreach (var nested in EnumerateStatements(elseBody))
              yield return nested;
          break;
        case HirForStatement forLoop:
          foreach (var nested in EnumerateStatements(forLoop.Body))
            yield return nested;
          break;
        case HirDoLoopStatement doLoop:
          foreach (var nested in EnumerateStatements(doLoop.Body))
            yield return nested;
          break;
        case HirSelectStatement selection:
          foreach (var arm in selection.Arms ?? [])
            foreach (var nested in EnumerateStatements(arm.Body))
              yield return nested;
          break;
      }
    }
  }
}
