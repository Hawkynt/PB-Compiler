namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A symbol with a fixed address: a global variable or a function. Its IR value is
/// always a pointer (<see cref="IrType.Ptr"/>) — the address of the symbol — while
/// the type stored at that address travels separately.
/// </summary>
public abstract class IrGlobalValue : IrValue {
  protected IrGlobalValue(string name) : base(IrType.Ptr) => this.Name = name;

  /// <summary>The symbol name (never null for a global value).</summary>
  public new string Name {
    get => base.Name!;
    set => base.Name = value;
  }
}

/// <summary>A module-level variable. <see cref="ValueType"/> is the type stored at its address.</summary>
public sealed class IrGlobalVariable(string name, IrType valueType) : IrGlobalValue(name) {
  /// <summary>The type of the value held in this global's storage (the element type for an array/blob).</summary>
  public IrType ValueType { get; } = valueType;

  /// <summary>True when the global has no initializer and may live in BSS.</summary>
  public bool IsZeroInitialized { get; set; } = true;

  /// <summary>Constant initializer bytes (a string literal / DATA blob), or null for typed storage.</summary>
  public byte[]? Bytes { get; init; }

  /// <summary>
  /// Target-independent floating initializer values. These are kept typed rather than serialized into
  /// <see cref="Bytes"/> so LLVM/C emission does not accidentally inherit the compiler host's byte
  /// order. The x86-16 backend serializes them only at its target-specific data boundary.
  /// </summary>
  public double[]? FloatingValues { get; init; }

  /// <summary>Element count: greater than one for an array global (a module-level <c>DIM</c>).</summary>
  public int Count { get; init; } = 1;

  /// <summary>Whether this global carries an explicit constant initializer rather than BSS storage.</summary>
  public bool HasConstantInitializer => this.Bytes is not null || this.FloatingValues is not null;
}
