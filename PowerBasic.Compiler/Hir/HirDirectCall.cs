using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>
/// One binder-resolved argument of a direct user procedure call.
///
/// <para>
/// The expression remains high-level because evaluation has not happened yet. Parameter identity, type and
/// BYVAL/BYREF are frozen here so later lowering never has to reinterpret the procedure declaration or named
/// argument syntax. Aggregate/string/array ABI representation is deliberately left to MIR/SSA lowering.
/// </para>
/// </summary>
public sealed record HirDirectCallArgument(
  Expression Value,
  VariableSymbol Parameter,
  PbType ParameterType,
  bool ByValue);

/// <summary>
/// One statically resolved SUB/FUNCTION call after binding but before ABI-shaped argument lowering.
/// </summary>
public sealed record HirDirectCall(
  ProcedureSymbol Target,
  IReadOnlyList<HirDirectCallArgument> Arguments,
  bool IsFunction,
  PbType? ReturnType,
  CallConvention CallConvention);

/// <summary>
/// Consumes binder-owned call identity and argument-ordering side tables exactly once.
/// </summary>
public static class HirDirectCallBuilder {

  public static bool TryBuild(
      SemanticModel model,
      object callSite,
      IReadOnlyList<Expression> writtenArguments,
      out HirDirectCall call,
      out string? error) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(callSite);
    ArgumentNullException.ThrowIfNull(writtenArguments);

    if (!model.CallBindings.TryGetValue(callSite, out var target)) {
      call = null!;
      error = "call site has no resolved user procedure";
      return false;
    }

    var positional = ResolvePositionalArguments(model, callSite, target, writtenArguments);
    if (positional.Count != target.Parameters.Count) {
      call = null!;
      error = $"argument count mismatch for {target.Name}: expected {target.Parameters.Count}, got {positional.Count}";
      return false;
    }

    var arguments = new HirDirectCallArgument[positional.Count];
    for (var i = 0; i < positional.Count; ++i) {
      var parameter = target.Parameters[i];
      arguments[i] = new(
        positional[i],
        parameter,
        parameter.Type,
        parameter.ByVal);
    }

    call = new(
      target,
      arguments,
      target.IsFunction,
      target.ReturnType,
      target.CallConv);
    error = null;
    return true;
  }

  private static IReadOnlyList<Expression> ResolvePositionalArguments(
      SemanticModel model,
      object callSite,
      ProcedureSymbol target,
      IReadOnlyList<Expression> writtenArguments) {
    if (model.ReorderedArguments.TryGetValue(callSite, out var reordered))
      return reordered;

    // PB 3.6 named arguments are already fully reordered/default-filled by the binder. A plain call
    // with omitted trailing defaults has no ReorderedArguments entry, so complete it here exactly once.
    // CDECL's optional/bracketed argument surface remains outside this lowering's supported contract.
    if (writtenArguments.Count >= target.Parameters.Count
        || target.IsCdecl
        || target.Parameters[writtenArguments.Count].DefaultValue is null)
      return writtenArguments;

    var filled = new List<Expression>(writtenArguments);
    for (var i = writtenArguments.Count;
         i < target.Parameters.Count && target.Parameters[i].DefaultValue is { } value;
         ++i)
      filled.Add(value);
    return filled;
  }
}
