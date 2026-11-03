using PowerBasic.Compiler.CodeGen;
using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Coalesces constant byte writes in a non-escaping, compiler-owned byte buffer.
/// Unlike generic POKE/AT memory, the alloca's distinct bytes are ordinary RAM:
/// independent stores may be reordered when every intervening memory access is
/// proven disjoint. Unknown effects always terminate the search.
/// </summary>
public static class PrivateByteStorePacking {
  private const int _MAX_WINDOW = 48;

  private readonly record struct ByteWrite(IrStore Store, int Offset, byte Value);

  public static int Run(IrFunction function, TargetCost cost) {
    ArgumentNullException.ThrowIfNull(function);
    ArgumentNullException.ThrowIfNull(cost);
    return IrFunctionPassPipeline.RunStandalone(function, "private-byte-store-pack",
      (fn, analyses) => {
        var changed = RunCore(fn, analyses.Get(IrAnalyses.PointerIdentity),
          analyses.Get(IrAnalyses.PointerEscape),
          cost.MemoryScalarBits >= 32 ? 4 : 2);
        return changed == 0
          ? IrPassResult.Unchanged
          : IrPassResult.ChangedPreservingSets(changed, IrAnalysisSets.Cfg);
      });
  }

  private static int RunCore(IrFunction fn, IrPointerIdentityAnalysis identities,
      IrPointerEscapeAnalysis escape, int maxWidth) {
    var packed = 0;
    foreach (var block in fn.Blocks) {
      foreach (var initial in block.Instructions.OfType<IrStore>().ToArray()) {
        if (initial.Parent is null || !TryByte(initial, identities, escape, out var root,
              out var first))
          continue;

        var writes = new Dictionary<int, ByteWrite> { [first.Offset] = first };
        var observations = new List<IrInstruction>();
        var at = block.Instructions.ToList().IndexOf(initial);
        var visited = 0;

        foreach (var instruction in block.Instructions.Skip(at + 1).ToArray()) {
          if (++visited > _MAX_WINDOW)
            break;
          if (instruction is IrGep or IrCast { Op: IrCastOp.BitCast })
            continue;
          if (instruction is IrLoad { IsDeviceRead: false } load) {
            if (writes.Values.Any(write => MayAlias(write.Store, load, identities)))
              break;
            observations.Add(load);
            continue;
          }
          if (instruction is not IrStore { IsPackedMode13Write: false,
                IsPackedPrivateWrite: false } store)
            break;
          if (TryByte(store, identities, escape, out var otherRoot, out var next)
              && ReferenceEquals(root, otherRoot)) {
            if (writes.ContainsKey(next.Offset)
                || observations.Any(effect => MayAlias(next.Store, effect, identities)))
              break;
            writes.Add(next.Offset, next);
            continue;
          }
          if (writes.Values.Any(write => MayAlias(write.Store, store, identities)))
            break;
          observations.Add(store);
        }

        var selected = PickRun(writes, first.Offset, maxWidth);
        if (selected.Count < 2)
          continue;

        var start = selected[0].Offset;
        IrValue pointer = start == 0
          ? root
          : block.InsertBefore(new IrGep(root, new IrConstantInt(IrType.I32, start)), initial);
        var type = selected.Count == 4 ? IrType.U32 : IrType.U16;
        var pattern = selected.Select((write, index) => (uint)write.Value << (index * 8))
          .Aggregate(0u, (bits, piece) => bits | piece);
        block.InsertBefore(new IrStore(new IrConstantInt(type, pattern), pointer) {
          IsPackedPrivateWrite = true,
        }, initial);
        foreach (var write in selected)
          write.Store.EraseFromParent();
        ++packed;
      }
    }
    if (packed != 0)
      Dce.Run(fn);
    return packed;
  }

  private static List<ByteWrite> PickRun(Dictionary<int, ByteWrite> writes,
      int firstOffset, int maxWidth) {
    foreach (var width in maxWidth >= 4 ? new[] { 4, 2 } : new[] { 2 })
      for (var start = firstOffset - width + 1; start <= firstOffset; ++start) {
        if (start < 0)
          continue;
        if (Enumerable.Range(start, width).All(writes.ContainsKey))
          return Enumerable.Range(start, width).Select(index => writes[index]).ToList();
      }
    return [];
  }

  private static bool TryByte(IrStore store, IrPointerIdentityAnalysis identities,
      IrPointerEscapeAnalysis escape, out IrAlloca root, out ByteWrite write) {
    root = null!;
    write = default;
    if (store.IsPackedMode13Write || store.IsPackedPrivateWrite
        || store.Value.Type.Bits != 8
        || store.Value is not IrConstantInt constant
        || identities.TryResolve(store.Pointer) is not {
          Root: IrAlloca { Allocated: var element, Count: >= 2 } allocation,
          ByteOffset: >= 0 and <= int.MaxValue } location
        || element.Bits != 8 || !element.IsInteger
        || allocation.Count > 65536 || location.ByteOffset >= allocation.Count
        || !escape.DoesNotEscape(allocation))
      return false;
    root = allocation;
    write = new(store, (int)location.ByteOffset!.Value,
      unchecked((byte)constant.ZeroExtended));
    return true;
  }

  private static bool MayAlias(IrStore write, IrInstruction other,
      IrPointerIdentityAnalysis identities) => other switch {
    IrLoad load => IrAliasAnalysis.MayAlias(
      write.Pointer, write.Value.Type, load.Pointer, load.Type, identities),
    IrStore store => IrAliasAnalysis.MayAlias(
      write.Pointer, write.Value.Type, store.Pointer, store.Value.Type, identities),
    _ => true,
  };
}
