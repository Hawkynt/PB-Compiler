using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>
/// A basic block: a maximal straight-line run of statements ending in at most
/// one branch. <see cref="Condition"/> non-null means a two-way branch
/// (<see cref="TrueSucc"/>/<see cref="FalseSucc"/>); a single successor in
/// <see cref="TrueSucc"/> with no <see cref="Condition"/> is an unconditional
/// edge; no successors means the block leaves the region (EXIT/END/fall-off).
/// </summary>
public sealed class BasicBlock {
  public BasicBlock(int id) => this.Id = id;

  public int Id { get; }
  public List<Statement> Statements { get; } = [];
  public Expression? Condition { get; set; }
  public BasicBlock? TrueSucc { get; set; }
  public BasicBlock? FalseSucc { get; set; }
  public List<BasicBlock> Predecessors { get; } = [];

  public IEnumerable<BasicBlock> Successors {
    get {
      if (this.TrueSucc != null)
        yield return this.TrueSucc;
      if (this.FalseSucc != null)
        yield return this.FalseSucc;
    }
  }

  public override string ToString() => $"B{this.Id}";
}

/// <summary>
/// A control-flow graph over a structured, acyclic region of a bound AST
/// (straight-line statements and IF/ELSEIF/ELSE, with EXIT SUB/FUNCTION/DEF and
/// END as region exits). The builder returns <c>null</c> for any body it cannot
/// model precisely - loops, SELECT, GOTO/labels, GOSUB, ON ERROR, computed
/// flow, inline asm - so every CFG it does produce is sound. This is the
/// foundation for the dominator/SSA/SCCP passes (docs/PB36.md mid-end).
/// </summary>
public sealed class ControlFlowGraph {
  private ControlFlowGraph(BasicBlock entry, BasicBlock exit, IReadOnlyList<BasicBlock> blocks) {
    this.Entry = entry;
    this.Exit = exit;
    this.Blocks = blocks;
  }

  /// <summary>The unique entry block (no predecessors).</summary>
  public BasicBlock Entry { get; }

  /// <summary>The unique synthetic exit block (all region exits flow here; carries no statements).</summary>
  public BasicBlock Exit { get; }

  /// <summary>All blocks, Entry first, in creation order.</summary>
  public IReadOnlyList<BasicBlock> Blocks { get; }

  /// <summary>
  /// Builds a CFG for <paramref name="body"/>, or returns null when the body
  /// uses control flow the structured builder does not model (so callers fall
  /// back to the unanalyzed path - never to wrong analysis).
  /// </summary>
  public static ControlFlowGraph? TryBuild(IReadOnlyList<Statement> body) {
    var builder = new Builder();
    var entry = builder.NewBlock();
    var fall = builder.BuildSequence(body, entry);
    if (builder.Failed)
      return null;
    if (fall != null)
      builder.LinkUnconditional(fall, builder.ExitBlock);
    builder.ComputePredecessors();
    return new(entry, builder.ExitBlock, builder.AllBlocks);
  }

  private sealed class Builder {
    private readonly List<BasicBlock> _blocks = [];
    public bool Failed { get; private set; }
    public BasicBlock ExitBlock { get; }
    public IReadOnlyList<BasicBlock> AllBlocks => this._blocks;

    public Builder() => this.ExitBlock = this.NewBlock();

    public BasicBlock NewBlock() {
      var block = new BasicBlock(this._blocks.Count);
      this._blocks.Add(block);
      return block;
    }

    public void LinkUnconditional(BasicBlock from, BasicBlock to) => from.TrueSucc = to;

    private void LinkBranch(BasicBlock from, Expression condition, BasicBlock onTrue, BasicBlock onFalse) {
      from.Condition = condition;
      from.TrueSucc = onTrue;
      from.FalseSucc = onFalse;
    }

    /// <summary>
    /// Appends <paramref name="stmts"/> to the graph starting at
    /// <paramref name="entry"/>; returns the block where control falls through
    /// afterwards, or null when the sequence cannot fall through (an EXIT/END
    /// terminator) or analysis failed.
    /// </summary>
    public BasicBlock? BuildSequence(IReadOnlyList<Statement> stmts, BasicBlock entry) {
      var current = entry;
      foreach (var stmt in stmts) {
        if (this.Failed)
          return current;
        if (current == null)
          current = this.NewBlock(); // unreachable tail (no predecessors)
        current = this.BuildStatement(stmt, current);
      }
      return current;
    }

    private BasicBlock? BuildStatement(Statement stmt, BasicBlock current) {
      switch (stmt) {
        // region exits - control leaves, no fall-through
        case ExitStmt { Kind: ExitKind.Sub or ExitKind.Function or ExitKind.Def }:
        case EndStmt:
          this.LinkUnconditional(current, this.ExitBlock);
          return null;

        case IfStmt ifStmt:
          return this.BuildIf(ifStmt, current);

        // straight-line statements the builder understands as a unit (their
        // defs/uses are read off the AST by the SSA pass); anything genuinely
        // unsupported as control flow fails the whole build below
        case AssignStmt or IncrDecrStmt or PrintStmt or CallStmt or CommandStmt
          or MidAssignStmt or LsetRsetStmt or DefSegStmt or DimStmt or MetaStmt
          or EquateStmt or DefTypeStmt or DataStmt or OpenStmt or CloseStmt
          or SeekStmt or WriteStmt or StdOutStmt or BitStmt or AscAssignStmt:
          current.Statements.Add(stmt);
          return current;

        default:
          // loops, SELECT, GOTO/labels, GOSUB, RETURN, ON*, RESUME, inline asm,
          // SWAP, INPUT/READ, ERASE, graphics, CHAIN, pointer flow, ... -> bail
          this.Failed = true;
          return current;
      }
    }

    private BasicBlock BuildIf(IfStmt ifStmt, BasicBlock current) {
      var merge = this.NewBlock();

      var thenEntry = this.NewBlock();
      var elseEntry = this.NewBlock();
      this.LinkBranch(current, ifStmt.Condition, thenEntry, elseEntry);

      var thenExit = this.BuildSequence(ifStmt.Then, thenEntry);
      if (thenExit != null)
        this.LinkUnconditional(thenExit, merge);

      var chain = elseEntry;
      foreach (var (condition, body) in ifStmt.ElseIfs) {
        var armEntry = this.NewBlock();
        var nextChain = this.NewBlock();
        this.LinkBranch(chain, condition, armEntry, nextChain);
        var armExit = this.BuildSequence(body, armEntry);
        if (armExit != null)
          this.LinkUnconditional(armExit, merge);
        chain = nextChain;
      }

      if (ifStmt.Else != null) {
        var elseExit = this.BuildSequence(ifStmt.Else, chain);
        if (elseExit != null)
          this.LinkUnconditional(elseExit, merge);
      } else
        this.LinkUnconditional(chain, merge);

      return merge;
    }

    public void ComputePredecessors() {
      foreach (var block in this._blocks)
        foreach (var succ in block.Successors)
          succ.Predecessors.Add(block);
    }
  }
}
