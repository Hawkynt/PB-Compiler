using PowerBasic.Compiler.CodeGen;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Finishes Mode 13h pixel specialization after the last optimizing IR pass.
/// Earlier passes see the ordinary runtime call (an observable write). The native
/// selector sees a segmented store, with constant-color neighboring pixels packed
/// only when the source contains no intervening observable operation.
/// </summary>
public static class PixelRuntimeStoreLowering {
  public static int Run(IrModule module, TargetCost? cost = null) {
    ArgumentNullException.ThrowIfNull(module);
    var changes = 0;
    foreach (var function in module.Functions.Where(function => !function.IsDeclaration)) {
      foreach (var call in function.AllInstructions.OfType<IrCall>().Where(IsPackedWrite).ToArray()) {
        var block = call.Parent!;
        var byteColor = block.InsertBefore(new IrCast(IrCastOp.Trunc, call.GetOperand(2), IrType.U8), call);
        var pointer = block.InsertBefore(new IrFarPtr(
          new IrConstantInt(IrType.I16, unchecked((short)0xA000)), call.GetOperand(1)), call);
        block.InsertBefore(new IrStore(byteColor, pointer), call);
        call.EraseFromParent();
        ++changes;
      }
      changes += PackAdjacentPixels(function, cost?.MemoryScalarBits >= 32 ? 4 : 2);
      if (changes > 0)
        Dce.Run(function);
    }
    return changes;
  }

  private static bool IsPackedWrite(IrCall call)
    => call.Callee is IrFunction { IsDeclaration: true, Name: "rt_pset_mode13_offset" }
       && call.Convention == IrCallConvention.Basic
       && call.Type.IsVoid
       && call.ArgCount == 2
       && call.Args.All(argument => argument.Type == IrType.I16);

  /// <summary>
  /// Pack a run of constant bytes in certified Mode 13h memory. Only the compiler's
  /// graphics-cursor writes and the newly inserted byte truncations/far-pointer
  /// constructors may lie between the pixels. Calls, reads, traps, memory operations
  /// on other objects and unrecognized instructions terminate the run.
  /// </summary>
  private static int PackAdjacentPixels(IrFunction function, int width) {
    var changes = 0;
    foreach (var block in function.Blocks) {
      var instructions = block.Instructions.ToArray();
      for (var i = 0; i < instructions.Length; ++i) {
        if (instructions[i] is not IrStore first || first.Parent is null
            || !TryPixel(first, out var start, out var firstColor))
          continue;

        var bytes = new List<byte> { firstColor };
        var stores = new List<IrStore> { first };
        for (var j = i + 1; j < instructions.Length && bytes.Count < width; ++j) {
          var instruction = instructions[j];
          if (instruction.Parent is null)
            continue;
          if (instruction is IrCast { Op: IrCastOp.Trunc, Type: { Bits: 8 } }
              || instruction is IrFarPtr)
            continue;
          if (instruction is not IrStore store)
            break;
          if (IsCursorStore(store))
            continue;
          if (!TryPixel(store, out var offset, out var color)
              || offset != start + bytes.Count
              || offset / 320 != start / 320)
            break;
          bytes.Add(color);
          stores.Add(store);
        }

        // A single byte stays an ordinary observable hardware write.
        if (bytes.Count < 2)
          continue;
        // Always pack a whole word; use a DWORD only if the target supports it.
        var count = bytes.Count >= 4 && width >= 4 ? 4 : 2;
        var packed = 0u;
        for (var byteIndex = 0; byteIndex < count; ++byteIndex)
          packed |= (uint)bytes[byteIndex] << (byteIndex * 8);
        var type = count == 4 ? IrType.U32 : IrType.U16;
        block.InsertBefore(new IrStore(new IrConstantInt(type, packed), first.Pointer), first);
        foreach (var store in stores.Take(count))
          store.EraseFromParent();
        changes += count - 1;
      }
    }
    return changes;
  }

  private static bool IsCursorStore(IrStore store)
    => store.Pointer is IrGlobalVariable { Name: "rt_gx1" or "rt_gy1" };

  private static bool TryPixel(IrStore store, out int offset, out byte color) {
    offset = 0;
    color = 0;
    if (store.Pointer is not IrFarPtr {
          Segment: IrConstantInt { Value: var segment },
          Offset: IrConstantInt { Value: var position }
        } || unchecked((ushort)segment) != 0xA000 || position is < 0 or >= 64000
        || store.Value.Type.Bits != 8)
      return false;
    var value = store.Value switch {
      IrConstantInt literal => (long?)literal.Value,
      IrCast { Op: IrCastOp.Trunc, Value: IrConstantInt literal } => literal.Value,
      _ => null,
    };
    if (value is null)
      return false;
    offset = (int)position;
    color = unchecked((byte)value.Value);
    return true;
  }
}
