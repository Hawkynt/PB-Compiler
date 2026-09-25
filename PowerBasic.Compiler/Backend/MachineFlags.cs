namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Whether the condition flags an instruction leaves behind can still be observed - the one question
/// every flag-changing rewrite (INC for ADD 1, XOR r,r for MOV r,0, LEA for ADD) has to ask first.
///
/// <para>
/// It used to be answered by four private copies, and they did not agree. Three treated any
/// instruction that writes flags as killing them, which is wrong for INC and DEC: both leave CF alone,
/// so in <c>ADD / INC / ADC</c> the ADC still reads the carry the ADD produced. Only one copy knew
/// that, and it was the one whose rewrites mattered least. There is now one answer, the strict one.
/// </para>
/// </summary>
public static class MachineFlags {

  /// <summary>
  /// Whether nothing after <paramref name="index"/> reads the flags before something replaces all of
  /// them. The end of a block proves nothing - a successor may branch on the flags - unless the block
  /// ends by leaving the function, since flags are not part of any call or return contract.
  /// </summary>
  public static bool DeadAfter(MBlock block, int index) {
    for (var i = index + 1; i < block.Instructions.Count; ++i) {
      var instruction = block.Instructions[i];
      if (instruction.Effect.ReadsFlags)
        return false;
      if (Kills(instruction))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Whether an instruction leaves no flag of the incoming state observable. The ALU operations that
  /// define every arithmetic flag without reading any, plus the two ways out of the code: a CALL (the
  /// callee owns the flags from there, and whatever the caller then tests is the callee's answer) and
  /// a RET.
  /// </summary>
  private static bool Kills(MInstr instruction)
    => instruction.Opcode switch {
      MOpcode.Ret or MOpcode.Call or MOpcode.CallFar => true,
      MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor
        or MOpcode.Cmp or MOpcode.Test or MOpcode.Neg
        => instruction.Effect.WritesFlags && !instruction.Effect.ReadsFlags,
      _ => false,
    };
}
