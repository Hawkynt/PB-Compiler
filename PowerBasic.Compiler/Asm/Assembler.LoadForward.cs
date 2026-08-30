namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// When set, a load of a frame cell whose current contents are already known - still in the
  /// register that stored them, or a constant just written there - is replaced by the cheaper
  /// form or removed outright. Off by default; the optimizer turns it on for the optimized
  /// standalone image.
  /// </summary>
  public bool EnableLoadForwarding { get; set; }

  private bool _loadForwardingRan;

  /// <summary>
  /// Redundant-load elimination over the recorded stream. Three shapes, all of them "the value
  /// is already available, so do not go to memory for it" - the last thing standing between the
  /// emitted code and a hand-written one wherever a value passes through a frame slot (a CSE
  /// define feeding its use, a constant feeding a register, a spill feeding its reload):
  /// <list type="bullet">
  ///   <item><c>MOV [BP-8],AX … MOV AX,[BP-8]</c> - the reload is dead, and disappears.</item>
  ///   <item><c>MOV [BP-8],AX … MOV DI,[BP-8]</c> - becomes <c>MOV DI,AX</c>: a register move
  ///     instead of a memory read, and a byte shorter.</item>
  ///   <item><c>MOV WORD PTR [BP-8],7 … MOV DI,[BP-8]</c> - becomes <c>MOV DI,7</c>.</item>
  /// </list>
  ///
  /// Deliberately narrow, because every mistake here is a miscompile:
  /// <list type="bullet">
  ///   <item>Only BP-relative cells. They are SS-relative, so unlike a <c>[label]</c> cell no
  ///     intervening segment load can re-point them.</item>
  ///   <item>The store and the load must be linked by an unbroken chain of RECORDED, byte-adjacent
  ///     instructions: a gap means an unrecorded one (a call, inline asm, a string op) sat between
  ///     them, and that ends the scan. Conditional jumps ARE recorded - they read the flags and
  ///     clobber nothing - so the scan sees through them.</item>
  ///   <item>No bound label in between: something could branch in and reach the load without ever
  ///     having run the store. With that ruled out, the only way to reach the load is to fall
  ///     through from the store, which is what makes looking across a branch sound.</item>
  ///   <item>Any write to the source register, or any store that may alias the cell, ends the
  ///     scan.</item>
  /// </list>
  /// Runs before <see cref="RunSchedule"/>, whose window permutation would invalidate the very
  /// records this reads. <see cref="RunPeephole"/> runs first and repairs the same records for any
  /// destination/length rewrite it makes; every later cut goes through <see cref="RemoveBytes"/>,
  /// which slides the records along with labels and fixups.
  /// </summary>
  public void RunLoadForwarding() {
    // The peephole is also length-changing and may retarget a MOV destination. It must consume its
    // original PeepInstr stream before forwarding can rewrite MOV loads into different shapes.
    this.RunPeephole();
    if (!this.EnableLoadForwarding || this._loadForwardingRan)
      return;
    this._loadForwardingRan = true;
    if (this._schedInstrs is not { Count: > 1 } recs)
      return;

    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    var labels = this.BoundLabelPositions();
    // every byte a still-unresolved fixup will overwrite; an immediate read out of one of them is
    // a placeholder rather than the value the instruction will carry
    var patched = this._fixups.Select(fixup => fixup.Position).ToHashSet();
    var patches = new List<(int Position, byte[] Bytes)>();
    var cuts = new List<(int Start, int Length)>();
    var rewritten = new HashSet<int>();
    var updates = new List<(int Index, int Length, int? Source)>();

    for (var i = 0; i < recs.Count; ++i) {
      var source = this.FrameCellRegister(recs[i], store: true);
      var constant = source is null ? this.FrameCellImmediate(recs[i], patched) : null;
      if (source is null && constant is null)
        continue;

      for (var j = i + 1; j < recs.Count; ++j) {
        if (recs[j].Start != recs[j - 1].Start + recs[j - 1].Length)
          break;                                            // an unrecorded instruction: barrier
        if (labels.Contains(recs[j].Start))
          break;                                            // a branch could land here
        var later = recs[j];

        if (this.FrameCellRegister(later, store: false) is { } loaded
            && later.MemDisp == recs[i].MemDisp && Equals(later.MemBase, recs[i].MemBase)
            && rewritten.Add(later.Start)) {
          // the load's own bytes are replaced in place and any surplus is cut from its tail
          var replacement = source is { } held
            ? loaded == held
              ? []                                          // the register already holds it
              : new byte[] { 0x89, (byte)(0xC0 | (held << 3) | loaded) }   // MOV loaded,held
            : [(byte)(0xB8 + loaded), (byte)constant!.Value, (byte)(constant.Value >> 8)];
          if (replacement.Length > later.Length)
            continue;                                       // never grow (a disp8 load is 3 bytes)
          if (replacement.Length > 0)
            patches.Add((later.Start, replacement));
          cuts.Add((later.Start + replacement.Length, later.Length - replacement.Length));
          // the record has to keep describing the instruction actually sitting there - the
          // scheduler reads these next and permutes whole byte blocks by their length - but not
          // until the scan is done, which still needs the original lengths to walk the chain
          updates.Add((j, replacement.Length, source));
          continue;                                         // cell and register both still valid
        }

        if (source is { } guarded && (later.Writes & (ushort)(1 << guarded)) != 0)
          break;                                            // the register no longer holds it
        if (later.MemWrite && MemMayAlias(recs[i], later))
          break;                                            // the cell no longer holds it
      }
    }

    if (patches.Count == 0 && cuts.Count == 0)
      return;
    foreach (var (index, length, held) in updates)          // records describe the new instructions
      recs[index] = recs[index] with {
        Length = length,
        Reads = held is { } from ? (ushort)(1 << from) : (ushort)0,
        MemRead = false, MemWrite = false, MemBase = null, MemDisp = 0,
      };
    foreach (var (position, bytes) in patches)              // patch at original offsets first
      for (var k = 0; k < bytes.Length; ++k)
        this._buffer[position + k] = bytes[k];
    cuts.Sort((left, right) => right.Start.CompareTo(left.Start));   // then cut, high offsets first
    foreach (var (start, length) in cuts)
      if (length > 0)
        this.RemoveBytes(start, length);
  }

  /// <summary>
  /// The register slot of a plain word <c>MOV</c> between a register and a BP-relative frame
  /// cell - the source of a store, the destination of a load - or null for anything else. The
  /// opcode is read from the buffer, so a prefixed form (segment override, 66h operand size)
  /// never matches.
  /// </summary>
  private int? FrameCellRegister(SchedInstr instr, bool store) {
    if (store ? !instr.MemWrite || instr.MemRead : !instr.MemRead || instr.MemWrite)
      return null;
    if (!IsFrameCell(instr) || !this.HasOpcode(instr, store ? (byte)0x89 : (byte)0x8B))
      return null;
    var modrm = this._buffer[instr.Start + 1];
    return (modrm & 0xC0) == 0xC0 ? null : (modrm >> 3) & 7;   // mod=11 is register-to-register
  }

  /// <summary>
  /// The 16-bit immediate of <c>MOV WORD PTR [BP+d],imm16</c>, or null for anything else - and null
  /// as well when a pending fixup covers those two bytes, because then they are not the immediate
  /// yet. A <c>MOV WORD PTR [BP-88],OFFSET pool+21</c> is emitted with a zero placeholder and the
  /// address written in when the label resolves, so reading the buffer here answers 0 for a cell
  /// that will hold an address, and forwarding that 0 into the reload is a miscompile. It cost
  /// DATAREAD.BAS a garbage string at -O1 and nothing at all at -O0, the pass being off there.
  /// </summary>
  private ushort? FrameCellImmediate(SchedInstr instr, HashSet<int> patched) {
    if (!instr.MemWrite || instr.MemRead || !IsFrameCell(instr) || !this.HasOpcode(instr, 0xC7))
      return null;
    if ((this._buffer[instr.Start + 1] & 0xC0) == 0xC0)
      return null;
    for (var at = instr.Start; at < instr.Start + instr.Length; ++at)
      if (patched.Contains(at))
        return null;
    return (ushort)(this._buffer[instr.Start + instr.Length - 2]
                  | (this._buffer[instr.Start + instr.Length - 1] << 8));
  }

  /// <summary>Only the frame: BP-relative cells are SS-relative, so no segment load can re-point them.</summary>
  private static bool IsFrameCell(SchedInstr instr) => "BP".Equals(instr.MemBase);

  private bool HasOpcode(SchedInstr instr, byte opcode) =>
    instr.Length is >= 3 and <= 6
    && instr.Start + instr.Length <= this._buffer.Count
    && this._buffer[instr.Start] == opcode;
}
