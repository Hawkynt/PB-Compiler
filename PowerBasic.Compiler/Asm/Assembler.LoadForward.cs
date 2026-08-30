namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// When set, a load of a frame cell whose current contents are already known - still in the
  /// register that stored them, or a constant just written there - is replaced by the cheaper
  /// form or removed outright. The same recorded stream also drops an earlier plain word store
  /// when a later plain word store fully overwrites that frame cell before any surviving read,
  /// and removes a redundant zero test when an earlier arithmetic instruction already left
  /// ZF/SF/PF describing the unchanged result. Off by default; the optimizer turns it on for
  /// the optimized standalone image.
  /// </summary>
  public bool EnableLoadForwarding { get; set; }

  private bool _loadForwardingRan;

  /// <summary>
  /// Redundant-load elimination over the recorded stream. Three shapes, all of them "the value
  /// is already available, so do not go to memory for it" - the last thing standing between the
  /// emitted code and a hand-written one wherever a value passes through a frame slot or direct
  /// variable cell (a CSE define feeding its use, a constant feeding a register, a spill feeding
  /// its reload):
  /// <list type="bullet">
  ///   <item><c>MOV [BP-8],AX … MOV AX,[BP-8]</c> - the reload is dead, and disappears.</item>
  ///   <item><c>MOV [variable],AX … MOV AX,[variable]</c> - the direct-variable reload disappears too.</item>
  ///   <item><c>MOV [BP-8],AX … MOV DI,[BP-8]</c> - becomes <c>MOV DI,AX</c>: a register move
  ///     instead of a memory read, and a byte shorter.</item>
  ///   <item><c>MOV WORD PTR [BP-8],7 … MOV DI,[BP-8]</c> - becomes <c>MOV DI,7</c>.</item>
  /// </list>
  ///
  /// After those rewrites, <see cref="RunDeadFrameStoreElimination"/> consumes the repaired records:
  /// if a plain word store is fully overwritten by another plain word store to the same frame cell
  /// before any surviving read, the first store is unobservable and disappears as well. This is the
  /// straight-line, recording-proven subset of O0065; a final store that is merely never read later
  /// still needs complete memory recording or a compiler-private-frame declaration.
  ///
  /// Deliberately narrow, because every mistake here is a miscompile:
  /// <list type="bullet">
  ///   <item>Only BP-relative cells and unprefixed direct-label cells. A segment override changes
  ///     which direct cell an instruction addresses, so those forms never qualify.</item>
  ///   <item>The store and the load must be linked by an unbroken chain of RECORDED, byte-adjacent
  ///     instructions: a gap means an unrecorded one (a call, inline asm, segment-register load,
  ///     string op) sat between them, and that ends the scan. Conditional jumps ARE recorded - they
  ///     read the flags and clobber nothing - so the scan sees through them.</item>
  ///   <item>No bound label in between: something could branch in and reach the load without ever
  ///     having run the store. With that ruled out, the only way to reach the load is to fall
  ///     through from the store, which is what makes looking across a branch sound.</item>
  ///   <item>Any write to the source register, or any store that may alias the cell, ends the
  ///     forwarding scan. Dead-store elimination separately stops at any surviving aliasing read
  ///     or at any write that is not an exact full-word replacement.</item>
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
      var source = this.ForwardableCellRegister(recs[i], store: true);
      var constant = source is null ? this.FrameCellImmediate(recs[i], patched) : null;
      if (source is null && constant is null)
        continue;

      for (var j = i + 1; j < recs.Count; ++j) {
        if (recs[j].Start != recs[j - 1].Start + recs[j - 1].Length)
          break;                                            // an unrecorded instruction: barrier
        if (labels.Contains(recs[j].Start))
          break;                                            // a branch could land here
        var later = recs[j];

        if (this.ForwardableCellRegister(later, store: false) is { } loaded
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

    if (patches.Count > 0 || cuts.Count > 0) {
      foreach (var (index, length, held) in updates)        // records describe the new instructions
        recs[index] = recs[index] with {
          Length = length,
          Reads = held is { } from ? (ushort)(1 << from) : (ushort)0,
          MemRead = false, MemWrite = false, MemBase = null, MemDisp = 0,
        };
      foreach (var (position, bytes) in patches)            // patch at original offsets first
        for (var k = 0; k < bytes.Length; ++k)
          this._buffer[position + k] = bytes[k];
      cuts.Sort((left, right) => right.Start.CompareTo(left.Start));   // then cut, high offsets first
      foreach (var (start, length) in cuts)
        if (length > 0)
          this.RemoveBytes(start, length);
    }

    // O0065 composes after O0034: a load that was the only reader may have just become a register
    // move (or disappeared), exposing the older frame store as dead before the next overwrite.
    this.RunDeadFrameStoreElimination(recs);
    this.RunFlagReuse();
  }

  /// <summary>
  /// O0065, conservative local form: removes <c>MOV [BP-d],...</c> when another plain word MOV to
  /// exactly the same cell is reached by uninterrupted fall-through before any surviving read.
  /// A record gap, a bound label, an aliasing read, a partial/RMW write or an unknown alias ends the
  /// proof. This deliberately does not claim that a store with no later recorded reader is dead:
  /// unrecorded memory forms can still observe it, which is the whole-procedure blocker documented
  /// by O0065.
  /// </summary>
  private void RunDeadFrameStoreElimination(List<SchedInstr> recs) {
    if (recs.Count < 2)
      return;

    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    var labels = this.BoundLabelPositions();
    var cuts = new List<(int Start, int Length)>();
    var dead = new HashSet<int>();

    for (var i = 0; i < recs.Count - 1; ++i) {
      var store = recs[i];
      if (!this.IsPlainWordFrameStore(store) || labels.Contains(store.Start) || dead.Contains(store.Start))
        continue;

      for (var j = i + 1; j < recs.Count; ++j) {
        var later = recs[j];
        if (later.Start != recs[j - 1].Start + recs[j - 1].Length)
          break;                                            // unrecorded memory/call/asm barrier
        if (labels.Contains(later.Start))
          break;                                            // another control-flow path may enter here

        if (later.MemRead && MemMayAlias(store, later))
          break;                                            // some surviving operation observes the old value
        if (!later.MemWrite || !MemMayAlias(store, later))
          continue;

        // Only an exact full-word plain MOV kills the old store. A byte store or an RMW operation
        // reads/retains some of the previous value, and an unknown alias gives us no overwrite proof.
        if (this.IsPlainWordFrameStore(later) && SameFrameCell(store, later)) {
          cuts.Add((store.Start, store.Length));
          dead.Add(store.Start);
        }
        break;
      }
    }

    cuts.Sort((left, right) => right.Start.CompareTo(left.Start));
    foreach (var (start, length) in cuts)
      this.RemoveBytes(start, length);
  }

  /// <summary>A plain word MOV that completely defines one BP-relative frame cell.</summary>
  private bool IsPlainWordFrameStore(SchedInstr instr) =>
    instr.MemWrite && !instr.MemRead && instr.MemBytes == 2 && IsFrameCell(instr)
    && (this.HasOpcode(instr, 0x89) || this.HasOpcode(instr, 0xC7));

  private static bool SameFrameCell(SchedInstr left, SchedInstr right) =>
    left.MemBytes == right.MemBytes && left.MemDisp == right.MemDisp
    && Equals(left.MemBase, right.MemBase);

  /// <summary>
  /// O0081: remove a zero test when the last flag-writing instruction already left ZF/SF/PF
  /// describing the same, still-unchanged register value. Only branches that consume exactly one of
  /// those result flags qualify; CF/OF-based and signed-order conditions deliberately keep the test.
  /// The scan has the same barriers as load forwarding: an unrecorded instruction or an intervening
  /// label ends the proof.
  /// </summary>
  private void RunFlagReuse() {
    if (this._schedInstrs is not { Count: > 1 } recs)
      return;

    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    var labels = this.BoundLabelPositions();
    var removals = new List<(int Start, int Length)>();

    for (var i = 1; i + 1 < recs.Count; ++i) {
      var test = recs[i];
      if (this.ZeroTestRegister(test) is not { } tested)
        continue;
      if (labels.Contains(test.Start))
        continue;                                           // another path may enter at the test

      var branch = recs[i + 1];
      if (branch.Start != test.Start + test.Length || labels.Contains(branch.Start)
          || !this.IsZeroSignParityBranch(branch))
        continue;

      var bit = (ushort)(1 << tested);
      var cursor = test.Start;
      var reusable = false;
      for (var j = i - 1; j >= 0; --j) {
        var prior = recs[j];
        if (prior.Start + prior.Length != cursor || labels.Contains(cursor))
          break;                                            // gap/entry point destroys dominance
        if ((prior.Writes & bit) != 0 && !prior.WritesFlags)
          break;                                            // value changed without matching flags
        if (prior.WritesFlags) {
          reusable = this.SetsValueFlags(prior, tested);
          break;                                            // the closest flag writer owns EFLAGS
        }
        cursor = prior.Start;
      }

      if (reusable)
        removals.Add((test.Start, test.Length));
    }

    removals.Sort((left, right) => right.Start.CompareTo(left.Start));
    foreach (var (start, length) in removals)
      this.RemoveBytes(start, length);
  }

  /// <summary>
  /// The register slot tested for zero by a word <c>CMP r,0</c>, <c>TEST r,r</c> or <c>OR r,r</c>;
  /// null for every other instruction. Byte/dword forms stay conservative for now.
  /// </summary>
  private int? ZeroTestRegister(SchedInstr instr) {
    if (instr.MemRead || instr.MemWrite || instr.Length < 2 || instr.Start + instr.Length > this._buffer.Count)
      return null;
    var op = this._buffer[instr.Start];

    if (op is 0x81 or 0x83) {
      var modrm = this._buffer[instr.Start + 1];
      if ((modrm & 0xC0) != 0xC0 || ((modrm >> 3) & 7) != 7)
        return null;
      if (op == 0x83)
        return instr.Length == 3 && this._buffer[instr.Start + 2] == 0 ? modrm & 7 : null;
      return instr.Length == 4 && this._buffer[instr.Start + 2] == 0 && this._buffer[instr.Start + 3] == 0
        ? modrm & 7 : null;
    }

    if (op == 0x3D)
      return instr.Length == 3 && this._buffer[instr.Start + 1] == 0 && this._buffer[instr.Start + 2] == 0 ? 0 : null;

    if (op is 0x85 or 0x09) {
      var modrm = this._buffer[instr.Start + 1];
      return (modrm & 0xC0) == 0xC0 && ((modrm >> 3) & 7) == (modrm & 7) ? modrm & 7 : null;
    }

    return null;
  }

  /// <summary>True when the branch consumes only ZF, SF or PF, all defined directly from a result.</summary>
  private bool IsZeroSignParityBranch(SchedInstr instr) {
    if (!instr.ReadsFlags || instr.WritesFlags || instr.Length < 2 || instr.Start + instr.Length > this._buffer.Count)
      return false;
    var op = this._buffer[instr.Start];
    int condition;
    if (op is >= 0x70 and <= 0x7F)
      condition = op & 0x0F;                                // includes the inverted 8086 long-Jcc prefix
    else if (op == 0x0F && instr.Length >= 4 && this._buffer[instr.Start + 1] is >= 0x80 and <= 0x8F)
      condition = this._buffer[instr.Start + 1] & 0x0F;
    else
      return false;
    return condition is 0x4 or 0x5 or 0x8 or 0x9 or 0xA or 0xB;
  }

  /// <summary>
  /// True when <paramref name="instr"/> is a word arithmetic instruction whose ZF/SF/PF describe
  /// the final value in <paramref name="register"/>. The allow-list excludes operations such as
  /// IMUL whose recorded "writes flags" means only CF/OF are defined.
  /// </summary>
  private bool SetsValueFlags(SchedInstr instr, int register) {
    if (!instr.WritesFlags || (instr.Writes & (ushort)(1 << register)) == 0
        || instr.Length < 1 || instr.Start + instr.Length > this._buffer.Count)
      return false;

    var op = this._buffer[instr.Start];
    if (op is >= 0x40 and <= 0x4F)
      return (op & 7) == register;                           // INC/DEC r16

    if (op is 0x01 or 0x03 or 0x09 or 0x0B or 0x11 or 0x13 or 0x19 or 0x1B
        or 0x21 or 0x23 or 0x29 or 0x2B or 0x31 or 0x33) {
      var modrm = this._buffer[instr.Start + 1];
      var destination = (op & 2) != 0 ? (modrm >> 3) & 7 : modrm & 7;
      return destination == register;
    }

    if (op is 0x05 or 0x0D or 0x15 or 0x1D or 0x25 or 0x2D or 0x35)
      return register == 0;                                 // accumulator-immediate ALU

    if (op is 0x81 or 0x83) {
      var modrm = this._buffer[instr.Start + 1];
      return (modrm & 0xC0) == 0xC0 && ((modrm >> 3) & 7) != 7 && (modrm & 7) == register;
    }

    if (op == 0xF7 && instr.Length >= 2) {
      var modrm = this._buffer[instr.Start + 1];
      return (modrm & 0xF8) == 0xD8 && (modrm & 7) == register;  // NEG r16
    }

    if (op is 0xD1 or 0xC1 && instr.Length >= 2) {
      var modrm = this._buffer[instr.Start + 1];
      var operation = (modrm >> 3) & 7;
      return (modrm & 0xC0) == 0xC0 && operation is 4 or 5 or 7 && (modrm & 7) == register;
    }

    return false;
  }

  /// <summary>
  /// The register slot of a plain word <c>MOV</c> between a register and a forwardable cell - the
  /// source of a store, the destination of a load - or null for anything else. BP-relative cells use
  /// the ordinary 89/8B encodings; direct AX stores additionally use the A3 accumulator short form.
  /// Prefixed forms never match.
  /// </summary>
  private int? ForwardableCellRegister(SchedInstr instr, bool store) {
    if (store ? !instr.MemWrite || instr.MemRead : !instr.MemRead || instr.MemWrite)
      return null;
    if (!this.IsForwardableCell(instr))
      return null;

    if (store && instr.MemBase is Label && this.HasOpcode(instr, 0xA3))
      return 0;                                             // MOV [label],AX accumulator short form
    if (!this.HasOpcode(instr, store ? (byte)0x89 : (byte)0x8B))
      return null;
    var modrm = this._buffer[instr.Start + 1];
    return (modrm & 0xC0) == 0xC0 ? null : (modrm >> 3) & 7;   // mod=11 is register-to-register
  }

  /// <summary>
  /// The 16-bit immediate of <c>MOV WORD PTR [BP+d],imm16</c>, or null for anything else - and null
  /// as well when a pending fixup covers those two bytes, because then they are not the immediate
  /// yet. Immediate forwarding deliberately remains frame-only: a direct-cell store also carries an
  /// address fixup, and separating address fixups from value fixups is unnecessary for O0083.
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

  private bool IsForwardableCell(SchedInstr instr) =>
    IsFrameCell(instr) || instr.MemBase is Label && !this.HasSegmentOverride(instr);

  /// <summary>Only the frame is inherently SS-relative; direct labels qualify only without a segment override.</summary>
  private static bool IsFrameCell(SchedInstr instr) => "BP".Equals(instr.MemBase);

  private bool HasSegmentOverride(SchedInstr instr) => instr.Length > 0 && this._buffer[instr.Start] is
    0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65;

  private bool HasOpcode(SchedInstr instr, byte opcode) =>
    instr.Length is >= 2 and <= 6
    && instr.Start + instr.Length <= this._buffer.Count
    && this._buffer[instr.Start] == opcode;
}
