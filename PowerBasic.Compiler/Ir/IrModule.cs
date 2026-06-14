namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A translation unit: the globals and functions produced from one bound program.
/// This is the root the middle-end optimizes and the backends consume.
/// </summary>
public sealed class IrModule(string name) {

  private readonly List<IrFunction> _functions = [];
  private readonly List<IrGlobalVariable> _globals = [];

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

  /// <summary>Finds a function by name, or null.</summary>
  public IrFunction? FindFunction(string name) => this._functions.FirstOrDefault(f => f.Name == name);
}
