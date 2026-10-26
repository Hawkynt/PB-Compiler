namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>
/// Forward must-facts for the DOS DEF SEG cell. The value is the actual 16-bit
/// segment word, not a signed BASIC INTEGER interpretation of its bits.
/// </summary>
public sealed class IrDefaultSegmentAnalysis {
  private readonly Dictionary<IrInstruction, ushort> _segments = [];

  internal IrDefaultSegmentAnalysis(IrFunction function) {
    if (function.Entry is null || function.HasErrorHandler || function.HasInlineAsm)
      return;
    var outputs = new Dictionary<IrBasicBlock, ushort?>();
    var pending = new Queue<IrBasicBlock>();
    pending.Enqueue(function.Entry);
    while (pending.TryDequeue(out var block)) {
      var segment = Incoming(function, block, outputs);
      foreach (var instruction in block.Instructions)
        segment = Transfer(instruction, segment);
      if (outputs.TryGetValue(block, out var previous) && previous == segment)
        continue;
      outputs[block] = segment;
      foreach (var next in block.Successors)
        pending.Enqueue(next);
    }
    // A backedge may disprove an apparent constant; publish only after convergence.
    foreach (var block in function.Blocks.Where(outputs.ContainsKey)) {
      var segment = Incoming(function, block, outputs);
      foreach (var instruction in block.Instructions) {
        if (segment is { } known)
          this._segments[instruction] = known;
        segment = Transfer(instruction, segment);
      }
    }
  }

  public ushort? Before(IrInstruction instruction) {
    ArgumentNullException.ThrowIfNull(instruction);
    return this._segments.TryGetValue(instruction, out var segment) ? segment : null;
  }

  /// <summary>A direct POKE into a proven packed VGA pixel byte is safe to specialize.</summary>
  internal static bool IsPackedVgaPoke(IrCall call, ushort? segment)
    => segment == 0xA000
       && call.Callee is IrFunction { IsDeclaration: true, Name: "rt_poke" }
       && call.Convention == IrCallConvention.Basic
       && call.Type.IsVoid
       && call.ArgCount == 2
       && call.GetOperand(1) is IrConstantInt { Value: >= 0 and < 64000, Type: { Bits: 16 } }
       && call.GetOperand(2).Type == IrType.I16;

  private static ushort? Incoming(IrFunction function, IrBasicBlock block,
      Dictionary<IrBasicBlock, ushort?> outputs) {
    if (ReferenceEquals(block, function.Entry))
      return null;
    var predecessors = block.Predecessors.Where(outputs.ContainsKey).ToArray();
    if (predecessors.Length == 0)
      return null;
    var first = outputs[predecessors[0]];
    return predecessors.All(previous => outputs[previous] == first) ? first : null;
  }

  private static ushort? Transfer(IrInstruction instruction, ushort? segment) {
    if (instruction is IrStore { Pointer: IrGlobalVariable { Name: "rt_defseg" } } setSegment)
      return setSegment.Value is IrConstantInt value ? unchecked((ushort)value.Value) : null;
    if (instruction is IrCall call) {
      if (call.Callee is not IrFunction { IsDeclaration: true } callee)
        return null;
      if (callee.Name is "rt_screen_mode" or "rt_pset" or "rt_point" or "rt_pset_mode13_offset")
        return segment;
      if (IsPackedVgaPoke(call, segment))
        return segment; // A000h video RAM cannot mutate rt_defseg in DS.
      return IrEffects.ForInstruction(call).IsEffectFree ? segment : null;
    }
    if (instruction is IrInlineAsm)
      return null;
    if (instruction is IrStore store && !IsProgramStorage(store.Pointer))
      return null;
    if (instruction is IrLoad load && !IsProgramStorage(load.Pointer))
      return null;
    return segment;
  }

  private static bool IsProgramStorage(IrValue pointer) => pointer is IrAlloca
    || pointer is IrGlobalVariable global && (!global.Name.StartsWith("rt_", StringComparison.Ordinal)
      || global.Name is "rt_gx1" or "rt_gy1");
}
