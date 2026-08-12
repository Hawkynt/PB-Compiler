namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Builds a chain of three or more string concatenations with ONE allocation instead of one per
/// node.
///
/// <para>
/// This is the IR half of the direct emitter's pb36 O24. <c>a$ + b$ + c$ + d$</c> lowered pairwise
/// allocates three results and copies the growing prefix into each of them, which is O(n²) in bytes
/// moved for O(n) of output. The runtime's <c>rt_str_concat_n</c> sums every operand's length first,
/// reserves the result once and copies each operand in exactly once; it consumes every operand
/// handle, which is precisely what the chain of pairwise concatenations would have done, so the
/// string produced and the temporaries released are the same.
/// </para>
///
/// <para>
/// The flattening descends into an operand only when that operand is itself a concatenation whose
/// ONLY reader is this one. A shared intermediate is a value the program uses twice, and a chain
/// that consumed it would leave the other reader holding a freed handle.
/// </para>
///
/// <para>
/// A leaf may only be a literal (<c>rt_str_const</c>) or a borrow of storage (<c>rt_str_dup</c>).
/// This is the same restriction the direct emitter states and it is about ORDER rather than about
/// ownership: the single-allocation builder wants every operand staged before any of them is copied,
/// and a leaf that came from a call may be sharing a result buffer the next call would overwrite -
/// <c>f$() &amp; g$() &amp; h$()</c> reading "hhh" is the failure it is guarding against. A literal
/// and a copy of a variable are each an independent block, so staging them is safe. Anything else
/// keeps the pairwise path, which consumes each operand as soon as it has it.
/// </para>
///
/// <para>
/// Runs BEFORE <see cref="StringAppendInPlace"/>: one allocation for the whole chain beats a series
/// of in-place appends, and the append pass would otherwise consume the shapes this one is looking
/// for. Two operands are left alone - the builder's fixed cost is not worth paying for a single
/// concatenation, which is the boundary the direct emitter draws in the same place.
/// </para>
/// </summary>
public static class StringConcatChain {

  private const string _CONCAT = "rt_str_concat";
  private const string _CONCAT_N = "rt_str_concat_n";
  private const string _CONST = "rt_str_const";
  private const string _DUP = "rt_str_dup";

  /// <summary>The runtime's staging list holds this many handles; a longer chain stays pairwise.</summary>
  private const int _MAX_OPERANDS = 64;

  /// <summary>Collapses qualifying chains; the number collapsed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var collapsed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || !IsConcat(call))
          continue;
        if (IsInnerNode(call))
          continue;                    // its parent is the root; the whole tree is collapsed from there
        if (Flatten(call) is not { } leaves)
          continue;
        Collapse(module, call, leaves);
        ++collapsed;
      }
    }
    return collapsed;
  }

  private static bool IsConcat(IrValue value)
    => value is IrCall { Callee: IrFunction { Name: _CONCAT }, ArgCount: 2 };

  /// <summary>Whether this concatenation feeds another one, which is then the root of the tree.</summary>
  private static bool IsInnerNode(IrCall concat)
    => concat.Users.Count == 1 && IsConcat(concat.Users[0]);

  /// <summary>
  /// The chain's leaves in evaluation order, or null when it is too short, too long, or contains an
  /// operand that may not be staged.
  /// </summary>
  private static List<IrValue>? Flatten(IrCall root) {
    var leaves = new List<IrValue>();
    return Collect(root) && leaves.Count is >= 3 and <= _MAX_OPERANDS ? leaves : null;

    bool Collect(IrValue value) {
      if (value is IrCall inner && IsConcat(inner) && (ReferenceEquals(inner, root) || inner.Users.Count == 1))
        return Collect(inner.GetOperand(1)) && Collect(inner.GetOperand(2));
      if (value is not IrCall { Callee: IrFunction { Name: _CONST or _DUP } } leaf || leaf.Users.Count != 1)
        return false;
      leaves.Add(leaf);
      return leaves.Count <= _MAX_OPERANDS;
    }
  }

  private static void Collapse(IrModule module, IrCall root, List<IrValue> leaves) {
    var entry = module.FindFunction(_CONCAT_N)
      ?? module.AddFunction(new IrFunction(_CONCAT_N, IrType.Ptr, [new IrArgument(IrType.I32, 0)]) {
        IsVarArgs = true,
      });
    IrValue[] arguments = [new IrConstantInt(IrType.I32, leaves.Count), .. leaves];
    var built = new IrCall(IrType.Ptr, entry, arguments);
    root.Parent!.InsertBefore(built, root);
    root.ReplaceAllUsesWith(built);
    // erase the tree from the root down: each node's operands lose their user as it goes, which is
    // what leaves the inner concatenations unused by the time they are reached
    Erase(root);
    return;

    void Erase(IrCall node) {
      var left = node.GetOperand(1);
      var right = node.GetOperand(2);
      node.EraseFromParent();
      foreach (var operand in new[] { left, right })
        if (operand is IrCall child && IsConcat(child) && child.HasNoUsers)
          Erase(child);
    }
  }
}
