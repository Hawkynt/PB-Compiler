using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O25 - automatic compile-time evaluation of pure functions. A FUNCTION the
/// compiler can prove <em>pure</em> (its result depends only on its BYVAL arguments -
/// no I/O, no globals, no BYREF, no side effects) and that is called with all
/// compile-time-constant arguments is <b>interpreted at compile time</b> and the call
/// is replaced by the resulting literal. No <c>CONSTEXPR</c> keyword: purity is
/// inferred, so ordinary functions fold transparently when their inputs are constant.
///
/// Sound subset (v1): integer-typed functions only (BYTE/INTEGER/WORD/LONG/DWORD and
/// integer-typed locals/params), straight-line code plus IF / SELECT CASE / FOR /
/// DO-LOOP / WHILE with EXIT FUNCTION / EXIT FOR / EXIT DO, and calls to other pure
/// functions (recursion included). Every intermediate is wrapped to its static type so
/// the folded value equals what the runtime ALU would have produced bit-for-bit; a
/// step/recursion budget bounds compile time (a runaway evaluation simply does not
/// fold and the real call is emitted). Floats, strings, arrays, intrinsics, pointers
/// and global access make a function ineligible - it is then compiled normally.
/// </summary>
public static class OptPureFold {

  private const int StepBudget = 500_000;
  private const int RecursionDepth = 64;

  /// <summary>Maps each foldable constant-argument call expression to its computed result.</summary>
  public static Dictionary<CallOrIndexExpr, ConstantValue> Analyze(SemanticModel model) {
    var result = new Dictionary<CallOrIndexExpr, ConstantValue>(ReferenceEqualityComparer.Instance);
    var pure = ClassifyPure(model);
    if (pure.Count == 0)
      return result;

    var folder = new ConstantFolder(model.Equates, model.EnumMembers);
    var evaluator = new Evaluator(model, pure);

    foreach (var (key, proc) in model.CallBindings) {
      if (key is not CallOrIndexExpr call || !pure.Contains(proc))
        continue;
      if (BindArguments(model, key, proc, folder) is not { } args)
        continue;
      if (evaluator.Evaluate(proc, args) is { } folded)
        result[call] = folded;
    }

    return result;
  }

  /// <summary>Folds the call's arguments (default-filling omitted ones) into constants, or null if any is non-constant.</summary>
  private static long[]? BindArguments(SemanticModel model, object key, ProcedureSymbol proc, ConstantFolder folder) {
    var visible = proc.VisibleParameterCount;
    var args = model.ReorderedArguments.GetValueOrDefault(key) ?? (key is CallOrIndexExpr c ? c.Arguments : []);
    if (args.Count > visible)
      return null;

    var values = new long[visible];
    for (var i = 0; i < visible; ++i) {
      var argExpr = i < args.Count ? Unwrap(args[i]) : proc.Parameters[i].DefaultValue;
      if (argExpr == null || folder.TryFold(argExpr) is not { Integer: { } v })
        return null;
      values[i] = ((ScalarType)proc.Parameters[i].Type) is { } t ? CodeGenerator.WrapToType(v, t) : v;
    }
    return values;
  }

  private static Expression Unwrap(Expression e) => e is ByValArgExpr b ? b.Value : e;

  #region purity classification

