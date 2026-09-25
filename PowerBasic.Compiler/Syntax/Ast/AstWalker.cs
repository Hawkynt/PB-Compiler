using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PowerBasic.Compiler.Syntax.Ast;

/// <summary>
/// Walks every <see cref="Statement"/> and <see cref="Expression"/> inside a body. It does so by
/// reflection, so it is complete by construction - a newly added AST node is covered automatically,
/// and an analysis built on it cannot silently miss a reference. It deliberately does NOT descend into
/// separately compiled bodies: a lambda's body and nested SUB/FUNCTION/DEF FN definitions are
/// procedures of their own.
/// </summary>
public static class AstWalker {

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
