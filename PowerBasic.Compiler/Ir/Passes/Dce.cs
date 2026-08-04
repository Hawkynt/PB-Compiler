namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Dead-code elimination: removes instructions with no users and no side effects,
/// cascading through operands as they become unused. Stores, calls and terminators
/// are never removed (their effect or control transfer is observable). This is what
/// sweeps up the residue InstCombine and mem2reg leave behind.
/// </summary>
public static class Dce {

  /// <summary>Removes dead instructions in place; returns how many were removed.</summary>
  public static int Run(IrFunction fn) {
    var removed = 0;
    var worklist = new Queue<IrInstruction>(fn.AllInstructions);
    while (worklist.Count > 0) {
      var inst = worklist.Dequeue();
      if (inst.Parent is null || !inst.HasNoUsers || HasSideEffects(inst))
        continue;

      foreach (var operand in inst.Operands)         // operands may now be dead
        if (operand is IrInstruction producer)
          worklist.Enqueue(producer);
      inst.EraseFromParent();
      ++removed;
    }
    return removed;
  }

  private static bool HasSideEffects(IrInstruction inst) =>
    inst is IrStore or IrCall or IrInlineAsm || inst.IsTerminator;
}
