namespace PowerBasic.Compiler.Asm;

/// <summary>
/// A position inside the code buffer that may be referenced before it is
/// bound via <see cref="Assembler.MarkLabel(Label)"/>.
/// </summary>
public sealed class Label {

  internal Label(string? name) => this.Name = name;

  public string? Name { get; }

  /// <summary>Offset within the image, or -1 while unbound.</summary>
  public int Position { get; internal set; } = -1;

  public bool IsBound => this.Position >= 0;

  /// <summary>True for symbols resolved by the linker (<see cref="Assembler.External"/>); never bindable.</summary>
  public bool IsExternal { get; internal set; }

  /// <summary>
  /// True for pseudo-labels whose <see cref="Position"/> is assigned manually
  /// as a plain constant (e.g. frame sizes), not an image offset - their fixup
  /// sites must never be rebased by a linker.
  /// </summary>
  public bool IsConstant { get; internal set; }

  public override string ToString() => this.Name ?? $"L{this.GetHashCode():X4}";
}
