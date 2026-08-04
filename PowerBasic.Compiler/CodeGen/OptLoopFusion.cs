using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// O0062 loop fusion: two adjacent <c>FOR</c> loops over the SAME counter with identical bounds,
/// whose bodies are simple - only scalar or counter-indexed-array assignments, no I/O, calls or
/// control flow - and free of a fusion-breaking dependence, merge into one loop. That halves the
/// per-iteration loop overhead and walks each array once instead of twice.
///
/// Every array subscript is exactly the counter, so a value the first loop writes and the second
/// reads is the same element the fused iteration just produced - the classic <c>b(i) = a(i)*2</c>
/// chain is safe. The only illegal shapes are SCALAR carries: the second body may not read a scalar
/// the first writes (it would see a per-iteration value where the two-loop form reads the final
/// one), and the first may not read a scalar the second writes (it would see the second's stale
/// write). Both are rejected conservatively, which also drops the shared-accumulator case where the
/// operation is not associative.
/// </summary>
public static class OptLoopFusion {

  /// <summary>Fuses adjacent FOR loops in the main body and every procedure body of <paramref name="model"/>, recursively.</summary>
  public static void Fuse(SemanticModel model) {
    ArgumentNullException.ThrowIfNull(model);
    var main = FuseList(model.MainBody, model);
    model.MainBody.Clear();
    model.MainBody.AddRange(main);

    foreach (var proc in model.ProcedureList)
      if (proc.Body is { } body)
        proc.Body = FuseList(body, model);
  }

  private static List<Statement> FuseList(IReadOnlyList<Statement> body, SemanticModel model) {
    var result = new List<Statement>(body.Count);
    foreach (var raw in body) {
      var s = RewriteChildBlocks(raw, model);   // fuse inside nested blocks first
      if (result.Count > 0 && result[^1] is ForStmt prev && s is ForStmt cur
          && TryFuse(prev, cur, model) is { } fused)
        result[^1] = fused;                      // the fused loop may itself fuse with the next
      else
        result.Add(s);
    }
    return result;
  }

  private static Statement RewriteChildBlocks(Statement s, SemanticModel model) => s switch {
    ForStmt f => f with { Body = FuseList(f.Body, model) },
    DoLoopStmt d => d with { Body = FuseList(d.Body, model) },
    IfStmt i => i with {
      Then = FuseList(i.Then, model),
      ElseIfs = [.. i.ElseIfs.Select(e => (e.Condition, (IReadOnlyList<Statement>)FuseList(e.Body, model)))],
      Else = i.Else != null ? FuseList(i.Else, model) : null,
    },
    SelectStmt sel => sel with { Arms = [.. sel.Arms.Select(a => a with { Body = FuseList(a.Body, model) })] },
    _ => s,
  };

  /// <summary>The fused loop, or null when the two are not the same shape or fusion is not legal.</summary>
  private static ForStmt? TryFuse(ForStmt f1, ForStmt f2, SemanticModel model) {
    if (f1.Variable is not NameExpr v1 || f2.Variable is not NameExpr v2
        || !model.VariableBindings.TryGetValue(v1, out var counter)
        || !model.VariableBindings.TryGetValue(v2, out var counter2)
        || !ReferenceEquals(counter, counter2))
      return null;                               // must iterate the same counter
    if (!StructEqual(f1.From, f2.From, model) || !StructEqual(f1.To, f2.To, model) || !StepEqual(f1.Step, f2.Step, model))
      return null;                               // identical bounds

    if (!Analyze(f1.Body, counter, model, out var writes1, out var reads1)
        || !Analyze(f2.Body, counter, model, out var writes2, out var reads2))
      return null;                               // a non-simple body or a non-counter subscript

    // scalar carries are the only hazard (arrays are all same-index): the first must not read a
    // scalar the second writes, and the second must not read a scalar the first writes
    if (reads1.Overlaps(writes2) || reads2.Overlaps(writes1))
      return null;

    // merge: the first loop's header over both bodies. f2's counter reads bind to the same symbol,
    // so they resolve unchanged inside the fused loop.
    return f1 with { Body = [.. f1.Body, .. f2.Body] };
  }