  /// <summary>Greatest fixed point: a function is pure if it is structurally pure and every function it calls is pure.</summary>
  private static HashSet<ProcedureSymbol> ClassifyPure(SemanticModel model) {
    var candidates = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    var calls = new Dictionary<ProcedureSymbol, List<ProcedureSymbol>>(ReferenceEqualityComparer.Instance);

    foreach (var proc in AllProcedures(model)) {
      if (!StructurallyEligible(proc))
        continue;
      var callees = new List<ProcedureSymbol>();
      if (BodyIsPure(proc.Body!, model, callees)) {
        candidates.Add(proc);
        calls[proc] = callees;
      }
    }

    // remove any candidate that (transitively) calls a non-candidate, to a fixed point
    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var proc in candidates.ToList())
        if (calls[proc].Any(callee => !candidates.Contains(callee))) {
          candidates.Remove(proc);
          changed = true;
        }
    }

    return candidates;
  }

  private static IEnumerable<ProcedureSymbol> AllProcedures(SemanticModel model) {
    var seen = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    foreach (var p in model.Procedures.Values)
      if (seen.Add(p))
        yield return p;
    foreach (var list in model.Overloads.Values)
      foreach (var p in list)
        if (seen.Add(p))
          yield return p;
  }

  /// <summary>Signature-level eligibility: an integer FUNCTION with only BYVAL integer scalar parameters and a real body.</summary>
  private static bool StructurallyEligible(ProcedureSymbol proc) {
    if (!proc.IsFunction || proc.Body == null || proc.IsExternal || proc.IsGenerator || proc.HasSretParam || proc.IsCdecl)
      return false;
    if (proc.ReturnType is not ScalarType { IsFloat: false, IsIntegral: true })
      return false;
    foreach (var p in proc.Parameters) {
      if (!p.ByVal || p.Type is not ScalarType { IsFloat: false, IsIntegral: true })
        return false;
    }
    return true;
  }

  private static bool BodyIsPure(IReadOnlyList<Statement> body, SemanticModel model, List<ProcedureSymbol> callees)
    => body.All(s => StatementIsPure(s, model, callees));

  private static bool StatementIsPure(Statement s, SemanticModel model, List<ProcedureSymbol> callees) {
    switch (s) {
      case DimStmt d:
        // plain local scalar declarations only - no SHARED/STATIC/PUBLIC/COMMON, no arrays,
        // no ABSOLUTE, no initializer (a separate assignment is interpreted normally)
        if (d.Storage is not (StorageClass.Dim or StorageClass.Local) || d.StaticFlag || d.SharedFlag || d.AtAddress != null || d.CommonBlock != null)
          return false;
        return d.Variables.All(v => v.ArrayBounds == null && v.Initializer == null);

      case AssignStmt a:
        return IsLocalScalarTarget(a.Target, model) && ExprIsPure(a.Value, model, callees);

      case IncrDecrStmt id:
        return IsLocalScalarTarget(id.Target, model) && (id.Amount == null || ExprIsPure(id.Amount, model, callees));

      case IfStmt i:
        return ExprIsPure(i.Condition, model, callees)
          && BodyIsPure(i.Then, model, callees)
          && i.ElseIfs.All(e => ExprIsPure(e.Condition, model, callees) && BodyIsPure(e.Body, model, callees))
          && (i.Else == null || BodyIsPure(i.Else, model, callees));

      case SelectStmt sel:
        return ExprIsPure(sel.Subject, model, callees)
          && sel.Arms.All(arm => BodyIsPure(arm.Body, model, callees)
            && arm.Selectors.All(x => (x.Value == null || ExprIsPure(x.Value, model, callees))
              && (x.RangeUpper == null || ExprIsPure(x.RangeUpper, model, callees))));

      case ForStmt f:
        return IsLocalScalarTarget(f.Variable, model) && ExprIsPure(f.From, model, callees) && ExprIsPure(f.To, model, callees)
          && (f.Step == null || ExprIsPure(f.Step, model, callees)) && BodyIsPure(f.Body, model, callees);

      case DoLoopStmt d:
        return (d.PreCondition == null || ExprIsPure(d.PreCondition, model, callees))
          && (d.PostCondition == null || ExprIsPure(d.PostCondition, model, callees))
          && BodyIsPure(d.Body, model, callees);

      case ExitStmt e:
        return e.Kind is ExitKind.Function or ExitKind.For or ExitKind.Do or ExitKind.Loop;

      default:
        return false; // PRINT, INPUT, CALL, GOTO, ON ERROR, SWAP, anything with effects
    }
  }

  private static bool IsLocalScalarTarget(Expression e, SemanticModel model)
    => e is NameExpr && model.VariableBindings.TryGetValue(e, out var sym)
       && sym.Storage is VariableStorage.Local or VariableStorage.Parameter
       && !sym.IsShared && sym.Type is ScalarType { IsFloat: false, IsIntegral: true };

  private static bool ExprIsPure(Expression e, SemanticModel model, List<ProcedureSymbol> callees) {
    switch (e) {
      case IntegerLiteralExpr:
      case NamedConstantExpr:
        return true;

      case NameExpr n when model.EnumMembers.ContainsKey(n.Name):
        return true;

      case NameExpr n:
        // only this function's own integer locals/params; a global/shared read is impure
        return model.VariableBindings.TryGetValue(n, out var sym)
          && sym.Storage is VariableStorage.Local or VariableStorage.Parameter
          && !sym.IsShared && sym.Type is ScalarType { IsFloat: false, IsIntegral: true };

      case UnaryExpr u:
        return u.Op is UnaryOp.Negate or UnaryOp.Not && ExprIsPure(u.Operand, model, callees);

      case BinaryExpr b:
        return ExprIsPure(b.Left, model, callees) && ExprIsPure(b.Right, model, callees);

      case IfExpr t:
        return ExprIsPure(t.Condition, model, callees) && ExprIsPure(t.WhenTrue, model, callees) && ExprIsPure(t.WhenFalse, model, callees);

      case CallOrIndexExpr call:
        // a call to another user FUNCTION is pure if that function is (recorded as a dependency);
        // intrinsics / array indexing / proc-pointer calls are not foldable here
        if (model.IntrinsicBindings.ContainsKey(call) || model.ProcPtrCalls.ContainsKey(call) || model.VariableBindings.ContainsKey(call))
          return false;
        if (!model.CallBindings.TryGetValue(call, out var callee))
          return false;
        callees.Add(callee);
        return call.Arguments.All(arg => ExprIsPure(Unwrap(arg), model, callees));

      default:
        return false; // MemberExpr, IndexExpr, PtrDeref, string/float literals, etc.
    }
  }

  #endregion

  #region interpreter

  private sealed class Evaluator(SemanticModel model, HashSet<ProcedureSymbol> pure) {
    private int _steps;

    public ConstantValue? Evaluate(ProcedureSymbol proc, long[] args) {
      this._steps = 0;
      try {
        var value = this.Call(proc, args, 0);
        return value == null ? null : ConstantValue.Of(value.Value);
      } catch (BailOut) {
        return null;
      }
    }

    private sealed class BailOut : Exception;

    private long? Call(ProcedureSymbol proc, long[] args, int depth) {
      if (depth > RecursionDepth)
        throw new BailOut();

      var env = new Dictionary<VariableSymbol, long>(ReferenceEqualityComparer.Instance);
      for (var i = 0; i < args.Length && i < proc.Parameters.Count; ++i)
        env[proc.Parameters[i]] = args[i];

      var resultSym = proc.Variables.GetValueOrDefault(proc.Name);
      if (resultSym == null)
        throw new BailOut();

      this.ExecBlock(proc.Body!, env, depth);
      var raw = env.GetValueOrDefault(resultSym, 0);
      return CodeGenerator.WrapToType(raw, (ScalarType)proc.ReturnType!);
    }

    // control-flow signal bubbling up from a block
    private enum Flow { Normal, ExitLoop, ExitFunction }

    private Flow ExecBlock(IReadOnlyList<Statement> body, Dictionary<VariableSymbol, long> env, int depth) {
      foreach (var s in body) {
        var flow = this.Exec(s, env, depth);
        if (flow != Flow.Normal)
          return flow;
      }
      return Flow.Normal;
    }

    private Flow Exec(Statement s, Dictionary<VariableSymbol, long> env, int depth) {
      if (++this._steps > StepBudget)
        throw new BailOut();

      switch (s) {
        case DimStmt:
          return Flow.Normal; // declaration only; the local defaults to 0 on first read

        case AssignStmt a:
          this.Store(a.Target, this.Eval(a.Value, env, depth), env, depth);
          return Flow.Normal;

        case IncrDecrStmt id: {
          var sym = this.SymbolOf(id.Target);
          var amount = id.Amount == null ? 1 : this.Eval(id.Amount, env, depth);
          var cur = env.GetValueOrDefault(sym, 0);
          env[sym] = CodeGenerator.WrapToType(id.Increment ? cur + amount : cur - amount, (ScalarType)sym.Type);
          return Flow.Normal;
        }

        case IfStmt i: {
          if (this.Eval(i.Condition, env, depth) != 0)
            return this.ExecBlock(i.Then, env, depth);
          foreach (var (cond, body) in i.ElseIfs)
            if (this.Eval(cond, env, depth) != 0)
              return this.ExecBlock(body, env, depth);
          return i.Else != null ? this.ExecBlock(i.Else, env, depth) : Flow.Normal;
        }

        case SelectStmt sel: {
          var subject = this.Eval(sel.Subject, env, depth);
          foreach (var arm in sel.Arms) {
            if (arm.Selectors.Count == 0 || arm.Selectors.Any(x => this.SelectorMatches(x, subject, env, depth)))
              return this.ExecBlock(arm.Body, env, depth);
          }
          return Flow.Normal;
        }

        case ForStmt f:
          return this.ExecFor(f, env, depth);

        case DoLoopStmt d:
          return this.ExecDoLoop(d, env, depth);

        case ExitStmt e:
          return e.Kind == ExitKind.Function ? Flow.ExitFunction : Flow.ExitLoop;

        default:
          throw new BailOut(); // unreachable for a classified-pure body, but fail safe
      }
    }

    private Flow ExecFor(ForStmt f, Dictionary<VariableSymbol, long> env, int depth) {
      var sym = this.SymbolOf(f.Variable);
      var type = (ScalarType)sym.Type;
      var from = this.Eval(f.From, env, depth);
      var to = this.Eval(f.To, env, depth);
      var step = f.Step == null ? 1 : this.Eval(f.Step, env, depth);
      if (step == 0)
        throw new BailOut();

      env[sym] = CodeGenerator.WrapToType(from, type);
      while (step > 0 ? env[sym] <= to : env[sym] >= to) {
        var flow = this.ExecBlock(f.Body, env, depth);
        if (flow == Flow.ExitFunction)
          return flow;
        if (flow == Flow.ExitLoop)
          break;
        env[sym] = CodeGenerator.WrapToType(env[sym] + step, type);
        if (++this._steps > StepBudget)
          throw new BailOut();
      }
      return Flow.Normal;
    }

    private Flow ExecDoLoop(DoLoopStmt d, Dictionary<VariableSymbol, long> env, int depth) {
      while (true) {
        if (d.PreTest != LoopTestKind.None && d.PreCondition != null) {
          var c = this.Eval(d.PreCondition, env, depth) != 0;
          if (d.PreTest == LoopTestKind.While ? !c : c)
            break;
        }
        var flow = this.ExecBlock(d.Body, env, depth);
        if (flow == Flow.ExitFunction)
          return flow;
        if (flow == Flow.ExitLoop)
          break;
        if (d.PostTest != LoopTestKind.None && d.PostCondition != null) {
          var c = this.Eval(d.PostCondition, env, depth) != 0;
          if (d.PostTest == LoopTestKind.While ? !c : c)
            break;
        }
        if (++this._steps > StepBudget)
          throw new BailOut();
      }
      return Flow.Normal;
    }

    private bool SelectorMatches(CaseSelector x, long subject, Dictionary<VariableSymbol, long> env, int depth) {
      if (x.IsComparison is { } cmp) {
        var v = this.Eval(x.Value!, env, depth);
        return cmp switch {
          CaseComparison.Equal => subject == v,
          CaseComparison.NotEqual => subject != v,
          CaseComparison.Less => subject < v,
          CaseComparison.LessEqual => subject <= v,
          CaseComparison.Greater => subject > v,
          CaseComparison.GreaterEqual => subject >= v,
          _ => false,
        };
      }
      var lo = this.Eval(x.Value!, env, depth);
      if (x.RangeUpper is { } upperExpr)
        return subject >= lo && subject <= this.Eval(upperExpr, env, depth);
      return subject == lo;
    }

    private void Store(Expression target, long value, Dictionary<VariableSymbol, long> env, int depth) {
      var sym = this.SymbolOf(target);
      env[sym] = CodeGenerator.WrapToType(value, (ScalarType)sym.Type);
    }

    private VariableSymbol SymbolOf(Expression e)
      => model.VariableBindings.TryGetValue(e, out var sym) ? sym : throw new BailOut();

    private long Eval(Expression e, Dictionary<VariableSymbol, long> env, int depth) {
      switch (e) {
        case IntegerLiteralExpr i:
          return this.Wrap(e, i.Value);

        case NamedConstantExpr c:
          return model.Equates.TryGetValue(c.Name, out var k) && k.Integer is { } kv ? this.Wrap(e, kv) : throw new BailOut();

        case NameExpr n when model.EnumMembers.TryGetValue(n.Name, out var em) && !model.VariableBindings.ContainsKey(n):
          return this.Wrap(e, em);

        case NameExpr:
          return env.GetValueOrDefault(this.SymbolOf(e), 0);

        case UnaryExpr u: {
          var v = this.Eval(u.Operand, env, depth);
          return this.Wrap(e, u.Op == UnaryOp.Negate ? -v : ~v);
        }

        case BinaryExpr b:
          return this.Wrap(e, this.EvalBinary(b, env, depth));

        case IfExpr t:
          return this.Eval(t.Condition, env, depth) != 0 ? this.Eval(t.WhenTrue, env, depth) : this.Eval(t.WhenFalse, env, depth);

        case CallOrIndexExpr call when model.CallBindings.TryGetValue(call, out var callee) && pure.Contains(callee): {
          var visible = callee.VisibleParameterCount;
          var args = model.ReorderedArguments.GetValueOrDefault(call) ?? call.Arguments;
          if (args.Count > visible)
            throw new BailOut();
          var values = new long[visible];
          for (var i = 0; i < visible; ++i) {
            var argExpr = i < args.Count ? Unwrap(args[i]) : callee.Parameters[i].DefaultValue ?? throw new BailOut();
            values[i] = CodeGenerator.WrapToType(this.Eval(argExpr, env, depth), (ScalarType)callee.Parameters[i].Type);
          }
          var r = this.Call(callee, values, depth + 1);
          return r ?? throw new BailOut();
        }

        default:
          throw new BailOut();
      }
    }

    private long Wrap(Expression e, long value)
      => model.TypeOf(e) is ScalarType { IsFloat: false } t ? CodeGenerator.WrapToType(value, t) : value;

    private long EvalBinary(BinaryExpr b, Dictionary<VariableSymbol, long> env, int depth) {
      var l = this.Eval(b.Left, env, depth);
      var r = this.Eval(b.Right, env, depth);
      return b.Op switch {
        BinaryOp.Add => l + r,
        BinaryOp.Subtract => l - r,
        BinaryOp.Multiply => l * r,
        BinaryOp.IntegerDivide => r == 0 ? throw new BailOut() : l / r,
        BinaryOp.Modulo => r == 0 ? throw new BailOut() : l % r,
        BinaryOp.And => l & r,
        BinaryOp.Or => l | r,
        BinaryOp.Xor => l ^ r,
        BinaryOp.Eqv => ~(l ^ r),
        BinaryOp.Imp => ~l | r,
        BinaryOp.ShiftLeft when r is >= 0 and < 64 => l << (int)r,
        BinaryOp.Equal => l == r ? -1 : 0,
        BinaryOp.NotEqual => l != r ? -1 : 0,
        BinaryOp.Less => l < r ? -1 : 0,
        BinaryOp.Greater => l > r ? -1 : 0,
        BinaryOp.LessEqual => l <= r ? -1 : 0,
        BinaryOp.GreaterEqual => l >= r ? -1 : 0,
        _ => throw new BailOut(), // Divide, Power, Concat, shifts/rotates we do not model -> no fold
      };
    }
  }

  #endregion
}
