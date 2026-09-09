namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0297: consumes read-only LEFT$/RIGHT$/MID$ results as expression-local string views instead of
/// materializing copied substrings.
///
/// <para>
/// A view is deliberately not a first-class IR value. It exists only while this pass rewrites one
/// consuming operation and is represented by three ordinary SSA values: the base string handle, a
/// 1-based start, and a byte length. The handle is stable across DOS string-heap compaction; the
/// descriptor is resolved only by the final view consumer. The real lifetime rule is therefore that
/// nobody may mutate or free the base between the cancelled borrow and that consumer.
/// </para>
///
/// <para>
/// Reads of BASIC string variables reach the IR as <c>rt_str_dup(base)</c> because the ordinary
/// runtime ABI consumes every string value. A view cancels exactly that single-use borrow and calls
/// a non-consuming view routine on <c>base</c>. An ordinary comparison operand is still an owned
/// temporary; the fused comparison borrows it and this pass inserts <c>rt_str_free</c> immediately
/// afterwards, preserving the old comparison's ownership transfer at the same program point.
/// </para>
///
/// <para>
/// <see cref="StringSliceLength"/> handles the LEN consumer separately. This pass is deliberately
/// only the cases that really need a view representation: comparison and PRINT.
/// </para>
/// </summary>
public static class StringSliceView {

  private const string _LEN_BORROW = "rt_str_len_borrow";
  private const string _COMPARE = "rt_str_compare";
  private const string _COMPARE_EQ = "rt_str_compare_eq";
  private const string _COMPARE_VIEW = "rt_str_compare_view";
  private const string _COMPARE_EQ_VIEW = "rt_str_compare_eq_view";
  private const string _PRINT = "rt_print_strvar";
  private const string _FPRINT = "rt_fprint_strvar";
  private const string _PRINT_VIEW = "rt_print_strview";
  private const string _FPRINT_VIEW = "rt_fprint_strview";
  private const string _FREE = "rt_str_free";
  private const string _DUP = "rt_str_dup";
  private const string _LEFT = "rt_str_left";
  private const string _RIGHT = "rt_str_right";
  private const string _MID = "rt_str_mid";
  private const string _MID2 = "rt_str_mid2";

  private enum SliceKind { Left, Right, Mid, MidToEnd }

  private readonly record struct SliceCandidate(
    IrCall Slice,
    IrCall Borrow,
    IrValue Base,
    SliceKind Kind,
    IrValue First,
    IrValue? Second);

  private readonly record struct View(IrValue Handle, IrValue Start, IrValue Length, SliceCandidate? Slice);

  // Calls that may sit between taking a string-variable borrow and the final consumer without changing
  // that variable. Some allocate and compact the DOS heap; that is harmless because the view keeps a
  // HANDLE, not a raw heap pointer, and resolves the descriptor only at the consumer. Unknown/user
  // calls are deliberately absent because they may assign or free the borrowed variable.
  private static readonly HashSet<string> _safeInterveningRuntimeCalls = new(StringComparer.Ordinal) {
    "rt_str_const", "rt_str_dup", "rt_str_concat", "rt_str_concat_n",
    "rt_str_left", "rt_str_right", "rt_str_mid", "rt_str_mid2",
    "rt_str_ucase", "rt_str_lcase", "rt_str_ltrim", "rt_str_rtrim",
    "rt_str_space", "rt_str_string", "rt_str_string_s", "rt_str_chr",
    "rt_str_hex", "rt_str_oct", "rt_str_bin", "rt_str_radix", "rt_str_repeat",
    "rt_str_from_i8", "rt_str_from_u8", "rt_str_from_i16", "rt_str_from_u16",
    "rt_str_from_i32", "rt_str_from_u32", "rt_str_from_i64", "rt_str_from_u64",
    "rt_str_from_single", "rt_str_from_double", "rt_str_from_fixed",
    "rt_str_len", "rt_str_len_borrow", "rt_str_asc", "rt_str_char_at",
    "rt_str_instr", "rt_str_instr_start", "rt_str_val", "rt_str_compare", "rt_str_compare_eq",
  };

  /// <summary>Rewrites compared and printed slices across the module; returns the number rewritten.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;

