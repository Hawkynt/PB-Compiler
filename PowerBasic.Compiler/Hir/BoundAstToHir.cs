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
    IfStmt conditional => new HirIfStatement(
      conditional with {
        Then = Array.Empty<Statement>(),
        ElseIfs = conditional.ElseIfs
          .Select(clause => (clause.Condition, (IReadOnlyList<Statement>)Array.Empty<Statement>())).ToArray(),
        Else = conditional.Else is null ? null : Array.Empty<Statement>(),
      },
      SnapshotExecutableBody(model, conditional.Then),
      conditional.ElseIfs.Select(clause =>
        (clause.Condition, SnapshotExecutableBody(model, clause.Body))).ToArray(),
      conditional.Else is { } elseBody ? SnapshotExecutableBody(model, elseBody) : null),
    ForStmt loop => new HirForStatement(
      loop with { Body = Array.Empty<Statement>() },
      loop.Variable, loop.From, loop.To, loop.Step, SnapshotExecutableBody(model, loop.Body)),
    DoLoopStmt loop => new HirDoLoopStatement(
      loop with { Body = Array.Empty<Statement>() },
      loop.PreTest, loop.PreCondition, loop.PostTest, loop.PostCondition,
      SnapshotExecutableBody(model, loop.Body)),
    SelectStmt selection => new HirSelectStatement(
      selection with { Arms = selection.Arms.Select(arm => arm with { Body = Array.Empty<Statement>() }).ToArray() },
      selection.Subject,
      selection.Arms.Select(arm =>
        new HirCaseArm(arm.Position, arm.Selectors, SnapshotExecutableBody(model, arm.Body))).ToArray()),
    CallStmt call when model.CallBindings.ContainsKey(call)
        && HirDirectCallBuilder.TryBuild(model, call, call.Arguments, out var operation, out _) =>
      new HirDirectCallStatement(call, operation),
    RedimStmt redim when HirArrayLifetimeBuilder.TryBuild(model, redim, out var resizeOperations, out _) =>
      new HirArrayResizeStatement(redim, resizeOperations),
    EraseStmt erase when HirArrayLifetimeBuilder.TryBuild(model, erase, out var eraseOperations, out _) =>
      new HirArrayEraseStatement(erase, eraseOperations),
    _ => new HirBoundStatement(statement),
  };

  internal static HirStatement LowerStatement(SemanticModel model, Statement statement) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(statement);
    return ToHirStatement(model, statement);
  }
}
