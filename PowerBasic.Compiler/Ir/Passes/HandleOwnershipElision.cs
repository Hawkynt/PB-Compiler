namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Eliminates balanced string-handle ownership copies whose lifetime is completely visible in one
/// basic block.
///
/// <para>
/// Dynamic-string storage owns its handle. Reading that storage therefore lowers to
/// <c>rt_str_dup(owner)</c>, while replacing or leaving the storage lowers to
/// <c>rt_str_free(owner)</c>. After <see cref="Mem2Reg"/> promotes ordinary local storage, an
/// assignment such as <c>a$ = b$</c> can leave a second ownership layer whose only observations are
/// further <c>rt_str_dup</c> calls made for reads of <c>a$</c>. If the source owner provably remains
/// alive for that whole interval, those reads can borrow the source owner directly and the assignment
/// copy plus its matching release cancel out.
/// </para>
///
/// <para>
/// The proof is intentionally local. The owned copy and its release must be in the same block; every
/// intervening use of the copy must itself be <c>rt_str_dup</c>; and when such reads exist the source
/// must be an SSA handle whose complete ownership use-list is visible in that block and whose own
/// release comes later. Loads are rejected because their storage may be changed through an alias that
/// does not appear in the loaded SSA value's use-list. CFG-wide ownership pairing belongs to a later
/// extension rather than to a speculative first implementation.
/// </para>
/// </summary>
public static class HandleOwnershipElision {

  private const string _DUP = "rt_str_dup";
  private const string _FREE = "rt_str_free";

  /// <summary>Elides every locally proven ownership pair; returns the number of pairs removed.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.IsDeclaration || fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var changed = 0;
    foreach (var copy in fn.AllInstructions.OfType<IrCall>().ToList())
      if (TryElide(copy))
        ++changed;
    return changed;
  }

  private static bool TryElide(IrCall copy) {
    if (!IsCall(copy, _DUP) || copy.Parent is not { } block || !copy.Type.IsPointer)
      return false;

    var copyIndex = IndexIn(block, copy);
    if (copyIndex < 0)
      return false;

    IrCall? release = null;
    var borrows = new List<IrCall>();
    foreach (var user in copy.Users.ToArray()) {
      if (user is not IrCall call || !ReferenceEquals(call.Parent, block) || call.ArgCount != 1
          || !ReferenceEquals(call.GetOperand(1), copy))
        return false;

      if (IsCall(call, _DUP)) {
        borrows.Add(call);
        continue;
      }
      if (!IsCall(call, _FREE) || release is not null)
        return false;
      release = call;
    }

    if (release is null)
      return false;
    var releaseIndex = IndexIn(block, release);
    if (releaseIndex <= copyIndex)
      return false;

    foreach (var borrow in borrows) {
      var index = IndexIn(block, borrow);
      if (index <= copyIndex || index >= releaseIndex)
        return false;
    }

    var source = copy.GetOperand(1);
    if (borrows.Count > 0 && !SourceOutlives(block, source, copy, releaseIndex))
      return false;

    foreach (var borrow in borrows)
      borrow.SetOperand(1, source);
    release.EraseFromParent();
    copy.EraseFromParent();
    return true;
  }

  private static bool SourceOutlives(IrBasicBlock block, IrValue source, IrCall copy, int copiedReleaseIndex) {
    if (source is IrNullPtr)
      return true;
    if (!source.Type.IsPointer || source is not (IrArgument or IrCall or IrPhi or IrSelect))
      return false;

    IrCall? sourceRelease = null;
    foreach (var user in source.Users.ToArray()) {
      if (ReferenceEquals(user, copy))
        continue;
      if (!ReferenceEquals(user.Parent, block) || user is not IrCall call || call.ArgCount != 1
          || !ReferenceEquals(call.GetOperand(1), source))
        return false;
      if (IsCall(call, _DUP))
        continue;
      if (!IsCall(call, _FREE) || sourceRelease is not null)
        return false;
      sourceRelease = call;
    }

    return sourceRelease is not null && IndexIn(block, sourceRelease) > copiedReleaseIndex;
  }

  private static bool IsCall(IrCall call, string name)
    => call.ArgCount == 1 && call.Callee is IrFunction { Name: var callee } && callee == name;

  private static int IndexIn(IrBasicBlock block, IrInstruction instruction) {
    for (var i = 0; i < block.Instructions.Count; ++i)
      if (ReferenceEquals(block.Instructions[i], instruction))
        return i;
    return -1;
  }
}
