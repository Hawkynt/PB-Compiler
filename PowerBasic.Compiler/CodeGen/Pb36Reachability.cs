using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O22 reachability - which procedures can actually run. A whole program's entry point
/// is its top-level code (the synthetic "main"); tracing the call graph from there - direct
/// calls, CODEPTR / CALL DWORD address references (the binder records those in
/// <see cref="SemanticModel.CallBindings"/> too) and lambdas - yields every reachable
/// procedure. It is <b>transitive</b>: a procedure reached only from other dead procedures
/// is itself dead. The caller drops the unreachable ones (subject to ownership rules).
///
/// Soundness rests on <see cref="DescendantNodes"/> visiting EVERY statement and expression
/// inside a body. It does so by reflection, so it is complete by construction - a newly
/// added AST node is covered automatically and no reference can be silently missed (missing
/// one would wrongly drop live code). It deliberately does NOT descend into separately
/// compiled bodies (a lambda's body and nested SUB/FUNCTION/DEF FN definitions are their own
/// procedures, reached on their own when referenced).
/// </summary>
public static class Pb36Reachability {

  /// <summary>Procedures reachable from <paramref name="rootBody"/> (the program's top-level code) through the call graph.</summary>
  public static HashSet<ProcedureSymbol> LiveProcedures(SemanticModel model, IReadOnlyList<Statement> rootBody) {
    var live = new HashSet<ProcedureSymbol>(ReferenceEqualityComparer.Instance);
    var work = new Queue<IReadOnlyList<Statement>>();
    work.Enqueue(rootBody);

    void Reach(ProcedureSymbol proc) {
      if (proc.Body is { } body && live.Add(proc))
        work.Enqueue(body);
    }

    while (work.Count > 0)
      foreach (var node in DescendantNodes(work.Dequeue())) {
        if (model.CallBindings.TryGetValue(node, out var callee))   // direct call or CODEPTR-family reference
          Reach(callee);
        if (node is LambdaExpr lambda && model.LambdaProcs.TryGetValue(lambda, out var lifted))
          Reach(lifted);
      }

    return live;
  }

  /// <summary>Every <see cref="Statement"/> and <see cref="Expression"/> textually inside <paramref name="root"/>, excluding separately compiled bodies.</summary>
  public static IEnumerable<object> DescendantNodes(object root) {
    var stack = new Stack<object?>();
    PushFlattened(stack, root);
    while (stack.Count > 0) {
      var node = stack.Pop();
      switch (node) {
        case Expression e:
          yield return e;
          if (e is LambdaExpr)
            break;                                  // the lambda body is a separate procedure
          foreach (var prop in PropertiesOf(e.GetType()))
            PushFlattened(stack, prop.GetValue(e));
          break;

        case SubDecl or FunctionDecl or DefFnDecl:
          break;                                    // nested definitions are separate procedures

        case Statement s:
          yield return s;
          foreach (var prop in PropertiesOf(s.GetType()))
            PushFlattened(stack, prop.GetValue(s));
          break;

        default:                                    // AST wrapper records (CaseArm, Parameter, ...) - recurse, do not yield
          if (node is not null && node.GetType().Namespace == AstNamespace)
            foreach (var prop in PropertiesOf(node.GetType()))
              PushFlattened(stack, prop.GetValue(node));
          break;
      }
    }
  }

  private const string AstNamespace = "PowerBasic.Compiler.Syntax.Ast";

  // flatten a property value into the candidate AST objects it holds: nodes directly, list
  // elements, and ValueTuple fields (recursively); scalars/strings/nulls carry no AST nodes.
  private static void PushFlattened(Stack<object?> stack, object? value) {
    switch (value) {
      case null or string:
        return;
      case ITuple tuple:
        for (var i = 0; i < tuple.Length; i++)
          PushFlattened(stack, tuple[i]);
        return;
      case IEnumerable items:
        foreach (var item in items)
          PushFlattened(stack, item);
        return;
      default:
        stack.Push(value);
        return;
    }
  }

  private static readonly Dictionary<Type, PropertyInfo[]> _propertyCache = [];

  private static PropertyInfo[] PropertiesOf(Type type) {
    if (!_propertyCache.TryGetValue(type, out var properties))
      _propertyCache[type] = properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
    return properties;
  }
}
