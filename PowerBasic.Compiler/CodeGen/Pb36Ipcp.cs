using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O18 - interprocedural constant propagation. A scalar parameter that
/// receives the same compile-time constant at <em>every</em> call site and is
/// never written inside the callee holds that constant on every invocation, so
/// its reads emit the literal directly (feeding O1 folding, O2 dead-code and
/// O17 branch elimination inside the callee). The call ABI is unchanged - the
/// argument is still passed - so observable behavior is identical; only the
/// callee body specializes.
///
/// Disabled wholesale when the program takes a procedure's address (CODEPTR /
/// CALL DWORD), since such an indirect call could supply a different argument
/// the analysis cannot see.
/// </summary>
public static class Pb36Ipcp {

  public static Dictionary<VariableSymbol, ConstantValue> Analyze(SemanticModel model) {
    var result = new Dictionary<VariableSymbol, ConstantValue>(ReferenceEqualityComparer.Instance);

    // bail if any procedure address is taken - an indirect call is opaque
    foreach (var (e, intrinsic) in model.IntrinsicBindings)
      if (intrinsic.Name is "CODEPTR" or "CODEPTR32") {
        _ = e;
        return result;
      }

    var folder = new ConstantFolder(model.Equates, model.EnumMembers);

    // collect, per (proc, param index), the set of constant arguments seen
    var seen = new Dictionary<ProcedureSymbol, ConstantValue?[]>(ReferenceEqualityComparer.Instance);
    var poisoned = new Dictionary<ProcedureSymbol, bool[]>(ReferenceEqualityComparer.Instance);
    var callCount = new Dictionary<ProcedureSymbol, int>(ReferenceEqualityComparer.Instance);

    foreach (var (key, proc) in model.CallBindings) {
      if (proc.IsExternal || proc.IsCdecl || proc.Body == null)
        continue;
      var args = model.ReorderedArguments.GetValueOrDefault(key) ?? ArgsOf(key); // PB 3.6 named args already positional
      if (args.Count > proc.Parameters.Count)
        continue;
      // PB 3.6 default parameters: a call that omits trailing defaulted arguments
      // effectively passes those defaults, so fold them in - otherwise IPCP would
      // see only the explicit call sites and wrongly think a parameter is constant.
      if (args.Count < proc.Parameters.Count) {
        var extended = new List<Expression>(args);
        var allDefaulted = true;
        for (var i = args.Count; i < proc.Parameters.Count; ++i) {
          if (proc.Parameters[i].DefaultValue is not { } d) {
            allDefaulted = false; // an unaccounted omitted argument (e.g. CDECL variadic)
            break;
          }
          extended.Add(d);
        }
        if (!allDefaulted)
          continue; // cannot account for the omitted arguments - do not propagate
        args = extended;
      }
      callCount[proc] = callCount.GetValueOrDefault(proc) + 1;
      if (!seen.TryGetValue(proc, out var slots)) {
        slots = new ConstantValue?[proc.Parameters.Count];
        poisoned[proc] = new bool[proc.Parameters.Count];
        seen[proc] = slots;
      }
      var poison = poisoned[proc];
      for (var i = 0; i < args.Count; ++i) {
        if (poison[i])
          continue;
        if (folder.TryFold(args[i]) is { } folded && (folded.Integer.HasValue || folded.Float.HasValue)) {
          if (slots[i] is { } prior && !SameConstant(prior, folded))
            poison[i] = true;
          else
            slots[i] = folded;
        } else {
          poison[i] = true;
        }
      }
    }

    foreach (var (proc, slots) in seen) {
      if (callCount.GetValueOrDefault(proc) == 0)
        continue;
      var poison = poisoned[proc];
      for (var i = 0; i < slots.Length; ++i) {
        if (poison[i] || slots[i] is not { } constant)
          continue;
        var parameter = proc.Parameters[i];
        if (parameter.Type is not ScalarType)
          continue; // strings/UDTs are not constant-propagated
        if (WritesParameter(proc.Body!, model, parameter))
          continue; // a write would make later reads observe a different value
        result[parameter] = constant;
      }
    }

    return result;
  }

  private static bool SameConstant(ConstantValue a, ConstantValue b)
    => a.Integer == b.Integer && a.Float.Equals(b.Float) && a.Text == b.Text;

  private static IReadOnlyList<Expression> ArgsOf(object callKey) => callKey switch {
    CallStmt c => c.Arguments,
    CallOrIndexExpr e => e.Arguments,
    NameExpr => [],
    _ => [],
  };

  /// <summary>
  /// Conservatively true when any path could store to <paramref name="parameter"/>:
  /// a direct write-statement target, or the parameter passed to a procedure
  /// other than as an explicit BYVAL argument (the default BYREF lets the
  /// callee write through it - even from inside an expression's function call).
  /// </summary>
  private static bool WritesParameter(IReadOnlyList<Statement> body, SemanticModel model, VariableSymbol parameter) {
    bool IsParam(Expression e) => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
      && ReferenceEqualityComparer.Instance.Equals(s, parameter);

    bool ExprMightWrite(Expression? e) {
      switch (e) {
        case null:
          return false;
        case CallOrIndexExpr call when model.CallBindings.ContainsKey(call):
          // a user FUNCTION call: the parameter passed plainly is a BYREF write hazard
          foreach (var arg in call.Arguments) {
            if (arg is not ByValArgExpr && IsParam(arg))
              return true;
            if (ExprMightWrite(arg))
              return true;
          }
          return false;
        case CallOrIndexExpr arrayOrIntrinsic:
          return arrayOrIntrinsic.Arguments.Any(ExprMightWrite);
        case UnaryExpr u:
          return ExprMightWrite(u.Operand);
        case BinaryExpr b:
          return ExprMightWrite(b.Left) || ExprMightWrite(b.Right);
        case ByValArgExpr v:
          return ExprMightWrite(v.Value);
        case FileNumberExpr f:
          return ExprMightWrite(f.Number);
        default:
          // unmodeled node: assume a write if any nested expression might write a
          // parameter (conservative - blocks specialization rather than risk it).
          return AstQuery.Subexpressions(e).Any(ExprMightWrite);
      }
    }

    bool StatementWrites(Statement statement) {
      switch (statement) {
        case AssignStmt a:
          return IsParam(a.Target) || ExprMightWrite(a.Value);
        case IncrDecrStmt id:
          return IsParam(id.Target);
        case SwapStmt sw:
          return IsParam(sw.Left) || IsParam(sw.Right);
        case BitStmt b:
          return IsParam(b.Target);
        case MidAssignStmt m:
          return IsParam(m.Target) || ExprMightWrite(m.Value);
        case InputStmt input:
          return input.Targets.Any(IsParam);
        case ReadStmt read:
          return read.Targets.Any(IsParam);
        case CallStmt call:
          return call.Arguments.Any(arg => (arg is not ByValArgExpr && IsParam(arg)) || ExprMightWrite(arg));
        case PrintStmt p:
          return p.Items.Any(item => ExprMightWrite(item.Value));
        default:
          return false;
      }
    }

    foreach (var statement in body) {
      if (StatementWrites(statement))
        return true;
      foreach (var block in ChildBlocks(statement))
        if (WritesParameter(block, model, parameter))
          return true;
    }
    return false;
  }

  private static IEnumerable<IReadOnlyList<Statement>> ChildBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, body) in i.ElseIfs)
          yield return body;
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
}
