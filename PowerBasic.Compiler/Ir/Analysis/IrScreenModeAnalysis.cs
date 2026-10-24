namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>Forward must-facts about the DOS screen mode on normal CFG edges.</summary>
public sealed class IrScreenModeAnalysis {
  private readonly Dictionary<IrInstruction, long> _modes = [];

  internal IrScreenModeAnalysis(IrFunction function, IrDefaultSegmentAnalysis segments) {
    if (function.Entry is null || function.HasErrorHandler || function.HasInlineAsm)
      return;
    var outputs = new Dictionary<IrBasicBlock, long?>();
    var pending = new Queue<IrBasicBlock>();
    pending.Enqueue(function.Entry);
    while (pending.TryDequeue(out var block)) {
      var incoming = Incoming(function, block, outputs);
      var mode = incoming;
      foreach (var instruction in block.Instructions)
        mode = Transfer(instruction, mode, segments.Before(instruction));
      if (outputs.TryGetValue(block, out var previous) && previous == mode)
        continue;
      outputs[block] = mode;
      foreach (var successor in block.Successors)
        pending.Enqueue(successor);
    }
    // Publish only after convergence: an initially unseen backedge can invalidate a mode.
    foreach (var block in function.Blocks.Where(outputs.ContainsKey)) {
      var mode = Incoming(function, block, outputs);
      foreach (var instruction in block.Instructions) {
        if (mode is { } known)
          this._modes[instruction] = known;
        mode = Transfer(instruction, mode, segments.Before(instruction));
      }
    }
  }

  /// <summary>The proven mode before this instruction, or null when paths disagree or state is opaque.</summary>
  public long? Before(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return this._modes.TryGetValue(instruction, out var mode) ? mode : null;
  }

  private static long? Incoming(IrFunction function, IrBasicBlock block, Dictionary<IrBasicBlock, long?> outputs) {
    if (ReferenceEquals(block, function.Entry))
      return null;
    var predecessors = block.Predecessors.Where(outputs.ContainsKey).ToArray();
    if (predecessors.Length == 0)
      return null;
    var first = outputs[predecessors[0]];
    return predecessors.All(predecessor => outputs[predecessor] == first) ? first : null;
  }

  private static long? Transfer(IrInstruction instruction, long? mode, ushort? segment) {
    if (instruction is IrCall call) {
      if (call.Callee is not IrFunction { IsDeclaration: true } callee)
        return null;
      if (callee.Name == "rt_screen_mode")
        return call.ArgCount == 1 && call.GetOperand(1) is IrConstantInt { Type: var type } value
          && type == IrType.I16 ? value.Value : null;
      // These native pixel routines neither switch modes nor program VGA addressing registers.
      if (callee.Name is "rt_pset" or "rt_point" or "rt_pset_mode13_offset")
        return mode;
      // A proven A000h packed framebuffer POKE cannot reprogram the VGA mode.
      if (IrDefaultSegmentAnalysis.IsPackedVgaPoke(call, segment))
        return mode;
      return IrEffects.ForInstruction(call).IsEffectFree ? mode : null;
    }
    if (instruction is IrInlineAsm)
      return null;
    if (instruction is IrStore store && !IsProgramStorage(store.Pointer))
      return null;
    if (instruction is IrLoad load && !IsProgramStorage(load.Pointer))
      return null;
    return mode;
  }

  private static bool IsProgramStorage(IrValue pointer) => pointer is IrAlloca
    || pointer is IrGlobalVariable global && (!global.Name.StartsWith("rt_", StringComparison.Ordinal)
      || global.Name is "rt_gx1" or "rt_gy1" or "rt_defseg");
}
