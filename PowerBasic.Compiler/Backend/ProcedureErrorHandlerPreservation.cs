namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Preserves the caller's process-global <c>ON ERROR</c> handler around one routed procedure body.
///
/// <para>
/// The DOS runtime keeps the active handler as three words: handler address, owning BP and owning SP.
/// Arming an <c>ON ERROR</c> inside a procedure overwrites that triple. A procedure therefore has to
/// save the caller's triple on entry and restore it on every ordinary return, exactly as the legacy
/// emitter does. The module body has no caller and must not use this pass.
/// </para>
///
/// <para>
/// This is machine IR rather than target-neutral SSA because the rule is an ABI/frame concern: the
/// saved triple belongs to this invocation's stack frame. Keeping it here also lets normal liveness
/// and register allocation choose the scratch register without clobbering live parameters or return
/// registers. The six-byte slot is created before allocation, so recursion naturally gets one copy per
/// invocation and final frame-elision checks correctly retain BP.
/// </para>
/// </summary>
public static class ProcedureErrorHandlerPreservation {

  private static readonly string[] _cells = ["rt_onerr", "rt_onerr_bp", "rt_onerr_sp"];

  /// <summary>Adds entry save and return restore sequences. Returns the number of inserted instructions.</summary>
  public static int Run(MFunction function) {
    if (function.Blocks.Count == 0)
      return 0;

    var slot = function.StackSlots.Count;
    function.StackSlots.Add(6);

    var scratch = new MOperand.Register(MReg.Virtual(function.VirtualRegisterCount++, MRegSize.Word));
    var entry = new List<MInstr>(6);
    for (var index = 0; index < _cells.Length; ++index) {
      var offset = index * 2;
      entry.Add(Load(scratch, new MOperand.DataCell(_cells[index], 0, MRegSize.Word)));
      entry.Add(Store(new MOperand.StackSlot(slot, MRegSize.Word, offset), scratch));
    }
    function.Blocks[0].Instructions.InsertRange(0, entry);

    var inserted = entry.Count;
    foreach (var block in function.Blocks) {
      for (var index = block.Instructions.Count - 1; index >= 0; --index) {
        if (block.Instructions[index].Opcode != MOpcode.Ret)
          continue;
        var restore = new List<MInstr>(6);
        for (var cell = 0; cell < _cells.Length; ++cell) {
          var offset = cell * 2;
          restore.Add(Load(scratch, new MOperand.StackSlot(slot, MRegSize.Word, offset)));
          restore.Add(Store(new MOperand.DataCell(_cells[cell], 0, MRegSize.Word), scratch));
        }
        block.Instructions.InsertRange(index, restore);
        inserted += restore.Count;
      }
    }
    return inserted;
  }

  private static MInstr Load(MOperand.Register destination, MOperand source)
    => new(MOpcode.Mov, [destination, source],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false));

  private static MInstr Store(MOperand destination, MOperand.Register source)
    => new(MOpcode.Mov, [destination, source],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true));
}
