namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Answers at compile time the string operations whose operands are literals, and drops the ones
/// whose result is the empty string however the argument turns out.
///
/// <para>
/// This is the IR half of three of the direct emitter's string folds - pb36 O0177 (a concatenation of
/// literals is one pooled literal), the literal side of O0299 (a comparison between two literals is a
/// number) and O0266 (a zero-length substring is the empty string). None of them is available to the
/// ordinary constant folders here: a PB string is a runtime HANDLE, so every one of these is spelled
/// as a call, and <see cref="Sccp"/> and <see cref="InstCombine"/> reason about values rather than
/// about what a particular runtime routine means.
/// </para>
///
/// <para>
/// What makes the folds legal is the ownership rule the lowering states: a runtime entry CONSUMES its
/// handle arguments. Folding a call therefore has to account for the handles it was going to eat.
/// Where both operands are literals that is free - the literal's own producing call goes with it, so
/// no handle is ever made - and where an argument is a value from somewhere else the fold RELEASES
/// it, either by cancelling the borrow it came from or by freeing it where the call stood. Getting
/// this wrong does not read as a leak in a small program; it reads as OUT OF STRING SPACE two
/// thousand assignments later, which is the failure mode <c>STRHEAP.BAS</c> exists to catch.
/// </para>
///
/// <para>
/// What it deliberately does not do:
/// </para>
/// <list type="bullet">
///   <item>fold a literal producer with more than one reader. The result is consumed by whoever
///   takes it, so a second reader is a second handle, and one call cannot make two;</item>
///   <item>fold an ORDERING comparison against an equality entry or the reverse - the two routines
///   answer differently and this pass folds each by its own rule;</item>
///   <item>touch a function with an armed error handler or inline assembly, for the reason the pass
///   manager already declines those wholesale.</item>
/// </list>
/// </summary>
public static class StringConstantFold {

  private const string _CONST = "rt_str_const";
  private const string _CONCAT = "rt_str_concat";
  private const string _COMPARE = "rt_str_compare";
  private const string _COMPARE_EQ = "rt_str_compare_eq";
  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";

