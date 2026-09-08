namespace PowerBasic.Compiler.Asm;

public sealed partial class Assembler {

  /// <summary>
  /// Produces the ordinary linker image plus the internal control-transfer records and complete bound
  /// label map O0360 needs to move already-assembled basic blocks without rediscovering x86 control
  /// flow from bytes.
  /// </summary>
  /// <remarks>
  /// This deliberately runs the same late assembler pipeline as <see cref="ToRelocatable"/> first.
  /// The records therefore describe the bytes that are actually stored in the PBU: if an earlier
  /// relaxation already shortened a branch, its record says so. A post-link rewriter can expand it
  /// back to a canonical near form before choosing a new layout, then relax again afterwards.
  /// </remarks>
  public RelocatableImage ToPostLinkRelocatable() {
    this.RunLoadForwarding();
    this.RunSchedule();
    this.RunPeephole();
    this.RunJumpThreading();
    this.RunTailMerge();
    this.RunJumpRelaxation();

    var result = this._buffer.ToArray();
    var relocations = new List<AsmRelocation>();
    var relativeFixups = new List<AsmRelativeFixup>();
    var registered = new HashSet<Label>(this._namedLabels.Values, ReferenceEqualityComparer.Instance);

    foreach (var fixup in this._fixups) {
      if (fixup.Target.IsExternal || (!fixup.Target.IsBound && registered.Contains(fixup.Target))) {
        switch (fixup.Kind) {
          case FixupKind.Rel16:
          case FixupKind.Rel16Pair:
            relocations.Add(new(fixup.Position, AsmRelocationKind.ExternalRelative, fixup.Target.Name));
            break;
          case FixupKind.Abs16:
            result[fixup.Position] = (byte)fixup.Addend;
            result[fixup.Position + 1] = (byte)(fixup.Addend >> 8);
            relocations.Add(new(fixup.Position, AsmRelocationKind.ExternalAbsolute, fixup.Target.Name));
            break;
          default:
            throw new InvalidOperationException($"Short jump to external label {fixup.Target} is not linkable.");
        }
        continue;
      }

      if (!fixup.Target.IsBound)
        throw new InvalidOperationException($"Label {fixup.Target} was referenced but never bound.");

      if (this.DescribeRelativeFixup(fixup) is { } relative)
        relativeFixups.Add(relative);

      ApplyFixup(result, fixup);
      if (fixup.Kind == FixupKind.Abs16 && !fixup.Target.IsConstant)
        relocations.Add(new(fixup.Position, AsmRelocationKind.Absolute, null));
    }

    foreach (var site in this._segmentRelocations)
      relocations.Add(new(site, AsmRelocationKind.Segment, null));

    var bound = this._namedLabels
      .Where(pair => pair.Value.IsBound)
      .ToDictionary(pair => pair.Key, pair => pair.Value.Position, StringComparer.OrdinalIgnoreCase);
    var allBound = this._boundLabels
      .Where(label => label.IsBound && !label.IsConstant)
      .Select(label => new AsmBoundLabel(label.Name, label.Position))
      .ToArray();

    return new(result, relocations, bound) {
      RelativeFixups = relativeFixups,
      AllBoundLabels = allBound,
    };
  }

  private AsmRelativeFixup? DescribeRelativeFixup(Fixup fixup) {
    var target = checked(fixup.Target.Position + fixup.Addend);
    switch (fixup.Kind) {
      case FixupKind.Rel8 when fixup.Position >= 1: {
        var opcode = this._buffer[fixup.Position - 1];
        if (opcode == 0xEB)
          return new(fixup.Position - 1, 2, AsmRelativeFixupKind.Jump, 0, target);
        if (opcode is >= 0x70 and <= 0x7F)
          return new(fixup.Position - 1, 2, AsmRelativeFixupKind.Conditional,
            (byte)(opcode & 0x0F), target);
        return null;
      }

      case FixupKind.Rel16 when fixup.Position >= 1: {
        var opcode = this._buffer[fixup.Position - 1];
        if (opcode == 0xE8)
          return new(fixup.Position - 1, 3, AsmRelativeFixupKind.Call, 0, target);
        if (opcode == 0xE9)
          return new(fixup.Position - 1, 3, AsmRelativeFixupKind.Jump, 0, target);
        if (fixup.Position >= 2 && this._buffer[fixup.Position - 2] == 0x0F
            && opcode is >= 0x80 and <= 0x8F)
          return new(fixup.Position - 2, 4, AsmRelativeFixupKind.Conditional,
            (byte)(opcode & 0x0F), target);
        return null;
      }

      case FixupKind.Rel16Pair when fixup.Position >= 3: {
        var start = fixup.Position - 3;
        var inverse = this._buffer[start];
        if (inverse is not (>= 0x70 and <= 0x7F)
            || this._buffer[start + 1] != 0x03 || this._buffer[start + 2] != 0xE9)
          throw new InvalidOperationException("rel16 conditional-pair fixup does not describe an 8086 Jcc/JMP pair");
        return new(start, 5, AsmRelativeFixupKind.Conditional,
          (byte)((inverse & 0x0F) ^ 1), target);
      }

      default:
        return null;
    }
  }
}
