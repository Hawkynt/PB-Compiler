using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen.Ssa;

/// <summary>
/// Sparse conditional constant propagation over <see cref="SsaForm"/>
/// (docs/PB36.md O17). Solves the constant lattice and block reachability
/// together: a branch whose condition folds to a constant marks only the taken
/// edge live, so phi merges ignore values flowing through dead edges. The
/// arithmetic is delegated to the emitter's <see cref="ConstantFolder"/> (via
/// leaf substitution) and every stored value is wrapped to its variable's type,
/// so a proven constant equals the exact value the program would compute.
/// The output (<see cref="ProvenReads"/>) maps each variable read the solver
/// proved constant to that value, ready for the emitter to fold.
/// </summary>
public sealed class Sccp {
  private enum State { Top, Const, Bottom }

  private readonly record struct Lat(State State, long Value) {
    public static readonly Lat Top = new(State.Top, 0);
    public static readonly Lat Bottom = new(State.Bottom, 0);
    public static Lat Of(long v) => new(State.Const, v);

    /// <summary>Lattice meet: Top is identity, Bottom absorbs, unequal constants drop to Bottom.</summary>
    public Lat Meet(Lat other) {
      if (this.State == State.Top)
        return other;
      if (other.State == State.Top)
        return this;
      if (this.State == State.Bottom || other.State == State.Bottom)
        return Bottom;
      return this.Value == other.Value ? this : Bottom;
    }
  }

  private readonly SemanticModel _model;
  private readonly SsaForm _ssa;
  private readonly ConstantFolder _folder;
  private readonly Dictionary<SsaValue, Lat> _values = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<BasicBlock, List<SsaValue>> _byBlock = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<BasicBlock> _reachableBlocks = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<(BasicBlock, BasicBlock)> _reachableEdges = [];

  private Sccp(SemanticModel model, SsaForm ssa) {
    this._model = model;
    this._ssa = ssa;
    this._folder = new(model.Equates);
  }

  /// <summary>Solves SCCP and returns the reads proven constant (NameExpr -> value).</summary>
  public static Dictionary<NameExpr, long> Solve(SemanticModel model, SsaForm ssa) {
    var sccp = new Sccp(model, ssa);
    sccp.Run();
    return sccp.Collect();
  }

  private void Run() {
    foreach (var block in this._ssa.Dominators.ReversePostorder)
      this._byBlock[block] = [];
    foreach (var value in this._ssa.Values) {
      this._values[value] = value.Kind == SsaDefKind.EntryZero ? Lat.Of(0) : Lat.Top;
      if (this._byBlock.TryGetValue(value.Block, out var list))
        list.Add(value); // values in unreachable blocks stay Top (never folded)
    }

    this._reachableBlocks.Add(this._ssa.Cfg.Entry);

    var changed = true;
    while (changed) {
      changed = false;
      foreach (var block in this._ssa.Dominators.ReversePostorder) {
        if (!this._reachableBlocks.Contains(block))
          continue;
        // re-evaluate this block's values (phis first by construction order)
        foreach (var value in this._byBlock[block]) {
          var updated = this.Evaluate(value);
          if (!updated.Equals(this._values[value])) {
            this._values[value] = updated;
            changed = true;
          }
        }
        // resolve out-edges from the (possibly now-constant) condition
        changed |= this.PropagateEdges(block);
      }
    }
  }

  private Lat Evaluate(SsaValue value) {
    switch (value.Kind) {
      case SsaDefKind.EntryZero:
        return Lat.Of(0);

      case SsaDefKind.Phi: {
        var result = Lat.Top;
        foreach (var (pred, input) in value.PhiInputs)
          if (this._reachableEdges.Contains((pred, value.Block)))
            result = result.Meet(this._values[input]);
        return result;
      }

      case SsaDefKind.IncrDecr: {
        var baseLat = this._values[value.IncrBase!];
        if (baseLat.State != State.Const)
          return baseLat.State == State.Top ? Lat.Top : Lat.Bottom;
        var amount = this.Fold(value.IncrAmount, defaultWhenNull: 1);
        if (amount is not { } step)
          return value.IncrAmount != null && this.HasPendingInput(value.IncrAmount) ? Lat.Top : Lat.Bottom;
        var raw = value.IncrUp ? baseLat.Value + step : baseLat.Value - step;
        return Lat.Of(CodeGenerator.WrapToType(raw, (ScalarType)value.Variable.Type));
      }

      default: { // Assign
        if (this.InputState(value.DefExpr!) is { } pending)
          return pending; // a tracked input is still Top or already Bottom
        var folded = this.Fold(value.DefExpr, defaultWhenNull: 0);
        return folded is { } v
          ? Lat.Of(CodeGenerator.WrapToType(v, (ScalarType)value.Variable.Type))
          : Lat.Bottom;
      }
    }
  }

