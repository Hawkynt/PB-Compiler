namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0331 — packs a non-escaping zero-initialized global INTEGER Boolean array into one bit per
/// element. The v1 proof is intentionally strict: every access must be a direct element GEP and every
/// stored value must be exactly 0 or -1.
/// </summary>
public static class BitsetSubstitution {

  private const int _MIN_ELEMENTS = 8;

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
        || !Collect(global, out var accesses, out var geps))
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

    foreach (var gep in geps)
      if (gep.HasNoUsers)
        gep.EraseFromParent();
    System.Diagnostics.Debug.Assert(global.HasNoUsers,
      "the whole-program access proof must account for every use before the representation changes");

    module.RemoveGlobal(global);
    module.AddGlobal(replacement);
    return true;
  }

  private static bool Collect(IrGlobalVariable global, out List<Access> accesses, out List<IrGep> geps) {
    accesses = [];
    geps = [];
    foreach (var user in global.Users.ToList()) {
      if (Opaque(user))
        return false;
      switch (user) {
        case IrGep { ElementType: { } element } gep when gep.BasePtr == global && element.SameStorage(IrType.I16)
                                                               && gep.ByteOffset.Type.IsInteger:
          geps.Add(gep);
          foreach (var indexed in gep.Users.ToList()) {
            if (!TryAccess(indexed, gep, gep.ByteOffset, accesses))
              return false;
          }
          break;
        case IrLoad load when ReferenceEquals(load.Pointer, global) && load.Type.SameStorage(IrType.I16):
          accesses.Add(new(load, new IrConstantInt(IrType.I16, 0)));
          break;
        case IrStore store when ReferenceEquals(store.Pointer, global) && BooleanStore(store):
          accesses.Add(new(store, new IrConstantInt(IrType.I16, 0)));
          break;
        default:
          return false;                              // address escape, differently typed access, or whole-array operation
      }
    }
    return accesses.Count > 0;
  }

  private static bool TryAccess(IrInstruction instruction, IrGep pointer, IrValue index, List<Access> accesses) {
    if (Opaque(instruction))
      return false;
    switch (instruction) {
      case IrLoad load when ReferenceEquals(load.Pointer, pointer) && load.Type.SameStorage(IrType.I16):
        accesses.Add(new(load, index));
        return true;
      case IrStore store when ReferenceEquals(store.Pointer, pointer) && BooleanStore(store):
        accesses.Add(new(store, index));
        return true;
      default:
        return false;
    }
  }

  private static bool BooleanStore(IrStore store)
    => store.Value is IrConstantInt constant && constant.Type.SameStorage(IrType.I16)
       && constant.ZeroExtended is 0 or 0xffff;

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
    var set = ((IrConstantInt)store.Value).ZeroExtended != 0;
    IrValue value = set
      ? block.InsertBefore(new IrBinary(IrBinaryOp.Or, old, mask), store)
      : block.InsertBefore(new IrBinary(IrBinaryOp.And, old,
          block.InsertBefore(new IrBinary(IrBinaryOp.Xor, mask, new IrConstantInt(IrType.I8, 0xff)), store)), store);
    block.InsertBefore(new IrStore(value, address), store);
    store.EraseFromParent();
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
