namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  private bool _encodingSelectionRan;

  /// <summary>
  /// O0092, target-independent shrink wins used by the SPEED scheduler:
  /// <list type="bullet">
  ///   <item><c>ADD r16,1</c> / <c>SUB r16,1</c> become one-byte <c>INC</c>/<c>DEC</c> when CF is
  ///     unobservable before a later instruction fully redefines the arithmetic flags.</item>
  ///   <item><c>MOV r16,0</c> becomes <c>XOR r16,r16</c> when the MOV's preserved incoming flags are
  ///     likewise unobservable before a full flag definition.</item>
  /// </list>
  ///
  /// Both choices shrink the stream and are at least as fast on the 8086-class targets this scheduler
  /// serves. The legality proof is intentionally stronger than necessary: any recorded flag read,
  /// any unrecorded instruction gap, or reaching the end of the recorded run before a complete flag
  /// definition makes the transform decline. That preserves exact machine-level flag behaviour for
  /// inline asm and callers as well as BASIC control flow.
  /// </summary>
  private void RunEncodingSelection() {
    if (this._encodingSelectionRan)
      return;
    this._encodingSelectionRan = true;
    if (this._schedInstrs is not { Count: > 1 } recs)
      return;

    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    this.SelectIncDec(recs);

    // INC/DEC deliberately preserve CF. Re-sort after their cuts and only then decide whether a
    // MOV-zero may become XOR, so an ADD/SUB-1 that was shortened can no longer masquerade as the
    // full flag-kill that justified changing the earlier MOV's flag behaviour.
    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    this.SelectZeroIdioms(recs);
  }

  /// <summary>ADD/SUB r16,1 -> INC/DEC r16 when the only semantic difference, CF, is dead.</summary>
  private void SelectIncDec(List<SchedInstr> recs) {
    var rewrites = new List<(int Index, int Start, int OldLength, byte Opcode)>();

    for (var i = 0; i < recs.Count; ++i) {
      if (!this.TryAddSubOne(recs[i], out var register, out var increment))
        continue;
      if (!this.FlagsFullyKilledBeforeRead(recs, i, excludeIncDecCandidateAsKiller: true))
        continue;
      rewrites.Add((i, recs[i].Start, recs[i].Length,
        (byte)((increment ? 0x40 : 0x48) + register)));
    }

    if (rewrites.Count == 0)
      return;

    foreach (var (index, start, _, opcode) in rewrites) {
      this._buffer[start] = opcode;
      recs[index] = recs[index] with { Length = 1 };
    }

    foreach (var (_, start, oldLength, _) in rewrites.OrderByDescending(r => r.Start))
      this.RemoveBytes(start + 1, oldLength - 1);
  }

  /// <summary>MOV r16,0 -> XOR r16,r16 when changing the flags cannot be observed.</summary>
  private void SelectZeroIdioms(List<SchedInstr> recs) {
    var rewrites = new List<(int Index, int Start, int OldLength, int Register)>();

    for (var i = 0; i < recs.Count; ++i) {
      if (!this.TryMovWordZero(recs[i], out var register))
        continue;
      if (!this.FlagsFullyKilledBeforeRead(recs, i, excludeIncDecCandidateAsKiller: false))
        continue;
      rewrites.Add((i, recs[i].Start, recs[i].Length, register));
    }

    if (rewrites.Count == 0)
      return;

    foreach (var (index, start, _, register) in rewrites) {
      this._buffer[start] = 0x31;                         // XOR r/m16,r16
      this._buffer[start + 1] = (byte)(0xC0 | (register << 3) | register);
      recs[index] = recs[index] with {
        Length = 2,
        Reads = (ushort)(1 << register),
        WritesFlags = true,
      };
    }

    foreach (var (_, start, oldLength, _) in rewrites.OrderByDescending(r => r.Start))
      this.RemoveBytes(start + 2, oldLength - 2);
  }

  /// <summary>Recognizes the ordinary 16-bit 83 /0|/5,1 spelling emitted by <c>ADD/SUB r,1</c>.</summary>
  private bool TryAddSubOne(SchedInstr instr, out int register, out bool increment) {
    register = 0;
    increment = false;
    if (instr.Length != 3 || instr.MemRead || instr.MemWrite || instr.Start + 3 > this._buffer.Count)
      return false;
    if (this._buffer[instr.Start] != 0x83 || this._buffer[instr.Start + 2] != 1)
      return false;
    var modrm = this._buffer[instr.Start + 1];
    if ((modrm & 0xC0) != 0xC0)
      return false;
    var operation = (modrm >> 3) & 7;
    if (operation is not (0 or 5))
      return false;
    register = modrm & 7;
    increment = operation == 0;
    return true;
  }

  /// <summary>Recognizes a numeric <c>MOV r16,0</c>; unresolved label immediates are rejected.</summary>
  private bool TryMovWordZero(SchedInstr instr, out int register) {
    register = 0;
    if (instr.Length != 3 || instr.MemRead || instr.MemWrite || instr.Start + 3 > this._buffer.Count)
      return false;
    if (this._fixups.Any(fixup => fixup.Position >= instr.Start && fixup.Position < instr.Start + instr.Length))
      return false;                                        // OFFSET label uses zero placeholder bytes pre-resolution
    var opcode = this._buffer[instr.Start];
    if (opcode is < 0xB8 or > 0xBF || this._buffer[instr.Start + 1] != 0 || this._buffer[instr.Start + 2] != 0)
      return false;
    register = opcode - 0xB8;
    return true;
  }

  /// <summary>
  /// True when the flags produced/preserved by instruction <paramref name="index"/> cannot be read
  /// before an instruction that defines the arithmetic condition flags independently of their old
  /// values. A gap is an unknown instruction and therefore a barrier. ADC/SBB and other flag readers
  /// fail before they can qualify as killers.
  /// </summary>
  private bool FlagsFullyKilledBeforeRead(List<SchedInstr> recs, int index, bool excludeIncDecCandidateAsKiller) {
    var cursor = recs[index].Start + recs[index].Length;
    for (var i = index + 1; i < recs.Count; ++i) {
      var later = recs[i];
      if (later.Start != cursor)
        return false;                                      // unrecorded instruction may inspect flags
      if (later.ReadsFlags)
        return false;
      if (this.FullyDefinesArithmeticFlags(later)) {
        if (!excludeIncDecCandidateAsKiller || !this.TryAddSubOne(later, out _, out _))
          return true;
      }
      cursor = later.Start + later.Length;
    }
    return false;                                          // preserve flags across procedure/region exit
  }

  /// <summary>
  /// Conservative set of instructions that replace CF/OF/ZF/SF/PF from their own operands and do
  /// not depend on incoming flags: ADD/OR/AND/SUB/XOR/CMP, TEST and NEG in their common encodings.
  /// It is intentionally not "anything with WritesFlags": INC/DEC preserve CF, while ADC/SBB read it.
  /// </summary>
  private bool FullyDefinesArithmeticFlags(SchedInstr instr) {
    if (!instr.WritesFlags || instr.ReadsFlags || instr.Length <= 0 || instr.Start + instr.Length > this._buffer.Count)
      return false;

    var at = instr.Start;
    // Ignore ordinary size/segment prefixes when classifying the opcode. They do not change the
    // arithmetic flag semantics of the instruction that follows.
    while (at < instr.Start + instr.Length && this._buffer[at] is 0x66 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65)
      ++at;
    if (at >= instr.Start + instr.Length)
      return false;

    var opcode = this._buffer[at];
    if (opcode is 0x00 or 0x01 or 0x02 or 0x03       // ADD
        or 0x08 or 0x09 or 0x0A or 0x0B             // OR
        or 0x20 or 0x21 or 0x22 or 0x23             // AND
        or 0x28 or 0x29 or 0x2A or 0x2B             // SUB
        or 0x30 or 0x31 or 0x32 or 0x33             // XOR
        or 0x38 or 0x39 or 0x3A or 0x3B)            // CMP
      return true;

    if (opcode is 0x04 or 0x05 or 0x0C or 0x0D or 0x24 or 0x25
        or 0x2C or 0x2D or 0x34 or 0x35 or 0x3C or 0x3D)
      return true;                                    // accumulator immediate forms

    if (opcode is 0x80 or 0x81 or 0x83) {
      if (at + 1 >= instr.Start + instr.Length)
        return false;
      var operation = (this._buffer[at + 1] >> 3) & 7;
      return operation is 0 or 1 or 4 or 5 or 6 or 7;
    }

    if (opcode is 0x84 or 0x85 or 0xA8 or 0xA9)
      return true;                                    // TEST

    if (opcode is 0xF6 or 0xF7) {
      if (at + 1 >= instr.Start + instr.Length)
        return false;
      var operation = (this._buffer[at + 1] >> 3) & 7;
      return operation is 0 or 3;                     // TEST / NEG
    }

    return false;
  }
}
