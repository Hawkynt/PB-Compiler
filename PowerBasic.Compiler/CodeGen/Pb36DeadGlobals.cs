using PowerBasic.Compiler.Runtime;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O23 data tree-shaking: a module scalar global that no reachable code ever
/// READS is dead - its data slot, and every dead store to it, contribute nothing.
/// The analysis classifies references SOUNDLY: a global occurrence is a read unless
/// it is provably a pure write - the ONLY pure-write form assumed is a top-level
/// <c>AssignStmt</c> whose <c>Target</c> is exactly <c>NameExpr(global)</c>. Everything
/// else (INCR/SWAP/MID$ assign, array index, BYREF arg, VARPTR/VARSEG of it, being any
/// expression operand) keeps the global live.
///
/// CODEPTR cascade: a <c>CODEPTR(P)</c> that appears only as the RHS of a store to a dead
/// global is not a live reference to P. Excluding those dead stores from the reachability
/// walk may make P (and transitively more) dead, which can make more globals dead - so the
/// dead-global set and the live-procedure set are solved together to a fixpoint.
///
/// HARD CONSERVATIVE GUARDS keep a global no matter what: its address is taken
/// (VARPTR/VARSEG/STRPTR/STRSEG/...32), it is SHARED/COMMON/exported, it is an array, UDT,
/// string (dynamic/fixed/ASCIIZ/FLEX), BCD/FIX, declared <c>DIM ... AT</c>, or any of its
/// writes has a side-effecting RHS. When unsure the global is KEPT - removing a live global
/// or its store is a miscompile; keeping a dead one only misses an optimization.
/// </summary>
public static class Pb36DeadGlobals {

  public sealed record Result(
    HashSet<VariableSymbol> DeadGlobals,
    HashSet<Statement> DeadStores,
    HashSet<ProcedureSymbol> LiveProcedures);

  /// <summary>
  /// Solves the dead-global / dead-store / live-procedure fixpoint for a whole program.
  /// <paramref name="isOwned"/> reports whether a procedure may be dropped (a never-read
  /// global pointer keeps only fully-owned procedures dead). <paramref name="checkingPossible"/>
  /// is true when $ERROR NUMERIC/OVERFLOW/BOUNDS checking is or can become active anywhere -
  /// then a trap-capable store RHS (arithmetic, an array element read) is NOT side-effect-free,
  /// because dropping it would skip the trap, so the global is kept.
  /// </summary>
  public static Result Analyze(SemanticModel model, Func<ProcedureSymbol, bool> isOwned, bool checkingPossible) {
    // candidate globals: fully-owned simple scalar module globals not disqualified by a guard.
    var candidates = new HashSet<VariableSymbol>(
      model.ModuleVariables.Values.Where(v => IsCandidate(v, model)),
      ReferenceEqualityComparer.Instance);

    if (candidates.Count == 0)
      return new([], [], Pb36Reachability.LiveProcedures(model, model.MainBody));

    // a global declared DIM ... AT is overlaid - drop it from the candidates.
    foreach (var node in AllBodies(model).SelectMany(Pb36Reachability.DescendantNodes))
      if (node is DimStmt { AtAddress: not null } dim)
        foreach (var v in dim.Variables)
          foreach (var key in new[] { v.Name + v.Suffix.KeyText(), v.Name + v.Suffix.KeyText() + "()" })
            if (model.ModuleVariables.TryGetValue(key, out var sym))
              candidates.Remove(sym);

    var dead = new HashSet<VariableSymbol>(candidates, ReferenceEqualityComparer.Instance);
    var deadStores = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
    HashSet<ProcedureSymbol> live;

    while (true) {
      // (1) live procedures, NOT following CODEPTR/calls buried in the current dead stores.
      live = LiveProceduresExcluding(model, deadStores);

      // (2) collect references in reachable code; a global referenced anywhere but in a pure
      //     write to a *currently-dead* global stays live. Disqualify on a side-effecting write.
      var read = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
      var disqualified = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
      var stores = new Dictionary<VariableSymbol, List<Statement>>(ReferenceEqualityComparer.Instance);

      foreach (var body in ReachableBodies(model, live))
        ScanBody(body, model, dead, candidates, read, disqualified, stores, checkingPossible);

      // a candidate is dead iff: every reachable occurrence is a pure write, no write has a
      // side-effecting RHS, and it is not read at all.
      var nextDead = new HashSet<VariableSymbol>(
        candidates.Where(v => !read.Contains(v) && !disqualified.Contains(v)),
        ReferenceEqualityComparer.Instance);

      var nextStores = new HashSet<Statement>(ReferenceEqualityComparer.Instance);
      foreach (var v in nextDead)
        if (stores.TryGetValue(v, out var list))
          foreach (var s in list)
            nextStores.Add(s);

      if (nextDead.SetEquals(dead) && nextStores.SetEquals(deadStores)) {
        dead = nextDead;
        deadStores = nextStores;
        break;
      }
      dead = nextDead;
      deadStores = nextStores;
    }

    return new(dead, deadStores, live);
  }

