using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0331 — packs a non-escaping zero-initialized global INTEGER Boolean array into one bit per
/// element. Every access must preserve Boolean semantics; stores may be literal 0/-1 or an SSA value
/// whose range is proven to contain only those two values.
/// </summary>
public static class BitsetSubstitution {

  private const int _MIN_ELEMENTS = 8;
  private const string _MEMSET = "llvm.memset.p0.i32";

  private sealed record Access(IrInstruction Instruction, IrValue Index);

  /// <summary>Packs qualifying globals in <paramref name="module"/>; returns the number packed.</summary>
  public static int Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var packed = 0;
    foreach (var global in module.Globals.ToList())
      if (TryPack(module, global))
        ++packed;
    return packed;
  }

  private static bool TryPack(IrModule module, IrGlobalVariable global) {
    if (!global.ValueType.SameStorage(IrType.I16) || global.Count < _MIN_ELEMENTS || global.Bytes is not null
        || !global.IsZeroInitialized || global.Name.StartsWith("rt_", StringComparison.Ordinal)
        || !Collect(global, out var accesses, out var geps, out var zeroFills))
      return false;

    var replacement = new IrGlobalVariable(global.Name, IrType.I8) {
      Count = (global.Count + 7) >> 3,
      IsZeroInitialized = true,
    };

    foreach (var access in accesses)
      switch (access.Instruction) {
        case IrLoad load:
          RewriteLoad(load, replacement, access.Index);
          break;
        case IrStore store:
          RewriteStore(store, replacement, access.Index);
          break;
      }

    foreach (var zeroFill in zeroFills)
      RewriteZeroFill(zeroFill, replacement);

    foreach (var gep in geps)
      if (gep.HasNoUsers)
        gep.EraseFromParent();
    System.Diagnostics.Debug.Assert(global.HasNoUsers,
      "the whole-program access proof must account for every use before the representation changes");

    module.RemoveGlobal(global);
    module.AddGlobal(replacement);
    return true;
  }

  private static bool Collect(IrGlobalVariable global, out List<Access> accesses, out List<IrGep> geps,
      out List<IrCall> zeroFills) {
    accesses = [];
    geps = [];
    zeroFills = [];
    var ranges = new Dictionary<IrFunction, IrRangeAnalysis?>();
    foreach (var user in global.Users.ToList()) {
      if (Opaque(user))
        return false;
      switch (user) {
        case IrGep { ElementType: { } element } gep when gep.BasePtr == global && element.SameStorage(IrType.I16)
                                                               && gep.ByteOffset.Type.IsInteger:
          geps.Add(gep);
          foreach (var indexed in gep.Users.ToList()) {
            if (!TryAccess(indexed, gep, gep.ByteOffset, accesses, ranges))
              return false;
          }
          break;
        case IrLoad load when ReferenceEquals(load.Pointer, global) && load.Type.SameStorage(IrType.I16):
          accesses.Add(new(load, new IrConstantInt(IrType.I16, 0)));
          break;
        case IrStore store when ReferenceEquals(store.Pointer, global) && BooleanStore(store, ranges):
          accesses.Add(new(store, new IrConstantInt(IrType.I16, 0)));
          break;
        case IrCall call when WholeArrayZero(call, global):
          zeroFills.Add(call);
          break;
        default:
          return false;                              // address escape, differently typed access, or unknown whole-array operation
      }
    }
    return accesses.Count > 0;
  }

  private static bool TryAccess(IrInstruction instruction, IrGep pointer, IrValue index, List<Access> accesses,
      Dictionary<IrFunction, IrRangeAnalysis?> ranges) {
    if (Opaque(instruction))
      return false;
    switch (instruction) {
      case IrLoad load when ReferenceEquals(load.Pointer, pointer) && load.Type.SameStorage(IrType.I16):
        accesses.Add(new(load, index));
        return true;
      case IrStore store when ReferenceEquals(store.Pointer, pointer) && BooleanStore(store, ranges):
        accesses.Add(new(store, index));
        return true;
      default:
        return false;
    }
  }

  private static bool BooleanStore(IrStore store, Dictionary<IrFunction, IrRangeAnalysis?> ranges) {
    if (!store.Value.Type.SameStorage(IrType.I16))
      return false;
    if (store.Value is IrConstantInt constant)
      return constant.ZeroExtended is 0 or 0xffff;

    var block = store.Parent;
    var function = block?.Parent;
    if (block is null || function is null)
      return false;
    if (!ranges.TryGetValue(function, out var analysis)) {
      analysis = IrRangeAnalysis.Build(function);
      ranges.Add(function, analysis);
    }
    if (analysis is null)
      return false;

    var range = analysis.RangeAt(store.Value, block);
    return !range.IsEmpty && range.Lo >= -1 && range.Hi <= 0;
  }

  private static bool WholeArrayZero(IrCall call, IrGlobalVariable global) {
    if (call.Callee is not IrFunction { Name: _MEMSET } || !call.Type.IsVoid || call.ArgCount != 4)
      return false;

    var args = call.Args.ToArray();
    return ReferenceEquals(args[0], global)
           && args[1] is IrConstantInt fill && fill.Type.SameStorage(IrType.I8) && fill.IsZero
           && args[2] is IrConstantInt bytes && bytes.Type.SameStorage(IrType.I32)
              && bytes.ZeroExtended == (ulong)global.Count * 2UL
           && args[3] is IrConstantInt volatility && volatility.Type.IsBool && volatility.IsZero;
  }

  private static bool Opaque(IrInstruction instruction)
    => instruction.Parent?.Parent is { HasErrorHandler: true } or { HasInlineAsm: true };

  private static void RewriteLoad(IrLoad load, IrGlobalVariable packed, IrValue index) {
    var block = load.Parent!;
    var (address, mask) = AddressAndMask(block, load, packed, index);
    var bits = block.InsertBefore(new IrLoad(IrType.I8, address), load);
    var selected = block.InsertBefore(new IrBinary(IrBinaryOp.And, bits, mask), load);
    var set = block.InsertBefore(new IrCmp(IrCmpPred.Ne, selected, new IrConstantInt(IrType.I8, 0)), load);
    var widened = block.InsertBefore(new IrCast(IrCastOp.ZExt, set, load.Type), load);
    var boolean = block.InsertBefore(new IrBinary(IrBinaryOp.Sub, new IrConstantInt(load.Type, 0), widened), load);
    load.ReplaceAllUsesWith(boolean);
    load.EraseFromParent();
  }

  private static void RewriteStore(IrStore store, IrGlobalVariable packed, IrValue index) {
    var block = store.Parent!;
    var (address, mask) = AddressAndMask(block, store, packed, index);
    var old = block.InsertBefore(new IrLoad(IrType.I8, address), store);
    IrValue value;
    if (store.Value is IrConstantInt constant) {
      value = constant.ZeroExtended != 0
        ? block.InsertBefore(new IrBinary(IrBinaryOp.Or, old, mask), store)
        : block.InsertBefore(new IrBinary(IrBinaryOp.And, old,
            block.InsertBefore(new IrBinary(IrBinaryOp.Xor, mask, new IrConstantInt(IrType.I8, 0xff)), store)), store);
    } else {
      var invertedMask = block.InsertBefore(
        new IrBinary(IrBinaryOp.Xor, mask, new IrConstantInt(IrType.I8, 0xff)), store);
      var cleared = block.InsertBefore(new IrBinary(IrBinaryOp.And, old, invertedMask), store);
      var narrowed = block.InsertBefore(new IrCast(IrCastOp.Trunc, store.Value, IrType.I8), store);
      var selected = block.InsertBefore(new IrBinary(IrBinaryOp.And, narrowed, mask), store);
      value = block.InsertBefore(new IrBinary(IrBinaryOp.Or, cleared, selected), store);
    }
    block.InsertBefore(new IrStore(value, address), store);
    store.EraseFromParent();
  }

  private static void RewriteZeroFill(IrCall call, IrGlobalVariable packed) {
    call.SetOperand(1, packed);                       // operand zero is the callee
    call.SetOperand(3, new IrConstantInt(call.GetOperand(3).Type, packed.Count));
  }

  private static (IrValue Address, IrValue Mask) AddressAndMask(IrBasicBlock block, IrInstruction anchor,
      IrGlobalVariable packed, IrValue index) {
    if (index is IrConstantInt constant) {
      var element = constant.ZeroExtended;
      return (
        element < 8 ? packed : block.InsertBefore(new IrGep(packed,
          new IrConstantInt(index.Type, (long)(element >> 3)), IrType.I8), anchor),
        new IrConstantInt(IrType.I8, 1 << (int)(element & 7))
      );
    }

    var byteIndex = block.InsertBefore(new IrBinary(IrBinaryOp.LShr, index, new IrConstantInt(index.Type, 3)), anchor);
    var bitIndex = block.InsertBefore(new IrBinary(IrBinaryOp.And, index, new IrConstantInt(index.Type, 7)), anchor);
    var wideMask = block.InsertBefore(new IrBinary(IrBinaryOp.Shl, new IrConstantInt(index.Type, 1), bitIndex), anchor);
    IrValue mask = index.Type.Bits == 8
      ? wideMask
      : block.InsertBefore(new IrCast(IrCastOp.Trunc, wideMask, IrType.I8), anchor);
    return (block.InsertBefore(new IrGep(packed, byteIndex, IrType.I8), anchor), mask);
  }
}
