namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0293 — removes an eager string duplicate when two SSA names can share one immutable handle until
/// one of their ownership lifetimes ends.
///
/// <para>
/// Lowering gives every string variable its own owned handle. Consequently <c>b$ = a$</c> reads
/// <c>a$</c> through <c>rt_str_dup</c>, and after <see cref="Mem2Reg"/> that duplicate becomes the SSA
/// value representing <c>b$</c>. Reads of either variable duplicate that owned value again before a
/// consuming runtime call, while replacement/scope exit ends the value's lifetime with
/// <c>rt_str_free</c>.
/// </para>
///
/// <para>
/// For a straight-line pair of such lifetimes, the assignment duplicate is unnecessary. Both SSA
/// names may reference the same immutable handle; the earlier of their two releases merely drops one
/// alias and is removed, while the later release remains the single owner release. If either variable
/// is subsequently changed, lowering already reads its old value through <c>rt_str_dup</c> before the
/// replacement free, so the actual byte copy happens at that first mutation rather than at assignment.
/// </para>
///
/// <para>
/// This first slice is deliberately local and proof-driven: every use of both owned values must be in
/// one basic block, and the only raw uses allowed are <c>rt_str_dup</c> and exactly one
/// <c>rt_str_free</c>. Calls that consume/escape the raw handle, phis and cross-block lifetimes all
/// decline. Full path-sensitive sharing or runtime reference counts remain separate extensions rather
/// than guesses hidden inside this transform.
/// </para>
/// </summary>
public static class StringCopyOnWriteElision {

  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";

  /// <summary>Elides provably delayable ownership copies in <paramref name="fn"/>; returns the number removed.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.Entry is null || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = 0;
    foreach (var copy in fn.AllInstructions.OfType<IrCall>().ToList()) {
      if (copy.Parent is null || !IsCall(copy, _DUP, out var source))
        continue;
      if (TryElide(copy, source!))
        ++changed;
    }
    return changed;
  }

  private static bool TryElide(IrCall copy, IrValue source) {
    var block = copy.Parent!;
    if (!TryLifetime(source, block, out var sourceFree)
        || !TryLifetime(copy, block, out var copyFree))
      return false;

    var copyIndex = IndexIn(block, copy);
    if (copyIndex < 0)
      return false;

    // A lifetime the function never ends outlives every local one, so it sorts after any release.
    var sourceFreeIndex = sourceFree is null ? int.MaxValue : IndexIn(block, sourceFree);
    var copyFreeIndex = copyFree is null ? int.MaxValue : IndexIn(block, copyFree);
    if (sourceFreeIndex <= copyIndex || copyFreeIndex <= copyIndex)
      return false;

    // Repoint the copied name first. This also rewrites its surviving later free to the shared handle.
    copy.ReplaceAllUsesWith(source);
    copy.EraseFromParent();

    // Before sharing there were two independently owned blocks and therefore up to two frees.
    // Afterwards there is one block: the earlier lifetime death drops only a name, and the later one
    // owns the actual release. Keeping the later free also keeps the shared handle valid for either
    // name's intervening borrows. When the later lifetime is never ended, the surviving block is one
    // the function already leaked, so no release is left behind either.
    (sourceFreeIndex < copyFreeIndex ? sourceFree : copyFree)?.EraseFromParent();
    return true;
  }

  /// <summary>
  /// Proves a local immutable ownership lifetime: only borrowed duplicates plus at most one final
  /// free may observe the raw handle, and every borrow must precede that free in the same block.
  /// A value with no release at all still has a provable lifetime - the function simply never ends
  /// it, so <paramref name="free"/> stays null and the lifetime outlives every local one.
  /// </summary>
  private static bool TryLifetime(IrValue value, IrBasicBlock block, out IrCall? free) {
    free = null;
    var users = value.Users
      .Distinct<IrInstruction>(ReferenceEqualityComparer.Instance)
      .ToList();
    IrCall? release = null;
    foreach (var user in users) {
      if (!ReferenceEquals(user.Parent, block))
        return false;
      if (IsCall(user, _DUP, out var duplicated) && ReferenceEquals(duplicated, value))
        continue;
      if (IsCall(user, _FREE, out var released) && ReferenceEquals(released, value)) {
        if (release is not null)
          return false;
        release = (IrCall)user;
        continue;
      }
      return false;
    }

    if (release is null)
      return true;

    var freeIndex = IndexIn(block, release);
    if (freeIndex < 0 || users.Any(user => !ReferenceEquals(user, release) && IndexIn(block, user) >= freeIndex))
      return false;

    free = release;
    return true;
  }

  private static bool IsCall(IrInstruction instruction, string name, out IrValue? argument) {
    argument = null;
    if (instruction is not IrCall { Callee: IrFunction callee } call || callee.Name != name || call.ArgCount != 1)
      return false;
    argument = call.GetOperand(1);
    return true;
  }

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