  /// <summary>
  /// Verifies the body is fusion-simple (only scalar / counter-indexed-array assignments and
  /// increments) and collects the SCALAR variables it writes and reads. Returns false on anything
  /// else - a call, I/O, control flow, an array subscript that is not exactly the counter, or a
  /// write to the counter itself.
  /// </summary>
  private static bool Analyze(IReadOnlyList<Statement> body, VariableSymbol counter, SemanticModel model,
      out HashSet<VariableSymbol> writeScalars, out HashSet<VariableSymbol> readScalars) {
    var writes = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    var reads = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    writeScalars = writes;
    readScalars = reads;
    var ok = true;

    bool IsCounterIndex(Expression e) =>
      e is NameExpr n && model.VariableBindings.TryGetValue(n, out var s) && ReferenceEquals(s, counter);

    void Read(Expression e) {
      switch (e) {
        case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
          break;
        case NameExpr n when model.IntrinsicBindings.ContainsKey(n):
          ok = false;   // a volatile intrinsic (TIMER, INKEY$, ...) may observe the interleaving
          break;
        case NameExpr n when model.VariableBindings.TryGetValue(n, out var s):
          if (s.Type is ArrayType) { ok = false; break; }   // a whole-array reference
          if (!ReferenceEquals(s, counter)) reads.Add(s);
          break;
        case CallOrIndexExpr c when model.VariableBindings.TryGetValue(c, out var arr) && arr.Type is ArrayType:
          if (c.Arguments is not [{ } idx] || !IsCounterIndex(idx)) ok = false;   // must be a(counter), rank 1
          break;
        case UnaryExpr u:
          Read(u.Operand);
          break;
        case BinaryExpr b:
          Read(b.Left); Read(b.Right);
          break;
        default:
          ok = false;   // a function call, member/pointer access, anything unmodelled
          break;
      }
    }

    foreach (var stmt in body) {
      switch (stmt) {
        case AssignStmt { Target: NameExpr t, Value: { } val }
            when model.VariableBindings.TryGetValue(t, out var ts) && ts.Type is ScalarType:
          if (ReferenceEquals(ts, counter)) return false;   // rewriting the counter breaks the loop
          Read(val);
          writes.Add(ts);
          break;
        case AssignStmt { Target: CallOrIndexExpr at, Value: { } val }
            when model.VariableBindings.TryGetValue(at, out var arr) && arr.Type is ArrayType { Rank: 1 }:
          if (at.Arguments is not [{ } idx] || !IsCounterIndex(idx)) return false;
          Read(val);
          break;
        case IncrDecrStmt { Target: NameExpr t } id
            when model.VariableBindings.TryGetValue(t, out var ts) && ts.Type is ScalarType:
          if (ReferenceEquals(ts, counter)) return false;
          reads.Add(ts);
          writes.Add(ts);
          if (id.Amount is { } amt) Read(amt);
          break;
        default:
          return false;   // any other statement (call, PRINT, IF, nested loop, GOTO, EXIT, ...)
      }
      if (!ok)
        return false;
    }
    return true;
  }

  private static bool StepEqual(Expression? s1, Expression? s2, SemanticModel model)
    => (s1 == null && s2 == null) || (s1 != null && s2 != null && StructEqual(s1, s2, model));

  /// <summary>Structural equality over the bound expressions: equal literals, the same variable, or the same operator tree.</summary>
  private static bool StructEqual(Expression a, Expression b, SemanticModel model) => (a, b) switch {
    (IntegerLiteralExpr x, IntegerLiteralExpr y) => x.Value == y.Value,
    (NameExpr x, NameExpr y) => model.VariableBindings.TryGetValue(x, out var sx)
      && model.VariableBindings.TryGetValue(y, out var sy) && ReferenceEquals(sx, sy),
    (UnaryExpr x, UnaryExpr y) => x.Op == y.Op && StructEqual(x.Operand, y.Operand, model),
    (BinaryExpr x, BinaryExpr y) => x.Op == y.Op && StructEqual(x.Left, y.Left, model) && StructEqual(x.Right, y.Right, model),
    _ => false,
  };
}
