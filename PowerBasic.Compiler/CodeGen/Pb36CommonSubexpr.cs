using System.Text;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 O3 - block-local common subexpression elimination (docs/PB36.md). A
/// pure integer subexpression tree (the SVGA corpus's <c>y*320+x</c> address
/// arithmetic, recomputed across statements) is evaluated once into a frame
/// slot and reloaded at every further occurrence within the same straight-line
/// run.
///
/// Soundness rests on one invariant: a USE only ever reloads a slot that a
/// DEFINE populated from <em>identical inputs</em> with no intervening write to
/// those inputs and no barrier. The DEFINE emits the subtree verbatim, so any
/// trap it would raise ($ERROR NUMERIC overflow on a <c>*</c>) fires exactly
/// where the un-CSE'd first occurrence would have - and a USE that reloads the
/// same value is what recomputing would have produced. Hence byte-identical.
///
/// The run model is deliberately block-local: every control-flow or
/// side-effecting statement (calls, labels, branches, loops, POKE/DEF SEG,
/// inline asm, pointer/member stores) ends the run and clears the cache; only
/// straight-line assignments / prints over pure scalar-integer arithmetic
/// keep it alive, invalidating the slots that read a written scalar.
/// </summary>
public static class Pb36CommonSubexpr {

  public readonly record struct CseMark(int Slot, bool IsDefine);

  /// <summary>Analysis result: which AST nodes to define/reload, and how many 4-byte slots the frame must reserve.</summary>
  public sealed class Result {
    public Dictionary<Expression, CseMark> Marks { get; } = new(ReferenceEqualityComparer.Instance);
    public int SlotCount { get; set; }
  }

  public static Result Analyze(IReadOnlyList<Statement> body, SemanticModel model) {
    // the modular-int16 emission path (a% = y%*320+x% computed on the 16-bit
    // ALU) is disabled whenever $ERROR NUMERIC/OVERFLOW/ALL is active, so its
    // CSE is too - keeping the analysis context in lock-step with EmitAssign
    var checkedArithmetic = model.MetaStatements.Any(m =>
      m.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
      && m.Arguments.Count >= 2
      && m.Arguments[0].Text.ToUpperInvariant() is "NUMERIC" or "OVERFLOW" or "ALL"
      && m.Arguments[^1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase));
    var state = new State(model, allowModular: !checkedArithmetic);
    state.Run(body);
    return state.Out;
  }

  private enum Mode { Integer, Modular }

  private sealed class State(SemanticModel model, bool allowModular) {
    public readonly Result Out = new();

    // key -> slot index (assigned the first time a key is reused; shared across runs)
    private readonly Dictionary<string, int> _slotOfKey = new(StringComparer.Ordinal);
    // key -> the inputs it reads, for write-invalidation
    private readonly Dictionary<string, HashSet<VariableSymbol>> _inputsOfKey = new(StringComparer.Ordinal);
    // stable small ids for variable symbols, used in the structural key
    private readonly Dictionary<VariableSymbol, int> _varId = new(ReferenceEqualityComparer.Instance);

    // live within the current straight-line run: key -> the node currently holding the value
    private Dictionary<string, Expression> _live = new(StringComparer.Ordinal);

    public void Run(IReadOnlyList<Statement> statements) => this.RunInheriting(statements, new(StringComparer.Ordinal));

    /// <summary>Runs a block starting from an inherited live cache (the dominating code's still-valid values).</summary>
    private void RunInheriting(IReadOnlyList<Statement> statements, Dictionary<string, Expression> inherited) {
      var savedLive = this._live;
      this._live = inherited;
      foreach (var statement in statements)
        this.Walk(statement);
      this._live = savedLive;
    }

    private void Walk(Statement statement) {
      switch (statement) {
        case AssignStmt a when this.IsStraightLineSafe(a):
          // a modular-int16 assignment computes its RHS on the 16-bit ALU
          // (EmitModularInt16); CSE there caches the modular value
          this.Register(a.Value, this.IsModularAssign(a) ? Mode.Modular : Mode.Integer);
          // index expressions on an array target are emitted via the normal path
          if (a.Target is CallOrIndexExpr { Arguments: { } targetArgs })
            foreach (var arg in targetArgs)
              this.Register(arg, Mode.Integer);
          this.InvalidateAfterWrite(a.Target);
          return;

        case PrintStmt p when this.IsStraightLinePrint(p):
          if (p.FileNumber is { } fn)
            this.Register(fn, Mode.Integer);
          foreach (var item in p.Items)
            if (item.Value is { } value)
              this.Register(value, Mode.Integer);
          return;

        case IncrDecrStmt id when id.Target is NameExpr && this.ScalarSymbolOf(id.Target) is { } target
            && (id.Amount == null || IsPure(id.Amount, model)):
          if (id.Amount is { } amount)
            this.Register(amount, Mode.Integer);
          this.Invalidate(target);
          return;

        case IfStmt iff when IsBarrierFree(iff.Condition, model) && iff.ElseIfs.All(e => IsBarrierFree(e.Condition, model)):
          // cross-block CSE: the code before the IF dominates every branch, so a value
          // computed before the IF is still available at each branch's start. The
          // (side-effect-free) conditions write nothing, so the inherited cache stays
          // valid; each branch invalidates a key incrementally when it writes that key's
          // inputs. Branch computations do not survive the merge, so the cache is cleared
          // afterwards. Branches emit through the mark-aware EmitExpression, so a reload
          // pairs with the unconditional pre-IF DEFINE.
          this.RunInheriting(iff.Then, new(this._live, StringComparer.Ordinal));
          foreach (var (_, elseIfBody) in iff.ElseIfs)
            this.RunInheriting(elseIfBody, new(this._live, StringComparer.Ordinal));
          if (iff.Else != null)
            this.RunInheriting(iff.Else, new(this._live, StringComparer.Ordinal));
          this._live.Clear();
          return;

        default:
          // a barrier ends the run; sub-blocks are their own runs
          this._live.Clear();
          foreach (var block in ChildBlocks(statement))
            this.Run(block);
          this._live.Clear();
          return;
      }
    }