  /// <summary>A fully-owned simple scalar module global with no disqualifying type/sharing guard.</summary>
  private static bool IsCandidate(VariableSymbol v, SemanticModel model)
    => v.Storage == VariableStorage.Global
       && !v.IsShared                                 // SHARED / COMMON / PUBLIC are visible elsewhere
       && v.Type is ScalarType                        // numeric scalar only (covers CODEPTR pointer cells)
       && v.ArrayClass == ArrayClass.Default
       && DosRuntime.InternalVariableLabel(v.Name) is null;  // PB internal cells are runtime-owned

  /// <summary>Every statement body in the whole program (main + every procedure).</summary>
  private static IEnumerable<IReadOnlyList<Statement>> AllBodies(SemanticModel model) {
    yield return model.MainBody;
    foreach (var proc in model.ProcedureList)
      if (proc.Body is { } body)
        yield return body;
  }

  /// <summary>The bodies of reachable code: main plus every live procedure.</summary>
  private static IEnumerable<IReadOnlyList<Statement>> ReachableBodies(SemanticModel model, HashSet<ProcedureSymbol> live) {
    yield return model.MainBody;
    foreach (var proc in model.ProcedureList)
      if (proc.Body is { } body && live.Contains(proc))
        yield return body;
  }

  /// <summary>
  /// Live procedures from main, but treating each statement in <paramref name="excluded"/> as
  /// absent - so a CODEPTR/call reachable only through a dead store is not followed.
  /// </summary>
  private static HashSet<ProcedureSymbol> LiveProceduresExcluding(SemanticModel model, HashSet<Statement> excluded) {
    if (excluded.Count == 0)
      return Pb36Reachability.LiveProcedures(model, model.MainBody);

    var live = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    var work = new Queue<IReadOnlyList<Statement>>();
    work.Enqueue(model.MainBody);

    void Reach(ProcedureSymbol proc) {
      if (proc.Body is { } body && live.Add(proc))
        work.Enqueue(body);
    }

    while (work.Count > 0)
      foreach (var node in DescendantNodesSkipping(work.Dequeue(), excluded)) {
        if (model.CallBindings.TryGetValue(node, out var callee))
          Reach(callee);
        if (node is LambdaExpr lambda && model.LambdaProcs.TryGetValue(lambda, out var lifted))
          Reach(lifted);
      }

    return live;
  }

  /// <summary>Like <see cref="Pb36Reachability.DescendantNodes"/> but never descends into an excluded statement.</summary>
  private static IEnumerable<object> DescendantNodesSkipping(IReadOnlyList<Statement> body, HashSet<Statement> excluded) {
    foreach (var statement in body)
      if (!(statement is Statement s && excluded.Contains(s)))
        foreach (var node in Pb36Reachability.DescendantNodes(statement))
          yield return node;
  }