  /// <summary>True/Top/Bottom rollup of the tracked reads in <paramref name="e"/>; null = all inputs are Const (safe to fold).</summary>
  private Lat? InputState(Expression e) {
    var sawTop = false;
    foreach (var read in TrackedReads(e)) {
      var lat = this._values[this._ssa.UseVersions[read]];
      if (lat.State == State.Bottom)
        return Lat.Bottom;
      if (lat.State == State.Top)
        sawTop = true;
    }
    return sawTop ? Lat.Top : null;
  }

  private bool HasPendingInput(Expression e) => this.InputState(e) is { State: State.Top };

  /// <summary>Folds an expression with every tracked read substituted by its proven constant; null = not a constant integer.</summary>
  private long? Fold(Expression? e, long defaultWhenNull) {
    if (e == null)
      return defaultWhenNull;
    var folded = this._folder.TryFold(this.Substitute(e));
    return folded?.Integer;
  }

  /// <summary>Clones <paramref name="e"/> replacing each tracked, proven-constant read with its literal value.</summary>
  private Expression Substitute(Expression e) {
    switch (e) {
      case NameExpr name when this._ssa.UseVersions.TryGetValue(name, out var version) && this._values[version].State == State.Const:
        return new IntegerLiteralExpr(name.Position, this._values[version].Value, TypeSuffix.None);
      case UnaryExpr u:
        return u with { Operand = this.Substitute(u.Operand) };
      case BinaryExpr b:
        return b with { Left = this.Substitute(b.Left), Right = this.Substitute(b.Right) };
      default:
        return e; // literals, equates, untracked reads, calls - the folder handles or rejects them
    }
  }

  private IEnumerable<NameExpr> TrackedReads(Expression e) {
    switch (e) {
      case NameExpr name when this._ssa.UseVersions.ContainsKey(name):
        yield return name;
        break;
      case UnaryExpr u:
        foreach (var r in TrackedReads(u.Operand))
          yield return r;
        break;
      case BinaryExpr b:
        foreach (var r in TrackedReads(b.Left))
          yield return r;
        foreach (var r in TrackedReads(b.Right))
          yield return r;
        break;
    }
  }

  private bool PropagateEdges(BasicBlock block) {
    var changed = false;
    void Live(BasicBlock? succ) {
      if (succ == null)
        return;
      changed |= this._reachableEdges.Add((block, succ));
      changed |= this._reachableBlocks.Add(succ);
    }

    if (block.Condition == null) {
      Live(block.TrueSucc); // unconditional (or a region exit with no successor)
      return changed;
    }

    var inputs = this.InputState(block.Condition);
    if (inputs is { State: State.Top })
      return changed; // a condition input is still undefined - leave both edges dark

    var cond = inputs is null ? this.Fold(block.Condition, defaultWhenNull: 0) : null;
    if (cond is { } value) {
      // a proven-constant condition lights only the taken edge (PB truth: 0 false, else true)
      Live(value != 0 ? block.TrueSucc : block.FalseSucc);
    } else {
      Live(block.TrueSucc);
      Live(block.FalseSucc);
    }
    return changed;
  }

  private Dictionary<NameExpr, long> Collect() {
    var result = new Dictionary<NameExpr, long>(ReferenceEqualityComparer.Instance);
    foreach (var (read, version) in this._ssa.UseVersions)
      if (this._values[version] is { State: State.Const } lat)
        result[read] = lat.Value;
    return result;
  }
}
