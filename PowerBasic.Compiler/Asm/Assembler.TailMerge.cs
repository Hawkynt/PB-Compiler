namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// S3 link-time tail merge (identical-code folding): when set, procedure regions recorded via
  /// <see cref="BeginFoldRegion"/>/<see cref="EndFoldRegion"/> with byte- and fixup-identical
  /// content are folded to one copy - every duplicate's entry label is re-bound to the survivor
  /// and its bytes removed. Off by default; the optimizer turns it on under $OPTIMIZE SIZE.
  /// </summary>
  public bool EnableTailMerge { get; set; }

  private readonly List<(Label Entry, Label Start, Label End)> _foldRegions = [];
  private bool _tailMergeRan;

  /// <summary>Opens a foldable region at the current position; <paramref name="entry"/> is the region's only externally-referenced label.</summary>
  public void BeginFoldRegion(Label entry) {
    if (!this.EnableTailMerge)
      return;
    var start = this.DefineLabel();
    this.MarkLabel(start);
    this._foldRegions.Add((entry, start, this.DefineLabel()));
  }

  /// <summary>Closes the most recently opened foldable region.</summary>
  public void EndFoldRegion() {
    if (!this.EnableTailMerge || this._foldRegions.Count == 0)
      return;
    var last = this._foldRegions[^1];
    if (!last.End.IsBound)
      this.MarkLabel(last.End);
  }

  /// <summary>
  /// Folds identical regions. Two regions are congruent when their raw bytes match (fixup
  /// placeholder bytes are zeros pre-resolution, so raw comparison is exact) AND their fixups
  /// match position-for-position - internal targets normalized to region-relative offsets,
  /// external targets compared by label identity. A duplicate folds only when nothing outside
  /// it references any label bound inside except the entry label. Runs after the peephole /
  /// jump threading (regions tracked by labels, so earlier cuts shifted them correctly) and
  /// before short-jump relaxation (the shorter image helps more jumps fit).
  /// </summary>
  public void RunTailMerge() {
    if (!this.EnableTailMerge || this._tailMergeRan)
      return;
    this._tailMergeRan = true;

    var regions = this._foldRegions
      .Where(r => r.Start.IsBound && r.End.IsBound && r.End.Position > r.Start.Position)
      .ToList();
    if (regions.Count < 2)
      return;

    var externalIds = new Dictionary<Label, int>(ReferenceEqualityComparer.Instance);
    string SignatureOf((Label Entry, Label Start, Label End) r) {
      var start = r.Start.Position;
      var end = r.End.Position;
      var sb = new System.Text.StringBuilder();
      sb.Append(end - start).Append(':');
      sb.Append(Convert.ToHexString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(this._buffer).Slice(start, end - start)));
      foreach (var f in this._fixups.Where(f => f.Position >= start && f.Position < end).OrderBy(f => f.Position)) {
        var isInternal = f.Target.IsBound && f.Target.Position >= start && f.Target.Position < end;
        sb.Append('|').Append(f.Position - start).Append(',').Append((int)f.Kind).Append(',');
        if (isInternal)
          sb.Append("i:").Append(f.Target.Position - start);
        else
          sb.Append("x:").Append(externalIds.TryGetValue(f.Target, out var id) ? id : externalIds[f.Target] = externalIds.Count);
        sb.Append(',').Append(f.Addend);
      }
      return sb.ToString();
    }

    bool OnlyEntryReferencedFromOutside((Label Entry, Label Start, Label End) r) {
      var start = r.Start.Position;
      var end = r.End.Position;
      foreach (var f in this._fixups) {
        if (f.Position >= start && f.Position < end)
          continue;   // internal reference - removed with the region
        if (f.Target.IsBound && f.Target.Position >= start && f.Target.Position < end && !ReferenceEquals(f.Target, r.Entry))
          return false;
      }
      return true;
    }

    var groups = new Dictionary<string, (Label Entry, Label Start, Label End)>(StringComparer.Ordinal);
    var folds = new List<((Label Entry, Label Start, Label End) Dup, Label Survivor)>();
    foreach (var region in regions.OrderBy(r => r.Start.Position)) {
      var signature = SignatureOf(region);
      if (groups.TryGetValue(signature, out var survivor)) {
        if (OnlyEntryReferencedFromOutside(region))
          folds.Add((region, survivor.Start));
      } else
        groups[signature] = region;
    }

    // apply highest-address first so earlier survivors/duplicates keep valid positions
    foreach (var (dup, survivorStart) in folds.OrderByDescending(f => f.Dup.Start.Position)) {
      var start = dup.Start.Position;
      var length = dup.End.Position - start;
      // Folding moves the entry label onto the survivor, which sits BELOW the region being cut, so
      // every jump to it grows. A short displacement that no longer reaches would be reported as an
      // out-of-range fixup at ToArray, and nothing here can widen it after the fact - so a fold that
      // would strand one is skipped and the duplicate simply stays.
      if (!ShortJumpsStillReach())
        continue;
      this.RemoveBytes(start, length);
      dup.Entry.Position = survivorStart.Position;   // survivors sit below every removed duplicate

      bool ShortJumpsStillReach() {
        var end = start + length;
        var target = survivorStart.Position;         // below the cut, so the cut does not move it
        foreach (var f in this._fixups) {
          if (f.Kind != FixupKind.Rel8 || !ReferenceEquals(f.Target, dup.Entry))
            continue;
          if (f.Position >= start && f.Position < end)
            continue;                                // removed along with the region
          var position = f.Position >= end ? f.Position - length : f.Position;
          if (target + f.Addend - (position + 1) is < sbyte.MinValue or > sbyte.MaxValue)
            return false;
        }
        return true;
      }
    }
  }
}
