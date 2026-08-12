namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Turns a concatenation whose LEFT operand is a fresh, dead string temporary into an APPEND onto
/// that temporary, so a build loop copies each addition once instead of recopying everything it has
/// built so far.
///
/// <para>
/// This is the IR half of the direct emitter's pb36 O9. The plain <c>rt_str_concat</c> allocates a
/// result and copies BOTH operands into it, which makes <c>s$ = s$ + x$</c> in a loop O(n²) in bytes
/// moved. The runtime's append entries grow the left operand in place when it is the topmost heap
/// block - the same handle comes back - and fall back to the allocate-and-copy path when it is not,
/// so the value produced is identical either way and only the work differs.
/// </para>
///
/// <para>
/// Two rewrites, and the first is what makes the second reach anything:
/// </para>
/// <list type="bullet">
///   <item><b>A borrow whose source dies at the concatenation is cancelled.</b> The lowering copies
///   every read of a string variable (<c>rt_str_dup</c>) because the consuming routines free what
///   they are given, and it frees the variable's previous value when a new one is stored. In
///   <c>s$ = s$ + …</c> those are the same handle: a copy is made, the original is freed, and the
///   copy is consumed. Handing the ORIGINAL to the concatenation does both jobs at once - it is
///   consumed there, which is what the free was for - so the copy and the free both go.</item>
///   <item><b>A right operand that is a borrow becomes a plain read.</b> The append entry copies the
///   source's bytes without taking ownership of them, so the copy the lowering made to be consumed is
///   not needed: the variable's own handle is passed and stays the variable's.</item>
/// </list>
///
/// <para>
/// The left operand must be a call that ALLOCATES - a literal, a previous concatenation or append, a
/// substring. That is what "fresh and dead" means here: nothing else can be holding the block the
/// append is about to grow. A borrow (<c>rt_str_dup</c>) is excluded even though it allocates too,
/// which is the direct emitter's boundary rather than a soundness one: where the borrow's source is
/// dead the first rewrite has already removed it, and where it is not the copy exists precisely
/// because the value belongs to storage someone else can still read.
/// </para>
///
/// <para>
/// Runs AFTER <see cref="StringConcatChain"/>. A chain of three or more operands is better built with
/// one allocation than with a series of appends, and the chain pass has flattened those away by the
/// time this sees them.
/// </para>
/// </summary>
public static class StringAppendInPlace {

  private const string _CONCAT = "rt_str_concat";
  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";
  private const string _CONST = "rt_str_const";
  private const string _APPEND_VAR = "rt_str_append_var";
  private const string _APPEND_LIT = "rt_str_append_lit";

  /// <summary>
  /// The runtime entries that answer with a handle to a BLOCK OF THEIR OWN, which no other value
  /// names. Growing one in place is therefore invisible to everything else.
  /// </summary>
  private static readonly HashSet<string> _freshAllocations = new(StringComparer.Ordinal) {
    _CONST, _CONCAT, _APPEND_VAR, _APPEND_LIT,
    "rt_str_concat_n", "rt_str_left", "rt_str_right", "rt_str_mid", "rt_str_mid2",
    "rt_str_ucase", "rt_str_lcase", "rt_str_ltrim", "rt_str_rtrim",
    "rt_str_space", "rt_str_string", "rt_str_string_s", "rt_str_chr",
  };

  /// <summary>Rewrites what it can across the module; the number of concatenations changed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var changed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || call.Callee is not IrFunction { Name: _CONCAT } || call.ArgCount != 2)
          continue;
        var touched = CancelDyingBorrow(call, 1) | CancelDyingBorrow(call, 2);
        touched |= AppendLiteral(module, call) || AppendVariable(module, call);
        if (touched)
          ++changed;
      }
    }
    return changed;
  }

  /// <summary>
  /// Replaces <c>rt_str_dup(v)</c> at <paramref name="index"/> with <c>v</c> itself when the
  /// concatenation is the copy's only reader and the only other thing done with <c>v</c> is a free
  /// AFTER the concatenation - the free the concatenation now performs by consuming it.
  /// </summary>
  private static bool CancelDyingBorrow(IrCall concat, int index) {
    if (concat.GetOperand(index) is not IrCall { Callee: IrFunction { Name: _DUP } } borrow)
      return false;
    if (borrow.Users.Count != 1 || borrow.ArgCount != 1)
      return false;
    var source = borrow.GetOperand(1);
    if (source.Users.Count != 2)
      return false;
    if (source.Users.FirstOrDefault(u => !ReferenceEquals(u, borrow)) is not
        IrCall { Callee: IrFunction { Name: _FREE } } free)
      return false;
    // the free has to come after the concatenation, in the same block: that is what says the value is
    // still the variable's up to this point and nothing between the two reads it
    var block = concat.Parent!;
    if (!ReferenceEquals(free.Parent, block))
      return false;
    if (IndexIn(block, free) < IndexIn(block, concat))
      return false;

    concat.SetOperand(index, source);
    borrow.EraseFromParent();
    free.EraseFromParent();
    return true;
  }

  /// <summary>The instruction's position in its block (<see cref="IrBasicBlock.Instructions"/> is not a list).</summary>
  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }

  /// <summary>Appends a literal's bytes onto a dead left temporary, without making the literal a handle first.</summary>
  private static bool AppendLiteral(IrModule module, IrCall concat) {
    if (!IsGrowable(concat.GetOperand(1)))
      return false;
    if (concat.GetOperand(2) is not IrCall { Callee: IrFunction { Name: _CONST } } literal)
      return false;
    if (literal.Users.Count != 1 || literal.ArgCount != 2)
      return false;

    var entry = Declare(module, _APPEND_LIT, IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I32);
    var append = new IrCall(IrType.Ptr, entry,
      [concat.GetOperand(1), literal.GetOperand(1), literal.GetOperand(2)]);
    concat.Parent!.InsertBefore(append, concat);
    concat.ReplaceAllUsesWith(append);
    concat.EraseFromParent();
    literal.EraseFromParent();
    return true;
  }

  /// <summary>Appends another string's bytes onto a dead left temporary, borrowing rather than consuming them.</summary>
  private static bool AppendVariable(IrModule module, IrCall concat) {
    if (!IsGrowable(concat.GetOperand(1)))
      return false;
    if (concat.GetOperand(2) is not IrCall { Callee: IrFunction { Name: _DUP } } borrow)
      return false;
    if (borrow.Users.Count != 1 || borrow.ArgCount != 1)
      return false;

    var entry = Declare(module, _APPEND_VAR, IrType.Ptr, IrType.Ptr, IrType.Ptr);
    var append = new IrCall(IrType.Ptr, entry, [concat.GetOperand(1), borrow.GetOperand(1)]);
    concat.Parent!.InsertBefore(append, concat);
    concat.ReplaceAllUsesWith(append);
    concat.EraseFromParent();
    borrow.EraseFromParent();
    return true;
  }

  /// <summary>Whether the value is a freshly allocated block this concatenation is the last reader of.</summary>
  private static bool IsGrowable(IrValue value)
    => value is IrCall { Callee: IrFunction callee } producer
       && producer.Users.Count == 1
       && _freshAllocations.Contains(callee.Name);

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
