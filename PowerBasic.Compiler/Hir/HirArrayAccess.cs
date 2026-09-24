using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>Where an array element's effective dimension bounds come from after binding.</summary>
public enum HirArrayBoundsSource {
  /// <summary>The bounds are constants carried by the bound array type.</summary>
  Static,
  /// <summary>The bounds are runtime state carried by the array/parameter descriptor.</summary>
  Descriptor,
}

/// <summary>One compile-time array dimension after OPTION BASE and constant folding.</summary>
public readonly record struct HirStaticArrayBound(int Lower, int Upper);

/// <summary>
/// One semantically resolved array element access before address arithmetic is chosen.
///
/// <para>
/// HIR retains the facts that are source semantics: the resolved array symbol/type/class, source-order
/// subscript expressions, where bounds come from, and whether Error 9 bounds checking applies at this
/// program point. It deliberately does not encode near/far address formation, descriptor layout, EMS
/// page mapping, element stride arithmetic or the first-subscript-fastest Horner fold; those are MIR/SSA
/// lowering decisions.
/// </para>
/// </summary>
public sealed record HirArrayElementAccess(
  VariableSymbol Target,
  ArrayType Type,
  ArrayClass ArrayClass,
  IReadOnlyList<Expression> Subscripts,
  HirArrayBoundsSource BoundsSource,
  IReadOnlyList<HirStaticArrayBound> StaticBounds,
  bool CheckBounds) {

  public PbType ElementType => this.Type.Element;
}

/// <summary>Builds resolved array-element HIR from binder-owned identity/type facts exactly once.</summary>
public static class HirArrayAccessBuilder {

  public static bool TryBuild(
      SemanticModel model,
      CallOrIndexExpr expression,
      bool checkBounds,
      out HirArrayElementAccess access,
      out string? error) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(expression);

    if (!model.VariableBindings.TryGetValue(expression, out var symbol)
        || symbol.Type is not ArrayType array) {
      access = null!;
      error = $"not an array element: {expression.Name}";
      return false;
    }

    if (expression.Arguments.Count != array.Rank) {
      access = null!;
      error = $"array rank mismatch for {symbol.Name}: expected {array.Rank}, got {expression.Arguments.Count}";
      return false;
    }

    var memoryModel = symbol.ArrayClass is
      ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms;
    var descriptorBacked = array.IsDynamic
      || symbol.Storage == VariableStorage.Parameter
      || symbol.ArrayClass == ArrayClass.Absolute
      || memoryModel;

    IReadOnlyList<HirStaticArrayBound> staticBounds = [];
    if (!descriptorBacked) {
      if (array.StaticBounds is not { } bounds || bounds.Count != array.Rank) {
        access = null!;
        error = $"static array {symbol.Name} has no complete bound contract";
        return false;
      }
      staticBounds = [.. bounds.Select(bound => new HirStaticArrayBound(bound.Lower, bound.Upper))];
    }

    // Fidelity boundary: the direct HUGE/VIRTUAL/EMS/XMS element path does not emit $ERROR BOUNDS
    // checks either. Record the effective semantics explicitly instead of letting MIR rediscover the
    // exception from an allocation class later.
    var effectiveBoundsCheck = checkBounds && !memoryModel;

    access = new(
      symbol,
      array,
      symbol.ArrayClass,
      [.. expression.Arguments],
      descriptorBacked ? HirArrayBoundsSource.Descriptor : HirArrayBoundsSource.Static,
      staticBounds,
      effectiveBoundsCheck);
    error = null;
    return true;
  }
}
