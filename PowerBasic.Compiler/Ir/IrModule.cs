namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A translation unit: the globals and functions produced from one bound program.
/// This is the root the middle-end optimizes and the backends consume.
/// </summary>
public sealed class IrModule(string name) {

  private readonly List<IrFunction> _functions = [];
  private readonly List<IrGlobalVariable> _globals = [];
  private readonly Dictionary<string, IrGlobalVariable> _internedStrings = new(StringComparer.Ordinal);

  /// <summary>A name for the module (typically the source file).</summary>
  public string Name { get; } = name;

  public IReadOnlyList<IrFunction> Functions => this._functions;
  public IReadOnlyList<IrGlobalVariable> Globals => this._globals;

  public IrFunction AddFunction(IrFunction function) {
    this._functions.Add(function);
    return function;
  }

  public IrGlobalVariable AddGlobal(IrGlobalVariable global) {
    this._globals.Add(global);
    return global;
  }

  /// <summary>Adds (or reuses) a private byte-array constant for a string literal; identical literals are interned to one global.</summary>
  public IrGlobalVariable AddStringConstant(byte[] bytes) {
    var key = Convert.ToBase64String(bytes);
    if (this._internedStrings.TryGetValue(key, out var existing))
      return existing;
    var global = new IrGlobalVariable($".str{this._globals.Count}", IrType.I8) { Bytes = bytes, IsZeroInitialized = false };
    this._globals.Add(global);
    this._internedStrings[key] = global;
    return global;
  }

  /// <summary>Finds a function by name, or null.</summary>
  public IrFunction? FindFunction(string name) => this._functions.FirstOrDefault(f => f.Name == name);
}
