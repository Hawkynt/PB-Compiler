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
  /// <summary>Assigned by the storage layouter: data-segment offset or BP displacement.</summary>
  public int Offset { get; set; }

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
  public List<VariableSymbol> Parameters { get; } = [];
  /// <summary>Locals & statics by name (case-insensitive), including the implicit function-result variable.</summary>
  public Dictionary<string, VariableSymbol> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
  public bool IsStatic { get; set; }
  /// <summary>CDECL calling convention: arguments pushed right to left, caller cleans the stack.</summary>
  public bool IsCdecl { get; set; }
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
