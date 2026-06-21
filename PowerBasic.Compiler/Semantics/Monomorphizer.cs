using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>
/// pb36 compile-time generics (monomorphization). A generic <c>TYPE Name OF T … END TYPE</c> is a
/// template; every concrete use (<c>AS Name OF LONG</c>) is vivified into an ordinary concrete TYPE
/// named with an untypeable mangle (<c>Name@LONG</c>) by substituting the type argument throughout a
/// deep clone of the template (fields, member signatures and member bodies - the clone gives each
/// instantiation fresh AST node identities so the binder's per-node annotations never collide). The
/// work-list runs to a fixpoint so an instantiation that itself uses another generic is covered too.
/// After this pass the program contains only concrete types; the binder resolves a generic-use type
/// name to its mangle via <see cref="Mangle"/> - no back-end change.
/// </summary>
internal static class Monomorphizer {

  /// <summary>The concrete name a generic use lowers to: <c>Stack OF LONG</c> → <c>Stack@LONG</c>, nesting recursively. The <c>@</c> separators cannot occur in a user identifier, so the name never collides.</summary>
  public static string Mangle(TypeName use) => MangleName(use.UserTypeName!, use.TypeArguments!);

  /// <summary>The mangled concrete name for a base name applied to concrete type arguments (shared by generic TYPEs and generic procedures).</summary>
  public static string MangleName(string baseName, IReadOnlyList<TypeName> args)
    => baseName + string.Concat(args.Select(a => "@" + MangleArg(a)));

  /// <summary>Deep-clones an AST node, substituting each bare type-parameter type name with its concrete argument and giving every node a fresh identity (so the binder's per-node annotations never collide across instantiations).</summary>
  public static object? SubstituteClone(object? node, IReadOnlyDictionary<string, TypeName> map) => Clone(node, map);

  private static string MangleArg(TypeName a) {
    var core = a.IsGenericUse ? Mangle(a) : a.IsUserDefined ? a.UserTypeName! : a.Builtin.ToString();
    return a.IsPointer ? MangleArg(a.PointerTarget!) + "Ptr" : core;
  }

  /// <summary>
  /// Expands every generic instantiation in <paramref name="unit"/>. Returns the new statement list
  /// (generic templates removed, concrete instantiations appended) and whether anything was generic.
  /// </summary>
  public static (IReadOnlyList<Statement> Statements, bool Any, IReadOnlyDictionary<string, (string Template, IReadOnlyList<TypeName> Args)> Instances) Expand(CompilationUnit unit, Action<SourcePosition, string> error) {
    var instanceArgs = new Dictionary<string, (string, IReadOnlyList<TypeName>)>(StringComparer.OrdinalIgnoreCase);
    var templates = new Dictionary<string, TypeDecl>(StringComparer.OrdinalIgnoreCase);
    var rest = new List<Statement>();
    foreach (var s in unit.Statements)
      if (s is TypeDecl { TypeParameters.Count: > 0 } generic)
        templates[generic.Name] = generic;
      else
        rest.Add(s);
    if (templates.Count == 0)
      return (unit.Statements, false, instanceArgs);

    var concrete = new Dictionary<string, TypeDecl>(StringComparer.OrdinalIgnoreCase);
    var queue = new Queue<TypeName>();
    // skip generic PROCEDURE templates: a "Box OF T" in their signature/body uses an abstract type
    // parameter, not a concrete instantiation (their instances are monomorphized later by the binder)
    var scannable = rest.Where(s => s is not (SubDecl { TypeParameters.Count: > 0 } or FunctionDecl { TypeParameters.Count: > 0 }));
    foreach (var use in GenericUses(scannable, templates))
      queue.Enqueue(use);

    while (queue.Count > 0) {
      var use = queue.Dequeue();
      var mangled = Mangle(use);
      if (concrete.ContainsKey(mangled) || !templates.TryGetValue(use.UserTypeName!, out var template))
        continue;
      if (template.TypeParameters.Count != use.TypeArguments!.Count) {
        error(use.Position, $"generic TYPE {use.UserTypeName} takes {template.TypeParameters.Count} type argument(s), got {use.TypeArguments.Count}");
        continue;
      }
      var map = new Dictionary<string, TypeName>(StringComparer.OrdinalIgnoreCase);
      for (var i = 0; i < template.TypeParameters.Count; ++i)
        map[template.TypeParameters[i]] = use.TypeArguments[i];
      var instance = ((TypeDecl)Clone(template, map)!) with { Name = mangled, TypeParameters = [] };
      concrete[mangled] = instance;
      instanceArgs[mangled] = (use.UserTypeName!, use.TypeArguments!);   // remember origin for nested inference
      foreach (var inner in GenericUses([instance], templates))   // transitive instantiations
        queue.Enqueue(inner);
    }

    // splice the instantiations in right after the last type-defining declaration - so they are
    // scanned before any DIM / executable statement that uses them, but after the user TYPEs they may
    // reference. Dependency order (an instantiation discovered from another's body comes later) is
    // reversed so a referenced instantiation is defined before the one that needs it.
    var instances = concrete.Values.Reverse().ToList();
    var insertAt = 0;
    for (var i = 0; i < rest.Count; ++i)
      if (rest[i] is TypeDecl or UnionDecl or EnumDecl or DefTypeStmt)
        insertAt = i + 1;
    var result = new List<Statement>(rest);
    result.InsertRange(insertAt, instances);
    return (result, true, instanceArgs);
  }

