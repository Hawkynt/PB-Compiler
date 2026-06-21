using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Semantics;

/// <summary>Where a variable lives on the target.</summary>
public enum VariableStorage {
  /// <summary>Module-level data segment (module code and, when SHARED, procedures see it).</summary>
  Global,
  /// <summary>Procedure stack frame ([BP-n]).</summary>
  Local,
  /// <summary>Procedure parameter ([BP+n]); BYREF parameters hold a pointer.</summary>
  Parameter,
  /// <summary>STATIC procedure variable - data segment slot private to the procedure.</summary>
  Static,
  /// <summary>PB 3.6 captured variable inside a lambda: reached by double-indirection through the closure's environment pointer; <see cref="VariableSymbol.Offset"/> is the env-record index.</summary>
  Captured,
}

/// <summary>A bound variable (scalar or array).</summary>
public sealed class VariableSymbol(string name, PbType type, VariableStorage storage) {
  public string Name { get; } = name;
  public PbType Type { get; set; } = type;
  public VariableStorage Storage { get; } = storage;
  /// <summary>True when visible inside procedures (SHARED / DIM ... AS SHARED / COMMON SHARED).</summary>
  public bool IsShared { get; set; }
  /// <summary>Parameters only: passed by value (default in PB is by reference).</summary>
  public bool ByVal { get; set; }
  /// <summary>Parameters only: far pointer passed (SEG modifier).</summary>
  public bool Seg { get; set; }
  /// <summary>Parameters only: CDECL bracket parameter (<c>[, BYVAL x]</c>) - may be omitted at call sites.</summary>
  public bool Optional { get; set; }
  /// <summary>Parameters only (PB 3.6): the default-value expression a call site uses when the (trailing) argument is omitted.</summary>
  public Expression? DefaultValue { get; set; }
  /// <summary>Assigned by the storage layouter: data-segment offset or BP displacement.</summary>
  public int Offset { get; set; }
  /// <summary>PB 3.6: this enclosing-frame local is captured by a lambda closure (its address escapes into the closure environment), so the optimizer must keep it in memory - no register residency, constant folding, or dead-store elimination of its writes.</summary>
  public bool IsCaptured { get; set; }
  /// <summary>PB 3.6 escaping-closure capture (Storage == Captured): byte offset of this capture's slot within the lambda's HEAP environment record. Unused for stack closures, where the capture is read at its enclosing-frame displacement instead.</summary>
  public int EnvSlotOffset { get; set; }

  /// <summary>Allocation class from DIM (HUGE/VIRTUAL/ABSOLUTE are diagnosed by codegen for now).</summary>
  public ArrayClass ArrayClass { get; set; } = ArrayClass.Default;

  public bool IsArray => this.Type is ArrayType;
  public override string ToString() => $"{this.Storage} {this.Name}: {this.Type}";
}

/// <summary>A SUB or FUNCTION (defined here, DECLAREd, or imported from a unit).</summary>
public sealed class ProcedureSymbol(string name, bool isFunction) {
  public string Name { get; } = name;
  public bool IsFunction { get; } = isFunction;
  public PbType? ReturnType { get; set; }
  /// <summary>
  /// For a lifted TYPE member (FUNCTION / PROPERTY GET), the simple member name the
  /// body assigns to as its result (e.g. <c>Pop</c> when <see cref="Name"/> is the
  /// mangled <c>Stack.Pop</c>); null for ordinary procedures, where the result is the name.
  /// </summary>
  public string? ResultName { get; set; }
  public List<VariableSymbol> Parameters { get; } = [];
  /// <summary>Locals & statics by name (case-insensitive), including the implicit function-result variable.</summary>
  public Dictionary<string, VariableSymbol> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
  public bool IsStatic { get; set; }
  /// <summary>
  /// Calling convention. BASIC (default): arguments left to right, BYREF, callee
  /// cleans (RET n). CDECL: right to left, caller cleans. STDCALL: right to left,
  /// callee cleans. PASCAL: left to right, callee cleans.
  /// </summary>
  public CallConvention CallConv { get; set; } = CallConvention.Basic;
  /// <summary>CDECL calling convention: arguments pushed right to left, caller cleans the stack.</summary>
  public bool IsCdecl => this.CallConv == CallConvention.Cdecl;
  /// <summary>ALIAS clause: the external (link) symbol this procedure resolves to, when it differs from <see cref="Name"/> (e.g. a C public "_foo"). Null = link by name.</summary>
  public string? Alias { get; set; }
  /// <summary>
  /// Position of this overload within its same-name set (PB 3.6 overloading). 0 for
  /// the first/only one, so a non-overloaded procedure keeps its plain emitted label.
  /// </summary>
  public int OverloadIndex { get; set; }
  /// <summary>PB 3.6: true for a nested local SUB/FUNCTION lifted to top level (bound in a separate capture phase).</summary>
  public bool IsNested { get; set; }
  /// <summary>PB 3.6 capturing lambda: the outer locals it captures, in environment-record order (the enclosing proc fills the record at closure creation). Empty for a non-capturing lambda.</summary>
  public List<VariableSymbol> Captures { get; } = [];
  /// <summary>PB 3.6 capturing lambda: the hidden env-record local allocated in the ENCLOSING procedure (an array of far pointers to the captured locals). Null when the lambda captures nothing.</summary>
  public VariableSymbol? ClosureEnvRecord { get; set; }
  /// <summary>PB 3.6 capturing lambda: the hidden local in THIS lambda that holds the far environment pointer (saved from the closure value at entry). Non-null marks a capturing lambda.</summary>
  public VariableSymbol? ClosureEnvPtr { get; set; }
  /// <summary>
  /// PB 3.6 ESCAPING capturing lambda: the closure value can outlive the enclosing
  /// frame (returned as the enclosing FUNCTION's result, or stored in a
  /// SHARED/GLOBAL/STATIC). Its environment is then a HEAP block holding a by-value
  /// snapshot of the captured locals taken at closure creation - so each
  /// <see cref="Captures"/> entry's <see cref="VariableSymbol.Offset"/> doubles as
  /// the byte offset of its slot within that heap env record. Non-escaping capturing
  /// lambdas keep the stage-1 stack env (env = enclosing frame, captures read at
  /// frame displacements, by reference).
  /// </summary>
  public bool IsEscapingClosure { get; set; }
  /// <summary>PB 3.6 escaping capturing lambda: total byte size of the heap env record (sum of the captured locals' slot sizes); 0 for a stack closure.</summary>
  public int ClosureEnvSize { get; set; }
  /// <summary>Null when only DECLAREd (external - resolved at link time from PBU/PBL).</summary>
  public IReadOnlyList<Statement>? Body { get; set; }
  public SourcePosition Position { get; set; }

  public bool IsExternal => this.Body == null;
  /// <summary>Number of parameters a call site must provide (CDECL bracket parameters may be omitted).</summary>
  public int RequiredParameters => this.Parameters.Count(p => !p.Optional);
  public override string ToString() => $"{(this.IsFunction ? "FUNCTION" : "SUB")} {this.Name}({this.Parameters.Count})";
}

/// <summary>A compile-time diagnostic.</summary>
public sealed record Diagnostic(SourcePosition Position, string Message) {
  public override string ToString() => $"{this.Position}: {this.Message}";
}

/// <summary>Raised when binding encounters an unrecoverable inconsistency.</summary>
public sealed class BindException(string message, SourcePosition position) : Exception($"{position}: {message}") {
  public SourcePosition Position { get; } = position;
}
