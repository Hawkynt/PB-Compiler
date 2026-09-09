namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0334/O0335 — recognizes a counted linear search over a read-only constant integer table. A
/// strictly sorted unique table becomes a balanced binary-search CFG; any other unique fixed set
/// becomes an <see cref="IrSwitch"/>, whose target-specific lowering already includes the perfect-hash
/// dispatcher. The switch default is the mandatory verification/failure path.
/// </summary>
public static class StaticSearchRecognition {

  private const int _MIN_BINARY_KEYS = 8;
  private const int _MIN_STATIC_KEYS = 4;

  private sealed record Search(IrValue Key, long[] Keys, bool Unsigned);

  /// <summary>Rewrites recognized searches; returns the number replaced.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var rewritten = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration).ToList()) {
      if (function.HasErrorHandler || function.HasInlineAsm)
        continue;
      foreach (var header in function.Blocks.ToList()) {
        if (header.Parent is null || CountedLoop.Match(function, header) is not { } loop
            || !TryMatch(function, loop, out var search))
          continue;
        Rewrite(function, loop, search);
        ++rewritten;
      }
    }
    return rewritten;
  }

  private static bool TryMatch(IrFunction function, CountedLoop loop, out Search search) {
    search = null!;
    if (loop.Preheader.Terminator is not IrBr preBranch || !ReferenceEquals(preBranch.Target, loop.Header)
        || loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt { IsZero: true }
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter) || next.Rhs is not IrConstantInt { Value: 1 }
        || loop.Exit.Phis.Any() || loop.Exit.Terminator is not IrRet { HasValue: true }
        || !function.ReturnType.SameStorage(loop.Counter.Type))
      return false;

    foreach (var block in loop.Region) {
      if (block.Terminator is not IrCondBr branch || ReferenceEquals(branch, loop.Header.Terminator)
          || branch.Condition is not IrCmp { Pred: IrCmpPred.Eq } comparison)
        continue;
      if (!TryLoadAndKey(comparison, loop.Counter, out var table, out var gep, out var load, out var key)
          || table.Bytes is null || table.Count != loop.Trips || !ReadOnly(table)
          || !TryKeys(table, out var keys) || keys.Length < _MIN_STATIC_KEYS || keys.Distinct().Count() != keys.Length)
        continue;

      var found = branch.IfTrue;
      if (!ReferenceEquals(branch.IfFalse, loop.Latch)
          || found.Terminator is not IrRet { HasValue: true, Value: { } returned }
          || !ReferenceEquals(returned, loop.Counter))
        continue;

      var allowed = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance) {
        loop.Counter, loop.Test, next, gep, load, comparison, branch, found.Terminator,
      };
      foreach (var regionBlock in loop.Region)
        if (regionBlock.Terminator is { } terminator)
          allowed.Add(terminator);
      if (loop.Region.SelectMany(regionBlock => regionBlock.Instructions).Any(instruction => !allowed.Contains(instruction)))
        continue;
      if (loop.Counter.Users.Any(user => user.Parent is not null && !loop.Region.Contains(user.Parent)))
        continue;

      search = new(key, keys, table.ValueType.IsUnsigned);
      return true;
    }
    return false;
  }

  private static bool TryLoadAndKey(IrCmp comparison, IrPhi counter, out IrGlobalVariable table,
      out IrGep gep, out IrLoad load, out IrValue key) {
    IrValue candidateLoad;
    IrValue candidateKey;
    if (comparison.Lhs is IrLoad) {
      candidateLoad = comparison.Lhs;
      candidateKey = comparison.Rhs;
    } else if (comparison.Rhs is IrLoad) {
      candidateLoad = comparison.Rhs;
      candidateKey = comparison.Lhs;
    } else {
      table = null!;
      gep = null!;
      load = null!;
      key = null!;
      return false;
    }

    if (candidateLoad is not IrLoad read || read.Pointer is not IrGep indexed
        || indexed.BasePtr is not IrGlobalVariable global || !ReferenceEquals(indexed.ByteOffset, counter)
        || indexed.ElementType is null || !indexed.ElementType.SameStorage(global.ValueType)
        || !read.Type.SameStorage(global.ValueType) || !candidateKey.Type.SameStorage(global.ValueType)) {
      table = null!;
      gep = null!;
      load = null!;
      key = null!;
      return false;
    }
    table = global;
    gep = indexed;
    load = read;
    key = candidateKey;
    return true;
  }

  private static bool ReadOnly(IrGlobalVariable table) {
    foreach (var user in table.Users) {
      if (user is not IrGep gep || !ReferenceEquals(gep.BasePtr, table))
        return false;
      foreach (var indexed in gep.Users)
        if (indexed is not IrLoad load || !ReferenceEquals(load.Pointer, gep))
          return false;
    }
    return true;
  }

  private static bool TryKeys(IrGlobalVariable table, out long[] keys) {
    keys = [];
    if (table.Bytes is not { } bytes || table.ValueType.Kind != IrTypeKind.Int)
      return false;
    var width = table.ValueType.Bits / 8;
    if (width is not (1 or 2) || bytes.Length != table.Count * width)
      return false;

    keys = new long[table.Count];
    for (var i = 0; i < keys.Length; ++i) {
      var pattern = width == 1 ? bytes[i] : bytes[i * 2] | (bytes[i * 2 + 1] << 8);
      keys[i] = table.ValueType.IsUnsigned ? pattern : Signed(pattern, table.ValueType.Bits);
    }
    return true;
  }

  private static void Rewrite(IrFunction function, CountedLoop loop, Search search) {
    if (StrictlySorted(search.Keys) && search.Keys.Length >= _MIN_BINARY_KEYS) {
      var root = BuildBinary(function, loop.Exit, search.Key, search.Keys, search.Unsigned,
        loop.Counter.Type, 0, search.Keys.Length - 1);
      ((IrBr)loop.Preheader.Terminator!).Target = root;
    } else {
      var dispatch = new IrSwitch(search.Key, loop.Exit);
      for (var i = 0; i < search.Keys.Length; ++i)
        dispatch.AddCase(search.Keys[i], Hit(function, loop.Counter.Type, i));
      loop.Preheader.Terminator!.EraseFromParent();
      loop.Preheader.Append(dispatch);
    }

    foreach (var block in loop.Region.ToList())
      function.RemoveBlock(block);
  }

  private static IrBasicBlock BuildBinary(IrFunction function, IrBasicBlock failure, IrValue key, long[] keys,
      bool unsigned, IrType resultType, int lo, int hi) {
    if (lo > hi)
      return failure;
    var mid = lo + ((hi - lo) >> 1);
    var node = function.CreateBlock($"bsearch.{mid}");
    var order = function.CreateBlock($"bsearch.order.{mid}");
    var constant = new IrConstantInt(key.Type, keys[mid]);
    var equal = node.Append(new IrCmp(IrCmpPred.Eq, key, constant));
    node.Append(new IrCondBr(equal, Hit(function, resultType, mid), order));
    var less = order.Append(new IrCmp(unsigned ? IrCmpPred.Ult : IrCmpPred.Slt, key, constant));
    order.Append(new IrCondBr(less,
      BuildBinary(function, failure, key, keys, unsigned, resultType, lo, mid - 1),
      BuildBinary(function, failure, key, keys, unsigned, resultType, mid + 1, hi)));
    return node;
  }

  private static IrBasicBlock Hit(IrFunction function, IrType resultType, int index) {
    var block = function.CreateBlock($"search.hit.{index}.{function.Blocks.Count}");
    block.Append(new IrRet(new IrConstantInt(resultType, index)));
    return block;
  }

  private static bool StrictlySorted(long[] values) {
    for (var i = 1; i < values.Length; ++i)
      if (values[i - 1] >= values[i])
        return false;
    return true;
  }

  private static long Signed(long pattern, int bits) {
    var sign = 1L << (bits - 1);
    return (pattern ^ sign) - sign;
  }
}
