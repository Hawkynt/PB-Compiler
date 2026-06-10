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

  public override string ToString() => this.Name ?? $"L{this.GetHashCode():X4}";
}