    /// <summary>Registers every cacheable subtree of <paramref name="e"/> bottom-up, marking define/use pairs.</summary>
    private void Register(Expression e, Mode mode) {
      if (!this.IsCacheable(e, mode)) {
        foreach (var child in Children(e))
          this.Register(child, mode);
        return;
      }

      var key = (mode == Mode.Modular ? "M" : "I") + this.Key(e);
      if (this._live.TryGetValue(key, out var definer)) {
        // reuse: the earlier node becomes the DEFINE, this one a USE (and we
        // do not descend - a USE never emits its children)
        var slot = this.SlotFor(key);
        this.Out.Marks[definer] = new(slot, IsDefine: true);
        this.Out.Marks[e] = new(slot, IsDefine: false);
        return;
      }

      // first occurrence: register children first (nested CSE), then go live
      foreach (var child in Children(e))
        this.Register(child, mode);
      this._live[key] = e;
      this._inputsOfKey[key] = Inputs(e, model);
    }

    /// <summary>A composite worth a slot: an integer-typed pure tree, or (modular mode) a float-typed +,-,* tree over 16-bit integral leaves.</summary>
    private bool IsCacheable(Expression e, Mode mode) {
      if (e is not (BinaryExpr or UnaryExpr))
        return false;
      if (mode == Mode.Modular)
        return model.TypeOf(e) is ScalarType { IsFloat: true }
          && this.IsModularInt16Tree(e);
      return model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 4 } && IsPure(e, model);
    }

