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
  public static bool DeadAfter(MBlock block, int index) => DeadAfter(null, block, index);

  /// <summary>
  /// The same, looking past the end of the block when the function is given: the flags are dead there
  /// when every successor - the fallthrough, a branch target, a label an inline-assembly jump names -
  /// replaces them before reading them. A block met again on the way counts as a reader: a loop that
  /// comes back without overwriting them is not a proof.
  /// </summary>
  public static bool DeadAfter(X86MachineFunction? function, MBlock block, int index)
    => DeadAfter(function, block, index, new HashSet<string>(StringComparer.Ordinal));

  private static bool DeadAfter(X86MachineFunction? function, MBlock block, int index, HashSet<string> visited) {
    for (var i = index + 1; i < block.Instructions.Count; ++i) {
      var instruction = block.Instructions[i];
      if (instruction.Effect.ReadsFlags)
        return false;
      if (Kills(instruction))
        return true;
    }
    if (function is null || !visited.Add(block.Label))
      return false;
    var successors = block.SuccessorsWithAsmJumps().ToList();
    if (successors.Count == 0)
      return false;
    foreach (var label in successors)
      if (function.Blocks.Find(candidate => candidate.Label == label) is not { } next
          || !DeadAfter(function, next, -1, visited))
        return false;
    return true;
  }

  /// <summary>
  /// Whether <paramref name="condition"/> holds after <c>CMP left, right</c> at the given width, or null
  /// for the one condition a compare's result does not settle here (parity). This is the constant
  /// folding of a machine branch: every other flag a CMP sets is a function of the two operands.
  /// </summary>
  public static bool? CompareHolds(Asm.Condition condition, long left, long right, int bits) {
    var mask = bits >= 64 ? -1L : (1L << bits) - 1;
    var signBit = 1L << (bits - 1);
    long Signed(long value) => (value & signBit) != 0 ? (value & mask) - (mask + 1) : value & mask;
    ulong Unsigned(long value) => (ulong)(value & mask);
    var (sl, sr, ul, ur) = (Signed(left), Signed(right), Unsigned(left), Unsigned(right));
    var difference = (left - right) & mask;
    var overflow = Signed(difference) != sl - sr;
    return condition switch {
      Asm.Condition.Overflow => overflow,
      Asm.Condition.NotOverflow => !overflow,
      Asm.Condition.Below => ul < ur,
      Asm.Condition.AboveOrEqual => ul >= ur,
      Asm.Condition.Equal => ul == ur,
      Asm.Condition.NotEqual => ul != ur,
      Asm.Condition.BelowOrEqual => ul <= ur,
      Asm.Condition.Above => ul > ur,
      Asm.Condition.Sign => (difference & signBit) != 0,
      Asm.Condition.NotSign => (difference & signBit) == 0,
      Asm.Condition.Less => sl < sr,
      Asm.Condition.GreaterOrEqual => sl >= sr,
      Asm.Condition.LessOrEqual => sl <= sr,
      Asm.Condition.Greater => sl > sr,
      _ => null,
    };
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