  /// <summary>
  /// Records, for the candidates, which are read (any non-pure-write occurrence), which are
  /// disqualified (a write with a side-effecting RHS, or any address-of), and the pure-write
  /// stores to each. A pure write to a candidate that is itself currently dead does not count
  /// as a read of any global it references on the RHS only when that RHS is excluded - which
  /// it already is, because dead stores are removed from the reachability walk; here we instead
  /// classify the store's own target as a write and treat its (side-effect-free) RHS reads as
  /// real reads, because the store survives only while the target is NOT dead.
  /// </summary>
  private static void ScanBody(
      IReadOnlyList<Statement> body, SemanticModel model,
      HashSet<VariableSymbol> dead, HashSet<VariableSymbol> candidates,
      HashSet<VariableSymbol> read, HashSet<VariableSymbol> disqualified,
      Dictionary<VariableSymbol, List<Statement>> stores, bool checkingPossible) {

    foreach (var statement in body) {
      // a top-level pure write `global = rhs` is the only non-read occurrence of the target.
      if (statement is AssignStmt { Target: NameExpr target, Value: { } rhs }
          && model.VariableBindings.TryGetValue(target, out var sym)
          && candidates.Contains(sym)) {

        if (!IsSideEffectFreeRhs(rhs, model, checkingPossible))
          disqualified.Add(sym);                 // a side-effecting write: keep the global
        else {
          if (!stores.TryGetValue(sym, out var list))
            stores[sym] = list = [];
          list.Add(statement);
        }

        // the RHS still counts as reads for whatever globals it mentions, EXCEPT a CODEPTR/call
        // edge that would be cut once this store becomes dead. When the target is (currently)
        // dead, this store will be removed, so its RHS contributes no reads/edges; when the
        // target is live, the RHS is real. Mirror that: only scan the RHS for reads when the
        // target is not currently dead.
        if (!dead.Contains(sym))
          foreach (var node in Pb36Reachability.DescendantNodes(rhs))
            MarkOccurrence(node, model, candidates, read);
        continue;
      }

      foreach (var node in Pb36Reachability.DescendantNodes(statement))
        MarkOccurrence(node, model, candidates, read);
    }
  }

  /// <summary>
  /// Marks a candidate global as read for ANY bound occurrence reached here - the only
  /// non-read form (the top-level pure-write target) is handled by the caller before this runs.
  /// A dotted name (Max.X) binds on a <c>MemberExpr</c> and an array element on a
  /// <c>CallOrIndexExpr</c>, not just <c>NameExpr</c>, so keying on the binding (any Expression)
  /// rather than the node type is what keeps those reads visible.
  /// </summary>
  private static void MarkOccurrence(object node, SemanticModel model, HashSet<VariableSymbol> candidates, HashSet<VariableSymbol> read) {
    if (node is Expression e && model.VariableBindings.TryGetValue(e, out var sym) && candidates.Contains(sym))
      read.Add(sym);   // any occurrence reached here (an operand, an index, an arg, a member, ...) is a read
  }

  /// <summary>
  /// A store RHS the optimizer may drop with the global: a literal, a named constant, a
  /// CODEPTR-family code address, a bare scalar variable read, and - only when no $ERROR
  /// NUMERIC/OVERFLOW/BOUNDS checking can fire - an arithmetic/logical tree and array element
  /// reads too. With checking active those CAN trap (Error 6/9), so dropping the store would
  /// skip the trap: such a store is treated as side-effecting and the global is kept. Anything
  /// that could call, deref a pointer, or read a volatile intrinsic (VARPTR, PEEK via a name,
  /// a function call) is always rejected -> the global is kept.
  /// </summary>
  private static bool IsSideEffectFreeRhs(Expression e, SemanticModel model, bool checkingPossible) {
    switch (e) {
      case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
        return true;

      // CODEPTR(P) / CODESEG(P) / CODEPTR32(P): a pure code address (the cascade edge).
      case CallOrIndexExpr call when model.IntrinsicBindings.TryGetValue(call, out var intr)
          && intr.Name is "CODEPTR" or "CODESEG" or "CODEPTR32":
        return true;

      // a bare scalar variable read never traps.
      case NameExpr n:
        return !model.IntrinsicBindings.ContainsKey(n);

      // an array element read can raise Error 9 under $ERROR BOUNDS; arithmetic can raise
      // Error 6 under $ERROR NUMERIC/OVERFLOW - so they are pure only with checking off.
      case CallOrIndexExpr arr when model.VariableBindings.ContainsKey(arr):
        return !checkingPossible && arr.Arguments.All(a => IsSideEffectFreeRhs(a, model, checkingPossible));
      case UnaryExpr u:
        return !checkingPossible && IsSideEffectFreeRhs(u.Operand, model, checkingPossible);
      case BinaryExpr b:
        return !checkingPossible && IsSideEffectFreeRhs(b.Left, model, checkingPossible) && IsSideEffectFreeRhs(b.Right, model, checkingPossible);
      case ByValArgExpr bv:
        return IsSideEffectFreeRhs(bv.Value, model, checkingPossible);

      default:
        return false;   // function/intrinsic calls, pointer deref, member access, VARPTR, ...
    }
  }
}
