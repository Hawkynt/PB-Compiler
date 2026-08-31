namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  private bool _enablePeephole;

  /// <summary>
  /// When set, the instruction methods record their boundaries so <see cref="RunPeephole"/>
  /// can rewrite the image before fixups are resolved. Off by default (most callers want the
  /// faithful stream); the optimizer turns it on for the program image. Scheduling implies this
  /// pre-pass: scheduling a non-canonical stream while deliberately retaining removable staging
  /// copies and long zero-tests is strictly worse, and the peephole now keeps scheduler records
  /// exact after every rewrite.
  /// </summary>
  public bool EnablePeephole {
    get => this._enablePeephole || this.EnableSchedule;
    set => this._enablePeephole = value;
  }

  private enum PeepKind : byte { MovRegImm, MovRegReg, MovRegMem, PopReg, CmpRegZero }

  /// <summary>A recorded instruction: its byte range, what it is, and (for register/memory MOVs) the modrm byte to repatch.</summary>
  private readonly record struct PeepInstr(int Start, int Length, PeepKind Kind, Reg Dst, Reg Src, int ModrmPos);

  private List<PeepInstr>? _peepInstrs;
  private bool _peepholeRan;

  private void RecordPeep(PeepKind kind, int start, Reg dst, Reg src = default, int modrmPos = -1) {
    if (!this.EnablePeephole)
      return;

    (this._peepInstrs ??= []).Add(new(start, this.Position - start, kind, dst, src, modrmPos));
  }

  /// <summary>Drops records at or beyond <paramref name="position"/> (inline-asm speculative rollback).</summary>
  private void TrimPeep(int position) => this._peepInstrs?.RemoveAll(record => record.Start >= position);

  /// <summary>
  /// Shrink-only peephole: coalesces <c>mov R, SRC ; mov R2, R</c> into <c>mov R2, SRC</c> when
  /// R dies immediately afterwards (the next instruction overwrites R without reading it) and no
  /// label can branch into the eliminated copy. SRC may be an immediate, register or memory
  /// operand - the load reads its address operands before writing R2, so redirecting the load to
  /// R2 is value-identical even when R2 appears in the address. Whole instructions are removed and
  /// every label/fixup/relocation position past each cut is decremented, so <see cref="ToArray"/>'s
  /// relative resolution stays correct (shrinking only tightens short jumps, never breaks them).
  ///
  /// When scheduling is enabled as well, every accepted rewrite also updates the scheduler's
  /// def/use record before any bytes move. This is not optional bookkeeping: after
  /// <c>MOV AX,src ; MOV DX,AX</c> becomes <c>MOV DX,src</c>, a later consumer depends on DX,
  /// not AX. Leaving the old write-set behind would let the scheduler hoist that consumer ahead of
  /// its producer. CMP-to-TEST likewise shortens its recorded instruction length from three/four
  /// bytes to two so the scheduler still sees an adjacent, non-overlapping stream.
  /// </summary>
  public void RunPeephole() {
    if (!this.EnablePeephole || this._peepholeRan)
      return;

    this._peepholeRan = true;
    if (this._peepInstrs is not { Count: > 0 } recs)
      return;

    recs.Sort((left, right) => left.Start.CompareTo(right.Start));
    var byStart = new Dictionary<int, int>(recs.Count);
    for (var k = 0; k < recs.Count; ++k)
      byStart[recs[k].Start] = k;

    var labelStarts = this.BoundLabelPositions();
    var patches = new List<(int Position, byte Value)>();
    var removals = new List<(int Start, int Length)>();
    var consumedEnd = -1;                              // greedy non-overlap of accepted triples

    for (var i = 0; i < recs.Count; ++i) {
      var a = recs[i];

      // CMP reg, 0 -> TEST reg, reg: flag-identical (both clear CF/OF and set ZF/SF/PF from the
      // value; AF is unused by the conditional jumps), one byte shorter, self-contained.
      if (a.Kind == PeepKind.CmpRegZero) {
        var index = a.Dst.Index();
        patches.Add((a.Start, a.Dst.IsByte() ? (byte)0x84 : (byte)0x85));
        patches.Add((a.Start + 1, (byte)(0xC0 | (index << 3) | index)));
        this.UpdateSchedForPeephole(a.Start, 2);
        if (a.Length > 2)
          removals.Add((a.Start + 2, a.Length - 2));
        continue;
      }

      if (a.Start < consumedEnd)
        continue;
      if (a.Kind is not (PeepKind.MovRegImm or PeepKind.MovRegReg or PeepKind.MovRegMem))
        continue;

      var intermediate = a.Dst;
      if (!byStart.TryGetValue(a.Start + a.Length, out var bi))
        continue;

      var b = recs[bi];
      if (b.Kind != PeepKind.MovRegReg || b.Src != intermediate || b.Dst == intermediate)
        continue;

      var target = b.Dst;
      if (a.Kind == PeepKind.MovRegReg && a.Src == target)
        continue;                                      // would degenerate to mov R2, R2

      if (!byStart.TryGetValue(b.Start + b.Length, out var ci))
        continue;

      var c = recs[ci];
      var killsIntermediate =
           (c.Kind == PeepKind.PopReg && c.Dst == intermediate)
        || (c.Kind == PeepKind.MovRegImm && c.Dst == intermediate)
        || (c.Kind == PeepKind.MovRegReg && c.Dst == intermediate && c.Src != intermediate);
      if (!killsIntermediate)
        continue;                                      // R might still be read - leave the copy

      if (labelStarts.Contains(b.Start))
        continue;                                      // a branch into the copy needs R set by a

      switch (a.Kind) {
        case PeepKind.MovRegImm:
          patches.Add((a.Start, (byte)(0xB8 + target.Index())));
          break;
        case PeepKind.MovRegReg:
          patches.Add((a.ModrmPos, (byte)((this._buffer[a.ModrmPos] & ~0x07) | target.Index())));
          break;
        case PeepKind.MovRegMem:
          patches.Add((a.ModrmPos, (byte)((this._buffer[a.ModrmPos] & ~0x38) | (target.Index() << 3))));
          break;
      }

      this.UpdateSchedForPeephole(a.Start, a.Length, intermediate, target);
      removals.Add((b.Start, b.Length));
      consumedEnd = c.Start;
    }

    if (patches.Count == 0 && removals.Count == 0)
      return;

    foreach (var (position, value) in patches)
      this._buffer[position] = value;

    removals.Sort((left, right) => right.Start.CompareTo(left.Start));   // apply high offsets first
    foreach (var (start, length) in removals)
      this.RemoveBytes(start, length);
  }

  /// <summary>
  /// Keeps the scheduler descriptor of an instruction changed by the peephole consistent with the
  /// bytes it will see. The peephole records only word MOVs, so retargeting changes exactly one
  /// register-def bit; the source/address reads and memory effects are unchanged.
  /// </summary>
  private void UpdateSchedForPeephole(int start, int length, Reg? oldDestination = null, Reg? newDestination = null) {
    if (this._schedInstrs is not { } sched)
      return;
    var index = sched.FindIndex(record => record.Start == start);
    if (index < 0)
      return;

    var record = sched[index];
    var writes = record.Writes;
    if (oldDestination is { } oldReg && newDestination is { } newReg)
      writes = (ushort)((writes & ~RegBit(oldReg)) | RegBit(newReg));
    sched[index] = record with { Length = length, Writes = writes };
  }

  /// <summary>Offsets of every non-constant label bound into the image, including anonymous labels.</summary>
  private HashSet<int> BoundLabelPositions() {
    var positions = new HashSet<int>();
    foreach (var label in this._boundLabels)
      if (label.IsBound && !label.IsConstant)
        positions.Add(label.Position);
    foreach (var label in this._namedLabels.Values)
      if (label.IsBound && !label.IsConstant)
        positions.Add(label.Position);
    foreach (var fixup in this._fixups)
      if (fixup.Target.IsBound && !fixup.Target.IsConstant)
        positions.Add(fixup.Target.Position);

    return positions;
  }

  /// <summary>
  /// Excises <c>[start, start+length)</c> from the image and slides every label, fixup and
  /// segment-relocation position beyond the cut down by <paramref name="length"/>, so all
  /// downstream offsets remain consistent for fixup resolution.
  /// </summary>
  private void RemoveBytes(int start, int length) {
    var end = start + length;
    this._buffer.RemoveRange(start, length);

    var labels = new HashSet<Label>(ReferenceEqualityComparer.Instance);
    foreach (var label in this._boundLabels)     // every bound label, including anonymous ones
      labels.Add(label);
    foreach (var label in this._namedLabels.Values)
      labels.Add(label);
    foreach (var fixup in this._fixups)
      labels.Add(fixup.Target);
    foreach (var label in labels)
      // a constant label's "position" is a value, not an image offset (the frame-size labels hold
      // a byte count) - sliding one would silently resize the frame the cut happens to precede
      if (label.IsBound && !label.IsConstant && label.Position >= end)
        label.Position -= length;

    // drop what the cut swallowed BEFORE sliding: a fixup just past the cut slides onto a
    // position inside [start, end) and a removal pass run afterwards would delete it - leaving
    // a live reference (a string literal's offset, say) unresolved and reading as zero
    this._fixups.RemoveAll(fixup => fixup.Position >= start && fixup.Position < end);
    for (var i = 0; i < this._fixups.Count; ++i)
      if (this._fixups[i].Position >= end)
        this._fixups[i] = this._fixups[i] with { Position = this._fixups[i].Position - length };

    this._segmentRelocations.RemoveAll(position => position >= start && position < end);
    for (var i = 0; i < this._segmentRelocations.Count; ++i)
      if (this._segmentRelocations[i] >= end)
        this._segmentRelocations[i] -= length;

    // the instruction records carry positions as well, and a pass that cuts before another one
    // reads them must leave them describing the buffer they actually sit in
    if (this._schedInstrs is { } sched) {
      sched.RemoveAll(record => record.Start >= start && record.Start < end);
      for (var i = 0; i < sched.Count; ++i)
        if (sched[i].Start >= end)
          sched[i] = sched[i] with { Start = sched[i].Start - length };
    }
    if (this._peepInstrs is { } peep) {
      peep.RemoveAll(record => record.Start >= start && record.Start < end);
      for (var i = 0; i < peep.Count; ++i)
        if (peep[i].Start >= end)
          peep[i] = peep[i] with { Start = peep[i].Start - length, ModrmPos = peep[i].ModrmPos - length };
    }
  }
}
