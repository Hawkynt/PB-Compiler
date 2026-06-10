namespace PowerBasic.Compiler.Asm;

/// <summary>
/// An immediate operand: a constant, the offset of a <see cref="Label"/>
/// (patched once the label is bound), or a segment value fixed up by the
/// DOS loader (recorded as an MZ relocation).
/// </summary>
public readonly struct Imm {

  private Imm(int value, Label? label, bool isSegmentReference) {
    this.Value = value;
    this.Label = label;
    this.IsSegmentReference = isSegmentReference;
  }

  public int Value { get; }
  public Label? Label { get; }
  public bool IsSegmentReference { get; }

  public static implicit operator Imm(int value) => new(value, null, false);

  /// <summary>The 16-bit offset of <paramref name="label"/> within the image.</summary>
  public static Imm OffsetOf(Label label, int addend = 0) => new(addend, label ?? throw new ArgumentNullException(nameof(label)), false);

  /// <summary>A paragraph (segment) value relocated by the DOS loader at load time.</summary>
  public static Imm Segment(int paragraph = 0) => new(paragraph, null, true);

  public override string ToString() => this.Label is { } l ? $"offset {l}" : this.IsSegmentReference ? $"seg {this.Value}" : this.Value.ToString();
}
