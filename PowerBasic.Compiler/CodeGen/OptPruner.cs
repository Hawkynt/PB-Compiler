using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 statement-level cleanups (docs/PB36.md O2/O10), applied to the bound
/// tree before emission. Both passes are conservative:
/// <list type="bullet">
/// <item><b>Unreachable-statement elimination (O2)</b>: after an unconditional
/// transfer (GOTO/END/EXIT/RETURN/RESUME) statements are dropped until the next
/// label (the only way control can come back). DATA/equate/DEFtype/meta
/// statements survive - they act at compile time.</item>
/// <item><b>DEF SEG coalescing (O10)</b>: a DEF SEG is dropped when another
/// DEF SEG follows with only provably segment-transparent statements between
/// them (no PEEK/POKE family, no BLOAD/BSAVE/interrupts, no inline asm, no
/// user calls, no control flow). Anything unrecognized counts as an observer.</item>
/// <item><b>GOTO threading (O27)</b>: a GOTO whose target label's next executable
/// statement is another GOTO retargets to the final label of the chain (cycle-guarded),
/// so a jump cascade collapses to one hop already at the source level.</item>
/// </list>
/// </summary>
public static class OptPruner {

  /// <summary>Prunes the main body and every defined procedure of <paramref name="model"/> in place.</summary>
  public static void Prune(SemanticModel model) {
    ArgumentNullException.ThrowIfNull(model);
    var main = ThreadGotos(PruneBlock(model.MainBody, model));
    model.MainBody.Clear();
    model.MainBody.AddRange(main);

    foreach (var proc in model.ProcedureList)
      if (proc.Body != null)
        proc.Body = ThreadGotos(PruneBlock(proc.Body, model));
  }

  #region O26 - GOTO threading

  /// <summary>
  /// Threads GOTO chains in one body (labels are body-scoped): builds label -> next-GOTO-target
  /// from every statement list, resolves chains with a visited guard, and rewrites every GOTO
  /// (including single-line <c>IF ... GOTO</c> arms) to the final label.
  /// </summary>
  private static IReadOnlyList<Statement> ThreadGotos(IReadOnlyList<Statement> body) {
    var hops = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    CollectGotoHops(body, hops);
    if (hops.Count == 0)
      return body;

    string Resolve(string label) {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { label };
      var current = label;
      while (hops.TryGetValue(current, out var next) && seen.Add(next))
        current = next;
      return current;
    }

    var final = hops.Keys.ToDictionary(l => l, Resolve, StringComparer.OrdinalIgnoreCase);
    return RewriteGotos(body, final);
  }

  /// <summary>Records, for every label, the target of the first executable statement after it when that statement is a plain GOTO (compile-time statements and further labels in between are skipped).</summary>
  private static void CollectGotoHops(IReadOnlyList<Statement> body, Dictionary<string, string> hops) {
    for (var i = 0; i < body.Count; ++i) {
      foreach (var block in ChildBlocks(body[i]))
        CollectGotoHops(block, hops);
      if (body[i] is not LabelStmt label)
        continue;
      for (var j = i + 1; j < body.Count; ++j) {
        var s = body[j];
        if (s is LabelStmt or DataStmt or EquateStmt or DefTypeStmt or MetaStmt)
          continue;
        if (s is GotoStmt g && !g.Target.Equals(label.Name, StringComparison.OrdinalIgnoreCase))
          hops[label.Name] = g.Target;
        break;
      }
    }
  }

  private static IReadOnlyList<Statement> RewriteGotos(IReadOnlyList<Statement> body, Dictionary<string, string> final) {
    var result = new List<Statement>(body.Count);
    foreach (var statement in body)
      result.Add(statement switch {
        GotoStmt g when final.TryGetValue(g.Target, out var t) && !t.Equals(g.Target, StringComparison.OrdinalIgnoreCase) => g with { Target = t },
        IfStmt i => i with {
          Then = RewriteGotos(i.Then, final),
          ElseIfs = [.. i.ElseIfs.Select(e => (e.Condition, RewriteGotos(e.Body, final)))],
          Else = i.Else == null ? null : RewriteGotos(i.Else, final),
        },
        SelectStmt s => s with { Arms = [.. s.Arms.Select(a => a with { Body = RewriteGotos(a.Body, final) })] },
        ForStmt f => f with { Body = RewriteGotos(f.Body, final) },
        DoLoopStmt d => d with { Body = RewriteGotos(d.Body, final) },
        _ => statement,
      });
    return result;
  }

