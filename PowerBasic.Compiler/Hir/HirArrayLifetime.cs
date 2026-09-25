using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>One array bound after binding-time defaults such as OPTION BASE have been made explicit.</summary>
public sealed record HirArrayBound(Expression? Lower, Expression Upper);

/// <summary>
/// Array identity and storage semantics retained across the Bound AST -> HIR boundary. The operation
/// names a resolved symbol rather than a source name, so later lowering never repeats binding.
/// </summary>
public abstract record HirArrayLifetimeOperation(
  VariableSymbol Target,
  ArrayType Type,
  ArrayClass ArrayClass);

/// <summary>Semantic REDIM operation, including the effective bounds and PRESERVE intent.</summary>
public sealed record HirArrayResize(
  VariableSymbol Target,
  ArrayType Type,
  ArrayClass ArrayClass,
  IReadOnlyList<HirArrayBound> Bounds,
  bool Preserve)
  : HirArrayLifetimeOperation(Target, Type, ArrayClass);

/// <summary>Semantic ERASE operation over one already-resolved array identity.</summary>
public sealed record HirArrayErase(
  VariableSymbol Target,
  ArrayType Type,
  ArrayClass ArrayClass)
  : HirArrayLifetimeOperation(Target, Type, ArrayClass);

/// <summary>
/// First incremental Bound AST -> HIR builder. It consumes binder side tables once and captures the
/// semantics that SSA construction previously rediscovered for REDIM/ERASE: symbol identity, array
/// type/class, rank, effective OPTION BASE and PRESERVE intent.
/// </summary>
public static class HirArrayLifetimeBuilder {

  public static bool TryBuild(
      SemanticModel model,
      RedimStmt statement,
      out IReadOnlyList<HirArrayResize> operations,
      out string? error) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(statement);

    var result = new List<HirArrayResize>(statement.Variables.Count);
    foreach (var declaration in statement.Variables) {
      if (!model.RedimBindings.TryGetValue(declaration, out var symbol)
          || symbol.Type is not ArrayType { IsDynamic: true } array) {
        operations = [];
        error = $"REDIM of non-dynamic array {declaration.Name}";
        return false;
      }

      if (declaration.ArrayBounds is not { } sourceBounds || sourceBounds.Count != array.Rank) {
        operations = [];
        error = $"REDIM rank mismatch for {declaration.Name}";
        return false;
      }

      var bounds = model.ArrayBoundsOf(declaration);
      if (bounds.Count != array.Rank) {
        operations = [];
        error = $"REDIM effective-bound rank mismatch for {declaration.Name}";
        return false;
      }

      result.Add(new(
        symbol,
        array,
        symbol.ArrayClass,
        [.. bounds.Select(bound => new HirArrayBound(bound.Lower, bound.Upper))],
        statement.Preserve));
    }

    operations = result;
    error = null;
    return true;
  }

  public static bool TryBuild(
      SemanticModel model,
      EraseStmt statement,
      out IReadOnlyList<HirArrayErase> operations,
      out string? error) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(statement);

    var result = new List<HirArrayErase>(statement.Arrays.Count);
    foreach (var expression in statement.Arrays) {
      if (!model.VariableBindings.TryGetValue(expression, out var symbol)
          || symbol.Type is not ArrayType array) {
        operations = [];
        error = "ERASE of a non-array";
        return false;
      }
      result.Add(new(symbol, array, symbol.ArrayClass));
    }

    operations = result;
    error = null;
    return true;
  }
}
