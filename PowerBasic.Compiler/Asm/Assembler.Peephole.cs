namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// When set, the instruction methods record their boundaries so <see cref="RunPeephole"/>
  /// can rewrite the image before fixups are resolved. Off by default (most callers want the
  /// faithful stream); the optimizer turns it on for the program image.
  /// </summary>
  public bool EnablePeephole { get; set; }

  private enum PeepKind : byte { MovRegImm, MovRegReg, MovRegMem, PopReg }

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
  /// Idempotent and a no-op unless <see cref="EnablePeephole"/> recorded the stream.
  /// </summary>
  public void RunPeephole() {
    if (!this.EnablePeephole || this._peepholeRan)
      return;

    this._peepholeRan = true;
    if (this._peepInstrs is not { Count: > 1 } recs)
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

      removals.Add((b.Start, b.Length));
      consumedEnd = c.Start;
    }

    if (removals.Count == 0)
      return;

    foreach (var (position, value) in patches)
      this._buffer[position] = value;

    removals.Sort((left, right) => right.Start.CompareTo(left.Start));   // apply high offsets first
    foreach (var (start, length) in removals)
      this.RemoveBytes(start, length);
  }

  /// <summary>Offsets every bound label is sitting on (named labels and every fixup target).</summary>
  private HashSet<int> BoundLabelPositions() {
    var positions = new HashSet<int>();
    foreach (var label in this._namedLabels.Values)
      if (label.IsBound)
        positions.Add(label.Position);
    foreach (var fixup in this._fixups)
      if (fixup.Target.IsBound)
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
    foreach (var label in this._namedLabels.Values)
      labels.Add(label);
    foreach (var fixup in this._fixups)
      labels.Add(fixup.Target);
    foreach (var label in labels)
      if (label.IsBound && label.Position >= end)
        label.Position -= length;

    for (var i = 0; i < this._fixups.Count; ++i)
      if (this._fixups[i].Position >= end)
        this._fixups[i] = this._fixups[i] with { Position = this._fixups[i].Position - length };
    this._fixups.RemoveAll(fixup => fixup.Position >= start && fixup.Position < end);

    for (var i = 0; i < this._segmentRelocations.Count; ++i)
      if (this._segmentRelocations[i] >= end)
        this._segmentRelocations[i] -= length;
    this._segmentRelocations.RemoveAll(position => position >= start && position < end);
  }
}