  private static IEnumerable<IReadOnlyList<Statement>> ChildBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, b) in i.ElseIfs)
          yield return b;
        if (i.Else != null)
          yield return i.Else;
        break;
      case SelectStmt sel:
        foreach (var arm in sel.Arms)
          yield return arm.Body;
        break;
      case ForStmt f:
        yield return f.Body;
        break;
      case DoLoopStmt d:
        yield return d.Body;
        break;
    }
  }

  #endregion

  /// <summary>Prunes one statement list (recursing into nested blocks first).</summary>
  public static IReadOnlyList<Statement> PruneBlock(IReadOnlyList<Statement> body, SemanticModel model) {
    ArgumentNullException.ThrowIfNull(body);
    ArgumentNullException.ThrowIfNull(model);

    var result = new List<Statement>(body.Count);
    foreach (var statement in body)
      result.Add(PruneChildren(statement, model));

    EliminateUnreachable(result);
    CoalesceDefSegs(result, model);
    return result;
  }

  private static Statement PruneChildren(Statement statement, SemanticModel model) => statement switch {
    IfStmt i => i with {
      Then = PruneBlock(i.Then, model),
      ElseIfs = [.. i.ElseIfs.Select(e => (e.Condition, PruneBlock(e.Body, model)))],
      Else = i.Else == null ? null : PruneBlock(i.Else, model),
    },
    SelectStmt s => s with { Arms = [.. s.Arms.Select(a => a with { Body = PruneBlock(a.Body, model) })] },
    ForStmt f => f with { Body = PruneBlock(f.Body, model) },
    DoLoopStmt d => d with { Body = PruneBlock(d.Body, model) },
    _ => statement,
  };

  #region O2 - unreachable statements

  /// <summary>True when control never falls through this statement.</summary>
  private static bool IsTerminal(Statement s) => s switch {
    GotoStmt or GotoPtrStmt or EndStmt or ExitStmt or ResumeStmt => true,
    ReturnStmt { Target: null } => true,
    _ => false,
  };

  /// <summary>Statements that act at compile time and survive inside dead regions.</summary>
  private static bool IsCompileTimeEffect(Statement s)
    => s is DataStmt or EquateStmt or DefTypeStmt or MetaStmt;

  private static void EliminateUnreachable(List<Statement> body) {
    var reachable = true;
    for (var i = 0; i < body.Count;) {
      var statement = body[i];
      if (statement is LabelStmt)
        reachable = true; // a jump target resumes the flow

      if (!reachable && !IsCompileTimeEffect(statement)) {
        body.RemoveAt(i);
        continue;
      }

      if (IsTerminal(statement))
        reachable = false;
      ++i;
    }
  }

  #endregion

  #region O10 - DEF SEG coalescing

  private static void CoalesceDefSegs(List<Statement> body, SemanticModel model) {
    var pending = -1; // index of a DEF SEG with no observer behind it yet
    for (var i = 0; i < body.Count; ++i) {
      var statement = body[i];
      if (statement is DefSegStmt seg) {
        if (seg.Segment != null && ObservesSegment(seg.Segment, model)) {
          // the new segment expression itself peeks - the old DEF SEG is observed
          pending = i;
          continue;
        }
        if (pending >= 0) {
          body.RemoveAt(pending);
          --i; // list shifted left
        }
        pending = i;
        continue;
      }

      if (!IsSegmentTransparent(statement, model))
        pending = -1;
    }
  }

  /// <summary>True when the statement provably neither reads nor depends on the current DEF SEG.</summary>
  private static bool IsSegmentTransparent(Statement statement, SemanticModel model) => statement switch {
    EquateStmt or DefTypeStmt or DataStmt or MetaStmt => true,
    AssignStmt a => !ObservesSegment(a.Target, model) && !ObservesSegment(a.Value, model),
    IncrDecrStmt id => !ObservesSegment(id.Target, model) && (id.Amount == null || !ObservesSegment(id.Amount, model)),
    PrintStmt { FileNumber: null, UsingFormat: null } p => p.Items.All(item => item.Value == null || !ObservesSegment(item.Value, model)),
    _ => false, // anything else (control flow, calls, I/O, asm, commands) may observe
  };

  /// <summary>True when evaluating <paramref name="e"/> could read memory through the DEF SEG (or do anything opaque).</summary>
  private static bool ObservesSegment(Expression e, SemanticModel model) {
    if (model.CallBindings.ContainsKey(e))
      return true; // user procedures are opaque
    if (model.IntrinsicBindings.TryGetValue(e, out var intrinsic)
        && intrinsic.Name.StartsWith("PEEK", StringComparison.OrdinalIgnoreCase))
      return true;

    return e switch {
      BinaryExpr b => ObservesSegment(b.Left, model) || ObservesSegment(b.Right, model),
      UnaryExpr u => ObservesSegment(u.Operand, model),
      CallOrIndexExpr c => c.Arguments.Any(a => ObservesSegment(a, model)),
      MemberExpr m => ObservesSegment(m.Target, model),
      IndexExpr ix => ObservesSegment(ix.Target, model) || ix.Arguments.Any(a => ObservesSegment(a, model)),
      PtrDerefExpr d => ObservesSegment(d.Pointer, model) || (d.Index != null && ObservesSegment(d.Index, model)),
      ByValArgExpr bv => ObservesSegment(bv.Value, model),
      FileNumberExpr f => ObservesSegment(f.Number, model),
      // literals/names/equates have no children (Subexpressions empty -> false);
      // an unmodeled node observes the segment if any nested expression does.
      _ => AstQuery.Subexpressions(e).Any(c => ObservesSegment(c, model)),
    };
  }

  #endregion
}