    /// <summary>Replicates CodeGenerator.IsModularInt16Tree: a +,-,* (and unary negate) tree over 16-bit-or-narrower integral leaves.</summary>
    private bool IsModularInt16Tree(Expression e, int depth = 0) {
      if (depth > 16)
        return false;
      if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 2 })
        return true;
      return e switch {
        UnaryExpr { Op: UnaryOp.Negate } u => this.IsModularInt16Tree(u.Operand, depth + 1),
        BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } b =>
          this.IsModularInt16Tree(b.Left, depth + 1) && this.IsModularInt16Tree(b.Right, depth + 1),
        _ => false,
      };
    }

    /// <summary>Exactly the EmitAssign condition that routes a store through EmitModularInt16.</summary>
    private bool IsModularAssign(AssignStmt a)
      => allowModular
        && model.TypeOf(a.Target) is ScalarType { IsFloat: false, ByteSize: <= 2 }
        && model.TypeOf(a.Value) is ScalarType { IsFloat: true }
        && this.IsModularInt16Tree(a.Value);

    private int SlotFor(string key) {
      if (this._slotOfKey.TryGetValue(key, out var slot))
        return slot;
      slot = this.Out.SlotCount++;
      this._slotOfKey[key] = slot;
      return slot;
    }

    private void InvalidateAfterWrite(Expression target) {
      // scalar target -> invalidate slots reading it; array element write
      // touches no cached key (arrays are never cached); member/ptr already
      // routed through the barrier path by IsStraightLineSafe
      if (this.ScalarSymbolOf(target) is { } symbol)
        this.Invalidate(symbol);
    }

    private void Invalidate(VariableSymbol symbol) {
      var stale = this._live
        .Where(kv => this._inputsOfKey.TryGetValue(kv.Key, out var ins) && ins.Contains(symbol))
        .Select(kv => kv.Key)
        .ToList();
      foreach (var key in stale)
        this._live.Remove(key);
    }

    private bool IsStraightLineSafe(AssignStmt a) {
      // RHS must be barrier-free; the target is either a pure scalar (we
      // invalidate it) or an array element with barrier-free indices
      if (!IsBarrierFree(a.Value, model))
        return false;
      switch (a.Target) {
        case NameExpr:
          return this.ScalarSymbolOf(a.Target) != null
            && model.TypeOf(a.Target) is ScalarType { IsFloat: false };
        case CallOrIndexExpr { Arguments: { } args } call
            when model.VariableBindings.ContainsKey(call):
          return args.All(arg => IsBarrierFree(arg, model));
        default:
          return false; // member/ptr stores may alias - barrier
      }
    }

    private bool IsStraightLinePrint(PrintStmt p)
      => !p.IsLPrint && p.UsingFormat == null
        && (p.FileNumber == null || IsBarrierFree(p.FileNumber, model))
        && p.Items.All(item => item.Value == null || IsBarrierFree(item.Value, model));

    private VariableSymbol? ScalarSymbolOf(Expression e)
      => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s)
        && s.Type is ScalarType && s.Storage is not VariableStorage.Static
        ? s : null;

    private string Key(Expression e) {
      var sb = new StringBuilder();
      this.AppendKey(sb, e);
      return sb.ToString();
    }

    private void AppendKey(StringBuilder sb, Expression e) {
      switch (e) {
        case IntegerLiteralExpr i:
          sb.Append('#').Append(i.Value).Append(';');
          break;
        case NamedConstantExpr c:
          sb.Append('#').Append(model.Equates.TryGetValue(c.Name, out var v) ? v.AsInteger : 0).Append(';');
          break;
        case NameExpr when model.VariableBindings.TryGetValue(e, out var sym):
          sb.Append('v').Append(this.IdOf(sym)).Append(';');
          break;
        case UnaryExpr u:
          sb.Append('u').Append((int)u.Op).Append('(');
          this.AppendKey(sb, u.Operand);
          sb.Append(')');
          break;
        case BinaryExpr b:
          sb.Append('b').Append((int)b.Op).Append('(');
          this.AppendKey(sb, b.Left);
          this.AppendKey(sb, b.Right);
          sb.Append(')');
          break;
        default:
          sb.Append('?').Append(e.GetHashCode()).Append(';');
          break;
      }
    }

    private int IdOf(VariableSymbol symbol) {
      if (this._varId.TryGetValue(symbol, out var id))
        return id;
      id = this._varId.Count;
      this._varId[symbol] = id;
      return id;
    }
  }

  // ---- pure static predicates shared with the (identical) emission walk ----

  /// <summary>True for an expression built only from scalar integer reads, literals, equates and integer operators.</summary>
  private static bool IsPure(Expression e, SemanticModel model) => e switch {
    IntegerLiteralExpr => true,
    NamedConstantExpr c => !(model.Equates.TryGetValue(c.Name, out var v) && v.Text != null),
    NameExpr n => !model.IntrinsicBindings.ContainsKey(n)
      && model.VariableBindings.TryGetValue(n, out var s)
      && s.Type is ScalarType { IsFloat: false },
    UnaryExpr { Op: UnaryOp.Negate or UnaryOp.Not } u => IsPure(u.Operand, model),
    BinaryExpr b => b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
        or BinaryOp.IntegerDivide or BinaryOp.Modulo
        or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp
        or BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical
        or BinaryOp.RotateLeft or BinaryOp.RotateRight
      && IsPure(b.Left, model) && IsPure(b.Right, model),
    _ => false,
  };

  /// <summary>An expression that calls nothing, dereferences nothing and reads no volatile intrinsic.</summary>
  private static bool IsBarrierFree(Expression e, SemanticModel model) {
    switch (e) {
      case IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr:
        return true;
      case NameExpr n:
        return !model.IntrinsicBindings.ContainsKey(n);
      case UnaryExpr u:
        return IsBarrierFree(u.Operand, model);
      case BinaryExpr b:
        return IsBarrierFree(b.Left, model) && IsBarrierFree(b.Right, model);
      case ByValArgExpr bv:
        return IsBarrierFree(bv.Value, model);
      case FileNumberExpr f:
        return IsBarrierFree(f.Number, model);
      case CallOrIndexExpr call when model.VariableBindings.ContainsKey(call):
        // a variable array read - barrier-free iff its indices are
        return call.Arguments.All(a => IsBarrierFree(a, model));
      default:
        return false; // function/intrinsic calls, pointer deref, member access
    }
  }

  private static HashSet<VariableSymbol> Inputs(Expression e, SemanticModel model) {
    var set = new HashSet<VariableSymbol>(ReferenceEqualityComparer.Instance);
    Collect(e);
    return set;

    void Collect(Expression node) {
      if (node is NameExpr && model.VariableBindings.TryGetValue(node, out var sym))
        set.Add(sym);
      foreach (var child in Children(node))
        Collect(child);
    }
  }

  private static IEnumerable<Expression> Children(Expression e) => e switch {
    UnaryExpr u => [u.Operand],
    BinaryExpr b => [b.Left, b.Right],
    CallOrIndexExpr c => c.Arguments,
    ByValArgExpr v => [v.Value],
    FileNumberExpr f => [f.Number],
    _ => [],
  };

  private static IEnumerable<IReadOnlyList<Statement>> ChildBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, body) in i.ElseIfs)
          yield return body;
        if (i.Else != null)
          yield return i.Else;
        break;
      case SelectStmt sel:
        foreach (var arm in sel.Arms)
          yield return arm.Body;
        break;
      case ForStmt f:
        yield return f.Body;
        break;
      case DoLoopStmt d:
        yield return d.Body;
        break;
    }
  }
}