  /// <summary>Every generic-use type name (a use of one of <paramref name="templates"/> with arguments) anywhere in the given statements.</summary>
  private static IEnumerable<TypeName> GenericUses(IEnumerable<Statement> statements, Dictionary<string, TypeDecl> templates) {
    var found = new List<TypeName>();
    foreach (var s in statements)
      CollectTypeNames(s, found);
    return found.Where(t => t.IsGenericUse && templates.ContainsKey(t.UserTypeName!));
  }

  // ---- reflection walkers over the AST node graph ----

  private static void CollectTypeNames(object? node, List<TypeName> sink) {
    switch (node) {
      case null or string or bool or Enum or SourcePosition:
        return;
      case TypeName tn:
        sink.Add(tn);
        foreach (var child in ChildObjects(tn))
          CollectTypeNames(child, sink);
        return;
      case ITuple tuple:
        for (var i = 0; i < tuple.Length; ++i)
          CollectTypeNames(tuple[i], sink);
        return;
      case IEnumerable list:
        foreach (var item in list)
          CollectTypeNames(item, sink);
        return;
      default:
        if (node.GetType().IsValueType && node.GetType().Namespace?.StartsWith("PowerBasic") != true)
          return;                                   // a foreign struct (no AST children)
        foreach (var child in ChildObjects(node))
          CollectTypeNames(child, sink);
        return;
    }
  }

  /// <summary>Deep-clones an AST node, replacing a bare type-parameter type name with its argument and recursing through everything else; immutable scalars pass through. Gives every node a fresh identity.</summary>
  private static object? Clone(object? node, IReadOnlyDictionary<string, TypeName> map) {
    switch (node) {
      case null or string or bool or Enum or SourcePosition or ValueType when node is not ITuple:
        return node;                                // immutable scalar / foreign struct
      case TypeName { IsUserDefined: true } tn when !tn.IsGenericUse && map.TryGetValue(tn.UserTypeName!, out var replacement):
        return replacement;                         // a bare T -> the concrete argument type
      case ITuple tuple: {
        var items = new object?[tuple.Length];
        for (var i = 0; i < tuple.Length; ++i)
          items[i] = Clone(tuple[i], map);
        return Activator.CreateInstance(node.GetType(), items);
      }
      case IEnumerable list:
        return CloneList(list, map);
      default:
        return CloneRecord(node, map);
    }
  }

  private static object CloneList(IEnumerable list, IReadOnlyDictionary<string, TypeName> map) {
    var element = list.GetType() is { IsGenericType: true } g ? g.GetGenericArguments()[0] : typeof(object);
    var typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
    foreach (var item in list)
      typed.Add(Clone(item, map));
    return typed;
  }

  private static object CloneRecord(object node, IReadOnlyDictionary<string, TypeName> map) {
    var type = node.GetType();
    var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
    var args = ctor.GetParameters()
      .Select(p => Clone(type.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!.GetValue(node), map))
      .ToArray();
    var clone = ctor.Invoke(args);
    // init-only / settable properties that are not constructor parameters (e.g. TypeDecl.Members)
    var ctorNames = new HashSet<string>(ctor.GetParameters().Select(p => p.Name!), StringComparer.OrdinalIgnoreCase);
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
      if (prop.CanWrite && prop.GetSetMethod(nonPublic: true) != null && !ctorNames.Contains(prop.Name))
        prop.SetValue(clone, Clone(prop.GetValue(node), map));
    return clone;
  }

  /// <summary>The AST-bearing child objects of a record node (constructor properties + extra writable properties), for the collectors.</summary>
  private static IEnumerable<object?> ChildObjects(object node) {
    var type = node.GetType();
    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
      if (prop.GetIndexParameters().Length == 0)
        yield return prop.GetValue(node);
  }
}
