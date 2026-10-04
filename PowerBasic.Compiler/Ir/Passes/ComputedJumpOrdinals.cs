namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Label addresses as numbers, for a target whose code addresses do not fit a PB code pointer.
///
/// <para>
/// <c>CODEPTR32(label)</c> is a segment and a 16-bit offset, and <c>GOTO DWORD</c> / <c>GOSUB DWORD</c>
/// jump to the offset half. On DOS that half is the label's real address. A flat machine's code
/// addresses are wider than 16 bits, so a label's address truncated to the offset would jump nowhere.
/// What a program can do with the number is only carry it around and jump to it, so any number that
/// names the label is as good as its address: each label whose address is taken gets an ordinal,
/// module-wide and starting at 1, and each computed jump becomes a switch over the ordinals of the
/// labels it can reach. Only the address a program sees as a number changes: the handler an
/// <c>ON ERROR GOTO</c> arms and the landing an <c>EXIT FAR</c> keeps stay real code addresses.
/// </para>
/// </summary>
public static class ComputedJumpOrdinals {

  public static void Run(IrModule module) {
    var ordinals = new Dictionary<IrBasicBlock, long>();
    long OrdinalOf(IrBasicBlock block) {
      if (!ordinals.TryGetValue(block, out var ordinal))
        ordinals.Add(block, ordinal = ordinals.Count + 1);
      return ordinal;
    }

    foreach (var function in module.Functions.Where(f => !f.IsDeclaration))
      foreach (var block in function.Blocks.ToList())
        foreach (var instruction in block.Instructions.ToList()) {
          if (instruction is IrCast { Op: IrCastOp.PtrToInt, Value: IrBlockAddress taken } cast) {
            cast.ReplaceAllUsesWith(new IrConstantInt(cast.Type, OrdinalOf(taken.Block)));
            cast.DropOperandUses();
            block.Remove(cast);
            continue;
          }
          if (instruction is IrIndirectBr jump)
            ReplaceJump(function, block, jump, OrdinalOf);
        }
  }

  private static void ReplaceJump(IrFunction function, IrBasicBlock block, IrIndirectBr jump, Func<IrBasicBlock, long> ordinalOf) {
    var nowhere = function.CreateBlock($"{block.Label}.nowhere");
    nowhere.Append(new IrUnreachable());
    var ordinal = block.InsertBefore(new IrCast(IrCastOp.PtrToInt, jump.Address, IrType.I32), jump);
    var dispatch = new IrSwitch(ordinal, nowhere);
    foreach (var target in jump.Targets.Distinct())
      dispatch.AddCase(ordinalOf(target), target);
    block.InsertBefore(dispatch, jump);
    jump.DropOperandUses();
    block.Remove(jump);
  }
}
