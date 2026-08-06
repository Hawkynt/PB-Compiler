using PowerBasic.Compiler.Syntax;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A translation unit: the globals and functions produced from one bound program.
/// This is the root the middle-end optimizes and the backends consume.
/// </summary>
public sealed class IrModule(string name, Dialect dialect = Dialect.Pb35, Dialect? compatDialect = null) {

  private readonly List<IrFunction> _functions = [];
  private readonly List<IrGlobalVariable> _globals = [];
  private readonly Dictionary<string, IrGlobalVariable> _internedStrings = new(StringComparer.Ordinal);

  /// <summary>Monotonic, so a literal's name can never collide with one whose global was removed.</summary>
  private int _stringOrdinal;

  /// <summary>A name for the module (typically the source file).</summary>
  public string Name { get; } = name;

  /// <summary>The dialect whose source surface produced this module.</summary>
  public Dialect Dialect { get; } = dialect;

  /// <summary>
  /// The dialect whose observable runtime rules the module requires. This differs from
  /// <see cref="Dialect"/> when a pb35 source carries a <c>$COMPAT</c> directive.
  /// </summary>
  public Dialect EffectiveDialect { get; } = compatDialect ?? dialect;

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

  /// <summary>Removes a function from the module (global dead-code elimination); returns whether it was present.</summary>
  public bool RemoveFunction(IrFunction function) => this._functions.Remove(function);

  /// <summary>Removes a global variable from the module (global dead-code elimination); returns whether it was present.</summary>
  public bool RemoveGlobal(IrGlobalVariable global) => this._globals.Remove(global);

  /// <summary>Adds (or reuses) a private byte-array constant for a string literal; identical literals are interned to one global.</summary>
  public IrGlobalVariable AddStringConstant(byte[] bytes) {
    var key = Convert.ToBase64String(bytes);
    if (this._internedStrings.TryGetValue(key, out var existing))
      return existing;
    var global = new IrGlobalVariable($".str{this._stringOrdinal++}", IrType.I8) { Bytes = bytes, IsZeroInitialized = false };
    this._globals.Add(global);
    this._internedStrings[key] = global;
    return global;
  }

  /// <summary>Finds a function by name, or null.</summary>
  public IrFunction? FindFunction(string name) => this._functions.FirstOrDefault(f => f.Name == name);

  /// <summary>Finds a global variable by name, or null.</summary>
  public IrGlobalVariable? FindGlobal(string name) => this._globals.FirstOrDefault(g => g.Name == name);
}