      foreach (var call in function.AllInstructions.OfType<IrCall>().ToList()) {
        if (call.Parent is null || call.Callee is not IrFunction callee)
          continue;
        if (callee.Name is _COMPARE or _COMPARE_EQ && call.ArgCount == 2)
          rewritten += RewriteCompare(module, call, equalityOnly: callee.Name == _COMPARE_EQ) ? 1 : 0;
        else if (callee.Name == _PRINT && call.ArgCount == 1)
          rewritten += RewritePrint(module, call, file: null) ? 1 : 0;
        else if (callee.Name == _FPRINT && call.ArgCount == 2)
          rewritten += RewritePrint(module, call, call.GetOperand(1)) ? 1 : 0;
      }
    }
    return rewritten;
  }

  private static bool RewriteCompare(IrModule module, IrCall compare, bool equalityOnly) {
    var leftCandidate = ClassifySlice(compare.GetOperand(1), compare);
    var rightCandidate = ClassifySlice(compare.GetOperand(2), compare);
    if (leftCandidate is null && rightCandidate is null)
      return false;

    var left = leftCandidate is { } lc ? MaterializeView(module, lc) : FullView(module, compare.GetOperand(1), compare);
    var right = rightCandidate is { } rc ? MaterializeView(module, rc) : FullView(module, compare.GetOperand(2), compare);
    var name = equalityOnly ? _COMPARE_EQ_VIEW : _COMPARE_VIEW;
    var entry = module.FindFunction(name)
      ?? module.AddFunction(new IrFunction(name, IrType.I32, [
        new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1), new IrArgument(IrType.I32, 2),
        new IrArgument(IrType.Ptr, 3), new IrArgument(IrType.I32, 4), new IrArgument(IrType.I32, 5),
      ]));
    var fused = compare.Parent!.InsertBefore(new IrCall(compare.Type, entry,
      [left.Handle, left.Start, left.Length, right.Handle, right.Start, right.Length]), compare);

    // The view routine borrows both sides. Any side that was not a cancelled slice-borrow is still the
    // owned temporary the old string compare would have consumed, so release it immediately after the
    // fused compare (all inserts are before the old compare anchor and therefore stay in this order).
    if (left.Slice is null)
      ReleaseOwned(module, compare, left.Handle);
    if (right.Slice is null)
      ReleaseOwned(module, compare, right.Handle);

    compare.ReplaceAllUsesWith(fused);
    compare.EraseFromParent();
    if (left.Slice is { } ls)
      EraseSlice(ls);
    if (right.Slice is { } rs)
      EraseSlice(rs);
    return true;
  }

  private static bool RewritePrint(IrModule module, IrCall print, IrValue? file) {
    var valueIndex = file is null ? 1 : 2;
    if (ClassifySlice(print.GetOperand(valueIndex), print) is not { } candidate)
      return false;

    var view = MaterializeView(module, candidate);
    var name = file is null ? _PRINT_VIEW : _FPRINT_VIEW;
    var entry = module.FindFunction(name) ?? module.AddFunction(file is null
      ? new IrFunction(name, IrType.Void, [
        new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1), new IrArgument(IrType.I32, 2),
      ])
      : new IrFunction(name, IrType.Void, [
        new IrArgument(IrType.I32, 0), new IrArgument(IrType.Ptr, 1),
        new IrArgument(IrType.I32, 2), new IrArgument(IrType.I32, 3),
      ]));
    IrValue[] args = file is null
      ? [view.Handle, view.Start, view.Length]
      : [file, view.Handle, view.Start, view.Length];
    print.Parent!.InsertBefore(new IrCall(IrType.Void, entry, args), print);
    print.EraseFromParent();
    EraseSlice(candidate);
    return true;
  }

  /// <summary>A whole owned string, viewed as [1, LEN(handle)] without consuming it yet.</summary>
  private static View FullView(IrModule module, IrValue handle, IrInstruction anchor) {
    var length = anchor.Parent!.InsertBefore(new IrCall(IrType.I32, LenBorrowEntry(module), [handle]), anchor);
    return new(handle, new IrConstantInt(IrType.I32, 1), length, null);
  }

  /// <summary>
  /// Recognizes a slice whose source is the lowering's single-use borrow and whose base cannot be
  /// invalidated before <paramref name="consumer"/>. The check begins at the borrow rather than at
  /// the slice call: MID$/LEFT$/RIGHT$ arguments are evaluated between those two points, and a user
  /// call there could mutate the source after the original program had already copied its snapshot.
  /// </summary>
  private static SliceCandidate? ClassifySlice(IrValue value, IrInstruction consumer) {
    if (value is not IrCall { Callee: IrFunction sliceFn } slice || slice.Users.Count != 1 || slice.Parent is null)
      return null;
    if (slice.GetOperand(1) is not IrCall { Callee: IrFunction { Name: _DUP }, ArgCount: 1 } borrow
        || borrow.Users.Count != 1 || borrow.Parent is null)
      return null;
    if (!SafeUntilConsumed(borrow, consumer))
      return null;

    return sliceFn.Name switch {
      _LEFT when slice.ArgCount == 2
        => new(slice, borrow, borrow.GetOperand(1), SliceKind.Left, slice.GetOperand(2), null),
      _RIGHT when slice.ArgCount == 2
        => new(slice, borrow, borrow.GetOperand(1), SliceKind.Right, slice.GetOperand(2), null),
      _MID when slice.ArgCount == 3
        => new(slice, borrow, borrow.GetOperand(1), SliceKind.Mid, slice.GetOperand(2), slice.GetOperand(3)),
      _MID2 when slice.ArgCount == 2
        => new(slice, borrow, borrow.GetOperand(1), SliceKind.MidToEnd, slice.GetOperand(2), null),
      _ => null,
    };
  }

  private static bool SafeUntilConsumed(IrCall borrow, IrInstruction consumer) {
    if (!ReferenceEquals(borrow.Parent, consumer.Parent) || borrow.Parent is null)
      return false;
    var instructions = borrow.Parent.Instructions.ToList();
    var from = instructions.IndexOf(borrow);
    var to = instructions.IndexOf(consumer);
    if (from < 0 || to <= from)
      return false;

    for (var i = from + 1; i < to; ++i)
      switch (instructions[i]) {
        case IrStore:
          return false;
        case IrCall { Callee: IrFunction callee } when callee.IsDeclaration
            && _safeInterveningRuntimeCalls.Contains(callee.Name):
          break;
        case IrCall:
          return false;
      }
    return true;
  }

  /// <summary>Materializes start/length at the original slice position, before any later allocation.</summary>
  private static View MaterializeView(IrModule module, SliceCandidate candidate) {
    var slice = candidate.Slice;
    var block = slice.Parent!;
    var sourceLength = block.InsertBefore(new IrCall(IrType.I32, LenBorrowEntry(module), [candidate.Base]), slice);
    var zero = new IrConstantInt(IrType.I32, 0);
    var one = new IrConstantInt(IrType.I32, 1);

    switch (candidate.Kind) {
      case SliceKind.Left: {
        var length = ClampCount(block, slice, candidate.First, sourceLength, zero);
        return new(candidate.Base, one, length, candidate);
      }
      case SliceKind.Right: {
        var length = ClampCount(block, slice, candidate.First, sourceLength, zero);
        var tail = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, sourceLength, length), slice);
        var start = block.InsertBefore(new IrBinary(IrBinaryOp.Add, tail, one), slice);
        return new(candidate.Base, start, length, candidate);
      }
      case SliceKind.Mid:
      case SliceKind.MidToEnd: {
        var beforeOne = block.InsertBefore(new IrCmp(IrCmpPred.Slt, candidate.First, one), slice);
        var normalizedStart = block.InsertBefore(new IrSelect(beforeOne, one, candidate.First), slice);
        var afterStart = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, sourceLength, normalizedStart), slice);
        var rawRemaining = block.InsertBefore(new IrBinary(IrBinaryOp.Add, afterStart, one), slice);
        var noRemaining = block.InsertBefore(new IrCmp(IrCmpPred.Sle, rawRemaining, zero), slice);
        var remaining = block.InsertBefore(new IrSelect(noRemaining, zero, rawRemaining), slice);
        // A zero-length view never dereferences its start. Canonicalizing that unused start to one is
        // what also keeps every start passed to the 16-bit DOS ABI provably within the string range.
        var start = block.InsertBefore(new IrSelect(noRemaining, one, normalizedStart), slice);
        var length = candidate.Kind == SliceKind.Mid
          ? ClampCount(block, slice, candidate.Second!, remaining, zero)
          : remaining;
        return new(candidate.Base, start, length, candidate);
      }
      default:
        throw new InvalidOperationException("unknown string slice kind");
    }
  }

  private static IrValue ClampCount(IrBasicBlock block, IrInstruction anchor, IrValue requested, IrValue maximum, IrValue zero) {
    var nonPositive = block.InsertBefore(new IrCmp(IrCmpPred.Sle, requested, zero), anchor);
    var nonNegative = block.InsertBefore(new IrSelect(nonPositive, zero, requested), anchor);
    var tooLarge = block.InsertBefore(new IrCmp(IrCmpPred.Sgt, nonNegative, maximum), anchor);
    return block.InsertBefore(new IrSelect(tooLarge, maximum, nonNegative), anchor);
  }

  private static IrFunction LenBorrowEntry(IrModule module)
    => module.FindFunction(_LEN_BORROW)
       ?? module.AddFunction(new IrFunction(_LEN_BORROW, IrType.I32, [new IrArgument(IrType.Ptr, 0)]));

  private static void ReleaseOwned(IrModule module, IrInstruction anchor, IrValue handle) {
    if (handle is IrNullPtr)
      return;
    var free = module.FindFunction(_FREE)
      ?? module.AddFunction(new IrFunction(_FREE, IrType.Void, [new IrArgument(IrType.Ptr, 0)]));
    anchor.Parent!.InsertBefore(new IrCall(IrType.Void, free, [handle]), anchor);
  }

  private static void EraseSlice(SliceCandidate candidate) {
    candidate.Slice.EraseFromParent();
    candidate.Borrow.EraseFromParent();
  }
}
