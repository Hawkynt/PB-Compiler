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
  /// <summary>The type of the value held in this global's storage.</summary>
  public IrType ValueType { get; } = valueType;

  /// <summary>True when the global has no initializer and may live in BSS.</summary>
  public bool IsZeroInitialized { get; set; } = true;
}
