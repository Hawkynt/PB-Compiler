using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Hir;

/// <summary>A procedure-sized high-level function before control flow is lowered to blocks.</summary>
public sealed class HirFunction {

  internal HirFunction(ProcedureSymbol? procedure, IReadOnlyList<HirStatement> body) {
    this.Procedure = procedure;
    this.Body = body;
  }

  /// <summary>The bound procedure represented by this function; null only for the program entry.</summary>
  public ProcedureSymbol? Procedure { get; }

  /// <summary>The ordered, executable statement body. External declarations have an empty body.</summary>
  public IReadOnlyList<HirStatement> Body { get; }

  public bool IsEntryPoint => this.Procedure is null;
  public bool IsExternal => this.Procedure?.IsExternal ?? false;
  public string Name => this.Procedure?.Name ?? "main";
}

/// <summary>One high-level executable operation.</summary>
public abstract record HirStatement {
  public abstract SourcePosition Position { get; }
  internal abstract Statement BoundSource { get; }
}

/// <summary>Unmigrated bound statement retained as a compatibility operation during incremental conversion.</summary>
public sealed record HirBoundStatement(Statement Source) : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>Program termination, kept explicit until control-flow formation.</summary>
public sealed record HirEndStatement(EndStmt Source) : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>Assignment with its target and value kept together as one high-level write operation.</summary>
public sealed record HirAssignmentStatement(AssignStmt Source) : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>A direct procedure call with parameter binding already resolved by the binder.</summary>
public sealed record HirDirectCallStatement(CallStmt Source, HirDirectCall Call) : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>Dynamic-array resizing with identity, bounds and PRESERVE intent resolved in HIR.</summary>
public sealed record HirArrayResizeStatement(RedimStmt Source, IReadOnlyList<HirArrayResize> Operations)
  : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>Dynamic-array release with each target identity resolved in HIR.</summary>
public sealed record HirArrayEraseStatement(EraseStmt Source, IReadOnlyList<HirArrayErase> Operations)
  : HirStatement {
  public override SourcePosition Position => this.Source.Position;
  internal override Statement BoundSource => this.Source;
}

/// <summary>
/// High-level executable units produced from a bound program. This first HIR boundary keeps the
/// bound statement nodes and semantic side tables intact while giving lowering a canonical function
/// inventory independent of the binder's several procedure lookup collections.
/// </summary>
public sealed class HirModule {

  internal HirModule(SemanticModel boundModel, IReadOnlyList<HirFunction> functions) {
    this.BoundModel = boundModel;
    this.Functions = functions;
  }

  /// <summary>Binding facts still needed by operation lowering.</summary>
  public SemanticModel BoundModel { get; }

  /// <summary>The entry point followed by every unique procedure, including external declarations.</summary>
  public IReadOnlyList<HirFunction> Functions { get; }

  public HirFunction EntryPoint => this.Functions[0];
}