  /// <summary>Folds what it can across the module; the number of calls folded away.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var folded = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      // to a fixpoint per function: folding `"a" + "b"` makes a literal that the enclosing
      // `+ "c"` can then fold with, and the chain is left-leaning so one sweep sees only the innermost
      for (var sweep = 0; sweep < 8; ++sweep) {
        var changes = FoldOnce(module, function);
        if (changes == 0)
          break;
        folded += changes;
      }
    }
    return folded;
  }

  private static int FoldOnce(IrModule module, IrFunction function) {
    var folded = 0;
    foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
      if (call.Parent is null || call.Callee is not IrFunction callee)
        continue;
      switch (callee.Name) {
        case _CONCAT when call.ArgCount == 2:
          folded += FoldConcat(module, call) ? 1 : 0;
          continue;
        case _COMPARE or _COMPARE_EQ when call.ArgCount == 2:
          folded += FoldCompare(call, callee.Name == _COMPARE_EQ) ? 1 : 0;
          continue;
        case "rt_str_left" or "rt_str_right" when call.ArgCount == 2:
          folded += FoldEmptySubstring(module, call, call.GetOperand(2)) ? 1 : 0;
          continue;
        case "rt_str_mid" when call.ArgCount == 3:
          folded += FoldEmptySubstring(module, call, call.GetOperand(3)) ? 1 : 0;
          continue;
      }
    }
    return folded;
  }

  /// <summary>A concatenation of two literals becomes the one literal it spells.</summary>
  private static bool FoldConcat(IrModule module, IrCall call) {
    if (LiteralOperand(call, 1) is not { } left || LiteralOperand(call, 2) is not { } right)
      return false;

    var bytes = new byte[left.Bytes.Length + right.Bytes.Length];
    left.Bytes.CopyTo(bytes, 0);
    right.Bytes.CopyTo(bytes, left.Bytes.Length);
    var producer = left.Call.Callee;
    var joined = new IrCall(IrType.Ptr, producer,
      [module.AddStringConstant(bytes), new IrConstantInt(IrType.I32, bytes.Length)]);
    call.Parent!.InsertBefore(joined, call);
    call.ReplaceAllUsesWith(joined);
    call.EraseFromParent();
    left.Call.EraseFromParent();      // neither literal is ever made: the joined one replaces both
    right.Call.EraseFromParent();
    return true;
  }

  /// <summary>A comparison between two literals is the number the bytes decide.</summary>
  private static bool FoldCompare(IrCall call, bool equalityOnly) {
    if (LiteralOperand(call, 1) is not { } left || LiteralOperand(call, 2) is not { } right)
      return false;

    var ordering = Compare(left.Bytes, right.Bytes);
    // the equality entry answers 0 or 1, the general one -1, 0 or 1 - each folds to what it would
    // itself have returned, so a reader of either sees no change
    var answer = equalityOnly ? (ordering == 0 ? 0 : 1) : ordering;
    call.ReplaceAllUsesWith(new IrConstantInt(call.Type, answer));
    call.EraseFromParent();
    left.Call.EraseFromParent();
    right.Call.EraseFromParent();
    return true;
  }

  /// <summary>
  /// LEFT$/RIGHT$/MID$ of a length that is constant zero is the empty string, which a PB handle
  /// spells as null - so the call goes and its source handle is released where it stood.
  /// </summary>
  private static bool FoldEmptySubstring(IrModule module, IrCall call, IrValue length) {
    if (length is not IrConstantInt { Value: 0 })
      return false;
    // the handle must be this call's alone. A value with a second reader is a second consumer, and
    // releasing it here would hand that one a freed handle - the fold is only about THIS consumption
    var handle = call.GetOperand(1);
    if (handle is not IrNullPtr && handle.Users.Count != 1)
      return false;

    Release(module, call, handle);
    call.ReplaceAllUsesWith(new IrNullPtr());
    call.EraseFromParent();
    return true;
  }

  /// <summary>
  /// Gives up the handle a folded-away call was going to consume: by cancelling the borrow that made
  /// it, when it came from one and nothing else reads it, and otherwise by freeing it in place.
  ///
  /// Cancelling is not merely the cheaper of the two - <c>rt_str_dup</c> immediately followed by
  /// <c>rt_str_free</c> allocates a copy and hands it straight back, which is what the direct emitter
  /// avoids by never evaluating a folded-away operand at all.
  /// </summary>
  private static void Release(IrModule module, IrCall call, IrValue handle) {
    if (handle is IrCall { Callee: IrFunction { Name: _DUP } } borrow && borrow.Users.Count == 1) {
      borrow.EraseFromParent();
      return;
    }
    if (handle is IrNullPtr)
      return;                          // freeing nothing is a no-op the emitter should not have to write
    call.Parent!.InsertBefore(new IrCall(IrType.Void, FreeEntry(module), [handle]), call);
  }

  /// <summary>The module's <c>rt_str_free</c> declaration, added if this is the first caller.</summary>
  private static IrFunction FreeEntry(IrModule module)
    => module.FindFunction(_FREE)
       ?? module.AddFunction(new IrFunction(_FREE, IrType.Void, [new IrArgument(IrType.Ptr, 0)]));

  /// <summary>The literal an operand was produced by, when it is one and nothing else reads it.</summary>
  private static (IrCall Call, byte[] Bytes)? LiteralOperand(IrCall call, int index) {
    if (call.GetOperand(index) is not IrCall { Callee: IrFunction { Name: _CONST } } producer)
      return null;
    if (producer.Users.Count != 1 || producer.ArgCount != 2)
      return null;                     // a second reader means a second handle, which one call cannot make
    if (producer.GetOperand(1) is not IrGlobalVariable { Bytes: { } bytes })
      return null;
    // the length travels beside the pointer and the fold uses the BYTES, so a length that disagrees
    // with them is not something to guess about
    return producer.GetOperand(2) is IrConstantInt count && count.Value == bytes.Length
      ? (producer, bytes)
      : null;
  }

  /// <summary>
  /// PB's own string ordering: unsigned bytes to the first difference, then the shorter string first.
  /// This is what <c>rt_strcmp</c> computes (<c>REPE CMPSB</c> over the shorter length, then a length
  /// compare), and the answer is the same -1 / 0 / 1 it returns.
  /// </summary>
  private static int Compare(byte[] left, byte[] right) {
    var shared = Math.Min(left.Length, right.Length);
    for (var i = 0; i < shared; ++i)
      if (left[i] != right[i])
        return left[i] < right[i] ? -1 : 1;
    return left.Length == right.Length ? 0 : left.Length < right.Length ? -1 : 1;
  }
}
