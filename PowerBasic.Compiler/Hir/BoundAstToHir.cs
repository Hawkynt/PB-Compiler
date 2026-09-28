using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>Forms the initial high-level function inventory from the binder's resolved program.</summary>
public static class BoundAstToHir {

  public static HirModule Lower(SemanticModel model) {
    ArgumentNullException.ThrowIfNull(model);

    var functions = new List<HirFunction> {
      new(null, SnapshotExecutableBody(model, model.MainBody)),
    };
    var seen = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var procedure in model.Procedures.Values.Concat(model.LambdaProcs.Values).Concat(model.ProcedureList))
      if (seen.Add(procedure))
        functions.Add(new(procedure, SnapshotExecutableBody(model, procedure.Body ?? [])));

    var module = new HirModule(model, functions.AsReadOnly());
    var errors = HirVerifier.Verify(module);
    if (errors.Count != 0)
      throw new InvalidOperationException($"Bound AST produced invalid HIR: {string.Join("; ", errors)}");
    return module;
  }

  private static IReadOnlyList<HirStatement> SnapshotExecutableBody(
      SemanticModel model,
      IReadOnlyList<Statement> body) {
    var statements = new List<HirStatement>(body.Count);
    Flatten(model, body, statements);
    return statements.AsReadOnly();
  }

  private static void Flatten(
      SemanticModel model,
      IEnumerable<Statement> source,
      ICollection<HirStatement> destination) {
    foreach (var statement in source)
      if (statement is GroupStmt group)
        Flatten(model, group.Body, destination);
      else
        destination.Add(ToHirStatement(model, statement));
  }

  private static HirStatement ToHirStatement(SemanticModel model, Statement statement) => statement switch {
    EndStmt end => new HirEndStatement(end),
    AssignStmt assignment => new HirAssignmentStatement(assignment),
    CallStmt call when model.CallBindings.ContainsKey(call)
        && HirDirectCallBuilder.TryBuild(model, call, call.Arguments, out var operation, out _) =>
      new HirDirectCallStatement(call, operation),
    RedimStmt redim when HirArrayLifetimeBuilder.TryBuild(model, redim, out var resizeOperations, out _) =>
      new HirArrayResizeStatement(redim, resizeOperations),
    EraseStmt erase when HirArrayLifetimeBuilder.TryBuild(model, erase, out var eraseOperations, out _) =>
      new HirArrayEraseStatement(erase, eraseOperations),
    _ => new HirBoundStatement(statement),
  };
}
