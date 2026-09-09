using PowerBasic.Compiler.Ir.Profiling;
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

  /// <summary>
  /// Whether source explicitly promises that all string bytes are 7-bit ASCII. The front-end sets
  /// this from PB36 <c>$OPTION ASCII</c>; middle-end passes may use it to choose implementations that
  /// are only correct under that contract. It is false for ordinary modules and must never be inferred
  /// merely from the selected dialect.
  /// </summary>
  public bool AsciiOnly { get; set; }
  /// Optional execution profile associated with this module. Optimizations must treat a missing entry
  /// exactly like a missing profile and fall back to their static heuristics rather than inventing heat.
  /// </summary>
  public IrProfile? Profile { get; set; }
  /// The optimization objective most recently applied to this module. Late passes outside
  /// <see cref="Passes.IrPassManager.Standard"/> use it to keep size-growing rewrites SPEED-only.
  /// </summary>
  public bool OptimizeForSpeed { get; internal set; }

  public IReadOnlyList<IrFunction> Functions => this._functions;
  public IReadOnlyList<IrGlobalVariable> Globals => this._globals;

  private readonly Dictionary<string, string> _procedureLoweringDeclines = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// The procedures that HAVE a body in the source and whose body the lowering refused, by name, with
  /// the reason. Such a procedure is left as a declaration - callers can still call it - so it
  /// vanishes from <see cref="Functions"/>'s defined half and appears in no selection or allocation
  /// census. Recording it is what stops a per-procedure lowering failure from reading as one fewer
  /// function to cover rather than as one function not covered.
  /// </summary>
  public IReadOnlyDictionary<string, string> ProcedureLoweringDeclines => this._procedureLoweringDeclines;

  internal void RecordProcedureLoweringDecline(string name, string reason)
    => this._procedureLoweringDeclines[name] = reason;

  public IrFunction AddFunction(IrFunction function) {
    function.Module = this;
    this._functions.Add(function);
    return function;
  }

  public IrGlobalVariable AddGlobal(IrGlobalVariable global) {
    this._globals.Add(global);
    this.ReserveStringOrdinal(global.Name);
    return global;
  }

  /// <summary>Removes a function from the module (global dead-code elimination); returns whether it was present.</summary>
  public bool RemoveFunction(IrFunction function) {
    if (!this._functions.Remove(function))
      return false;
    function.Module = null;
    return true;
  }

  /// <summary>Removes a global variable from the module (global dead-code elimination); returns whether it was present.</summary>
  public bool RemoveGlobal(IrGlobalVariable global) => this._globals.Remove(global);

  /// <summary>Adds (or reuses) a private byte-array constant for a string literal; identical literals are interned to one global.</summary>
  public IrGlobalVariable AddStringConstant(byte[] bytes) {
    var key = Convert.ToBase64String(bytes);
    if (this._internedStrings.TryGetValue(key, out var existing))
      return existing;
    var global = new IrGlobalVariable($".str{this._stringOrdinal++}", IrType.I8) { Bytes = bytes, IsZeroInitialized = false };
    this.AddGlobal(global);
    this._internedStrings[key] = global;
    return global;
  }

  /// <summary>Finds a function by name, or null.</summary>
  public IrFunction? FindFunction(string name) => this._functions.FirstOrDefault(f => f.Name == name);

  /// <summary>Finds a global variable by name, or null.</summary>
  public IrGlobalVariable? FindGlobal(string name) => this._globals.FirstOrDefault(g => g.Name == name);

  /// <summary>
  /// Imported/cloned IR may already contain generated <c>.strN</c> globals. Keep the allocator beyond
  /// the largest imported ordinal so a later string pass cannot mint a duplicate symbol name.
  /// Deliberately do not add imported bytes to <see cref="_internedStrings"/>: an arbitrary global
  /// named like a literal is not proof that its storage is immutable and safe to coalesce.
  /// </summary>
  private void ReserveStringOrdinal(string globalName) {
    const string prefix = ".str";
    if (!globalName.StartsWith(prefix, StringComparison.Ordinal)
        || !int.TryParse(globalName.AsSpan(prefix.Length), out var ordinal)
        || ordinal < 0
        || ordinal == int.MaxValue)
      return;
    this._stringOrdinal = Math.Max(this._stringOrdinal, ordinal + 1);
  }
}
