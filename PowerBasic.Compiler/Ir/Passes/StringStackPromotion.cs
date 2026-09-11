namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0287: materializes a bounded, non-escaping dynamic-string temporary as raw bytes in the
/// function's stack frame instead of allocating it in the DOS string heap.
///
/// <para>
/// A DOS dynamic string is a HANDLE into the runtime descriptor table, not a pointer to its bytes.
/// Consequently an <see cref="IrAlloca"/> can never replace an arbitrary string value. This pass
/// only crosses that representation boundary when the complete value is compiler-controlled: an
/// exact-size producer tree whose sole final user is a raw-byte consumer. The producer calls then
/// disappear together and the consumer receives the exact bytes rather than a fabricated handle.
/// </para>
///
/// <para>
/// The escape proof is deliberately structural while O0260's shared escape analysis remains
/// pending. Every producer must have exactly one user, every producer must be in the consumer's
/// basic block, and the root must be consumed by one of the approved PRINT entries. A store, return,
/// phi, BYREF pass, unknown call, second read, or any handle-visible use therefore makes the
/// candidate fail closed.
/// </para>
///
/// <para>
/// The first slice handles the bounded builders whose exact byte count is already explicit in IR:
/// <c>CHR$</c>, pooled literals, pairwise concatenation, O0352 literal append, and O0024 multi-concat.
/// Stack objects are capped at 64 bytes on the 16-bit target. Numeric <c>STR$</c>, substrings with
/// data-dependent lengths, and string variables remain heap handles until a bounded raw formatter or
/// a richer representation proof exists.
/// </para>
///
/// <para>
/// The vintage raw-print ABI takes a DS-relative byte address while frame storage is SS-relative.
/// The finished stack object is therefore copied to one compiler-owned 64-byte DS staging buffer
/// immediately before <c>rt_print_str</c>/<c>rt_fprint_str</c>. The staging copy is representation
/// plumbing only: no string descriptor, heap block, allocation, or free is created.
/// </para>
/// </summary>
public static class StringStackPromotion {

  /// <summary>
  /// Deliberately small for the 16-bit target. O0287 is about moving tiny temporaries out of the
  /// compacting string heap, not about turning the stack into a second variable-size heap.
  /// </summary>
  public const int MaxStackBytes = 64;

  private const string _CHR = "rt_str_chr";
  private const string _CONST = "rt_str_const";
  private const string _CONCAT = "rt_str_concat";
  private const string _CONCAT_N = "rt_str_concat_n";
  private const string _APPEND_LIT = "rt_str_append_lit";
  private const string _PRINT_VAR = "rt_print_strvar";
  private const string _FPRINT_VAR = "rt_fprint_strvar";
  private const string _PRINT_RAW = "rt_print_str";
  private const string _FPRINT_RAW = "rt_fprint_str";
  private const string _MEMCPY = "llvm.memcpy.p0.p0.i32";
  private const string _STAGING = ".o0287.printbuf";

  private abstract record Piece(int Length);
  private sealed record LiteralPiece(IrValue Bytes, int Count) : Piece(Count);
  private sealed record BytePiece(IrValue Value) : Piece(1);

  /// <summary>Promotes all qualifying string-temporary roots in <paramref name="module"/>.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);

