namespace PowerBasic.Compiler.Ir;

/// <summary>A formal parameter of an <see cref="IrFunction"/>, usable as an operand inside its body.</summary>
public sealed class IrArgument : IrValue {

  public IrArgument(IrType type, int index, string? name = null) : base(type) {
    this.Index = index;
    this.Name = name;
  }

  /// <summary>The zero-based position of this argument in its function's signature.</summary>
  public int Index { get; }

  /// <summary>The function this argument belongs to (set when the function is created).</summary>
  public IrFunction? Parent { get; internal set; }
}