    var changed = 0;
    foreach (var function in module.Functions.ToList()) {
      if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
        continue;

      foreach (var consumer in function.AllInstructions.OfType<IrCall>().ToList())
        if (TryPromote(module, function, consumer))
          ++changed;
    }
    return changed;
  }

  private static bool TryPromote(IrModule module, IrFunction function, IrCall consumer) {
    if (consumer.Parent is not { } block || consumer.Callee is not IrFunction callee)
      return false;

    var file = callee.Name == _FPRINT_VAR;
    if ((!file || consumer.ArgCount != 2) && (file || callee.Name != _PRINT_VAR || consumer.ArgCount != 1))
      return false;

    var root = consumer.GetOperand(file ? 2 : 1);
    if (root is not IrCall || root.Users.Count != 1)
      return false;

    var pieces = new List<Piece>();
    var producers = new List<IrCall>();
    var seen = new HashSet<IrCall>(ReferenceEqualityComparer.Instance);
    var length = 0;
    if (!Describe(root, block, pieces, producers, seen, ref length) || length is <= 0 or > MaxStackBytes)
      return false;

    var entry = function.Entry;
    if (entry is null)
      return false;

    // Keep static allocas together at the head of the entry block. The frame owns this storage for
    // exactly the function lifetime; no explicit free exists or is required.
    var insertAt = 0;
    while (insertAt < entry.Instructions.Count && entry.Instructions[insertAt] is IrAlloca)
      ++insertAt;
    var buffer = entry.InsertAt(insertAt, new IrAlloca(IrType.I8) { Count = length });

    var memcpy = Declare(module, _MEMCPY, IrType.Void,
      IrType.Ptr, IrType.Ptr, IrType.I32, IrType.I1);
    var cursor = 0;
    foreach (var piece in pieces) {
      IrValue destination = cursor == 0
        ? buffer
        : block.InsertBefore(new IrGep(buffer, IrBuilder.ConstI32(cursor)), consumer);
      switch (piece) {
        case LiteralPiece literal:
          block.InsertBefore(new IrCall(IrType.Void, memcpy,
            [destination, literal.Bytes, IrBuilder.ConstI32(literal.Count), IrBuilder.ConstBool(false)]), consumer);
          break;
        case BytePiece one:
          var value = one.Value.Type.SameStorage(IrType.I8)
            ? one.Value
            : block.InsertBefore(new IrCast(IrCastOp.Trunc, one.Value, IrType.I8), consumer);
          block.InsertBefore(new IrStore(value, destination), consumer);
          break;
      }
      cursor += piece.Length;
    }

    // The x86-16 raw print ABI names a literal-style DS offset rather than a general segmented
    // pointer. Bridge SS->DS here with the existing target-aware memcpy; C/LLVM see the same ordinary
    // byte copy. Nothing can observe the staging buffer between this copy and the print call.
    var staging = StagingBuffer(module);
    block.InsertBefore(new IrCall(IrType.Void, memcpy,
      [staging, buffer, IrBuilder.ConstI32(length), IrBuilder.ConstBool(false)]), consumer);

    IrFunction raw;
    IrCall replacement;
    if (file) {
      raw = Declare(module, _FPRINT_RAW, IrType.Void, IrType.I32, IrType.Ptr, IrType.I32);
      replacement = new IrCall(IrType.Void, raw,
        [consumer.GetOperand(1), staging, IrBuilder.ConstI32(length)]);
    } else {
      raw = Declare(module, _PRINT_RAW, IrType.Void, IrType.Ptr, IrType.I32);
      replacement = new IrCall(IrType.Void, raw, [staging, IrBuilder.ConstI32(length)]);
    }
    block.InsertBefore(replacement, consumer);
    consumer.EraseFromParent();

    // Children were recorded before parents. Erasing from the root down first drops each child's
    // final use before that child is removed, leaving no stale users behind.
    for (var i = producers.Count - 1; i >= 0; --i)
      producers[i].EraseFromParent();
    return true;
  }

  private static bool Describe(IrValue value, IrBasicBlock consumerBlock, List<Piece> pieces,
      List<IrCall> producers, HashSet<IrCall> seen, ref int total) {
    if (value is not IrCall { Callee: IrFunction callee } call
        || !ReferenceEquals(call.Parent, consumerBlock)
        || call.Users.Count != 1
        || !seen.Add(call))
      return false;

    // A local function cannot capture a ref parameter, so the running total is threaded through
    // explicitly rather than closed over.
    static bool AddLength(int count, ref int total) {
      if (count < 0 || total > MaxStackBytes - count)
        return false;
      total += count;
      return true;
    }

    switch (callee.Name) {
      case _CHR when call.ArgCount == 1:
        if (!AddLength(1, ref total))
          return false;
        pieces.Add(new BytePiece(call.GetOperand(1)));
        break;

      case _CONST when call.ArgCount == 2:
        if (!Literal(call.GetOperand(1), call.GetOperand(2), out var bytes, out var length)
            || !AddLength(length, ref total))
          return false;
        if (length > 0)
          pieces.Add(new LiteralPiece(bytes, length));
        break;

      case _CONCAT when call.ArgCount == 2:
        if (!Describe(call.GetOperand(1), consumerBlock, pieces, producers, seen, ref total)
            || !Describe(call.GetOperand(2), consumerBlock, pieces, producers, seen, ref total))
          return false;
        break;

      case _APPEND_LIT when call.ArgCount == 3:
        if (!Describe(call.GetOperand(1), consumerBlock, pieces, producers, seen, ref total)
            || !Literal(call.GetOperand(2), call.GetOperand(3), out var appendedBytes, out var appendedLength)
            || !AddLength(appendedLength, ref total))
          return false;
        if (appendedLength > 0)
          pieces.Add(new LiteralPiece(appendedBytes, appendedLength));
        break;

      case _CONCAT_N when call.ArgCount >= 2:
        if (call.GetOperand(1) is not IrConstantInt count
            || count.Value < 2
            || count.Value != call.ArgCount - 1)
          return false;
        for (var i = 0; i < count.Value; ++i)
          if (!Describe(call.GetOperand(i + 2), consumerBlock, pieces, producers, seen, ref total))
            return false;
        break;

      default:
        return false;
    }

    producers.Add(call);
    return true;
  }

  private static bool Literal(IrValue source, IrValue countValue, out IrValue bytes, out int count) {
    bytes = source;
    count = 0;
    if (source is not IrGlobalVariable { Bytes: { } literal }
        || countValue is not IrConstantInt length
        || length.Value < 0
        || length.Value > literal.Length
        || length.Value > int.MaxValue)
      return false;
    count = (int)length.Value;
    return true;
  }

  private static IrGlobalVariable StagingBuffer(IrModule module)
    => module.FindGlobal(_STAGING)
       ?? module.AddGlobal(new IrGlobalVariable(_STAGING, IrType.I8) { Count = MaxStackBytes });

  private static IrFunction Declare(IrModule module, string name, IrType returnType, params IrType[] parameters)
    => module.FindFunction(name)
       ?? module.AddFunction(new IrFunction(name, returnType,
         parameters.Select((type, index) => new IrArgument(type, index))));
}
