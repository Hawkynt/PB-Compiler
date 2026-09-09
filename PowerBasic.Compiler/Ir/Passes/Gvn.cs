using System.Globalization;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Global value numbering by dominator-tree scoped hashing: two pure instructions
/// that compute the same function of the same operands are congruent, and the one
/// dominated by the other is replaced by it. Because the value table is scoped to the
/// dominator tree, a leader is only reused where it provably dominates the use - so
/// the result is always valid SSA. Commutative operands are ordered so <c>a+b</c> and
/// <c>b+a</c> are recognised as equal. This supersedes block-local CSE: it eliminates
/// redundancy across blocks, not just within one.
/// </summary>
public static class Gvn {

  /// <summary>Eliminates redundant pure computations; returns how many instructions were removed.</summary>
  public static int Run(IrFunction fn) {
    if (fn.Entry is null)
      return 0;
    var dom = IrDominators.Build(fn)!;
    var children = DomChildren(fn, dom);
    var ctx = new Context();
    ctx.Visit(fn.Entry, children);
    return ctx.Removed;
  }

  private static Dictionary<IrBasicBlock, List<IrBasicBlock>> DomChildren(IrFunction fn, IrDominators dom) {
    var children = new Dictionary<IrBasicBlock, List<IrBasicBlock>>(ReferenceEqualityComparer.Instance);
    foreach (var block in dom.ReversePostorder) {
      var idom = dom.ImmediateDominatorOf(block);
      if (idom is null || ReferenceEquals(idom, block))
        continue;
      (children.TryGetValue(idom, out var list) ? list : children[idom] = []).Add(block);
    }
    return children;
  }

  private sealed class Context {
    private readonly Dictionary<string, IrInstruction> _table = [];
    private readonly Dictionary<IrValue, int> _ids = new(ReferenceEqualityComparer.Instance);
    private int _nextId;
    public int Removed { get; private set; }

    public void Visit(IrBasicBlock block, Dictionary<IrBasicBlock, List<IrBasicBlock>> children) {
      var added = new List<string>();
      foreach (var inst in block.Instructions.ToList()) {
        var key = KeyOf(inst);
        if (key is null)
          continue;
        if (this._table.TryGetValue(key, out var leader)) {
          inst.ReplaceAllUsesWith(leader);            // leader dominates inst by construction
          inst.EraseFromParent();
          ++this.Removed;
        } else {
          this._table[key] = inst;
          added.Add(key);
        }
      }

      if (children.TryGetValue(block, out var kids))
        foreach (var child in kids)
          this.Visit(child, children);

      foreach (var key in added)                       // leave the dominator scope
        this._table.Remove(key);
    }

    private string? KeyOf(IrInstruction inst) => inst switch {
      IrBinary b => $"b{b.Op}({this.Pair(b.Lhs, b.Rhs, IsCommutative(b.Op))})",
      IrCmp c => $"c{c.Pred}({this.Pair(c.Lhs, c.Rhs, IsCommutative(c.Pred))})",
      IrCast x => $"x{x.Op}:{x.Type}({this.Operand(x.Value)})",
      IrGep g => $"g({this.Operand(g.BasePtr)},{this.Operand(g.ByteOffset)})",
      // A call is numbered only when the callee is on the checked purity list - an entry that answers
      // the same for the same arguments and leaves nothing behind, so the second one is redundant.
      // FunctionSummaries.IsPureExternal carries the argument for each row; everything else, including
      // every string entry that consumes or allocates a handle, stays unnumbered.
      IrCall { Callee: IrFunction callee } call when FunctionSummaries.IsPureExternal(callee.Name)
        => $"r{callee.Name}({string.Join(',', call.Args.Select(this.Operand))})",
      _ => null,                                       // loads/stores/other calls/allocas/phis/terminators are not numbered
    };

    private string Pair(IrValue a, IrValue b, bool commutative) {
      var ka = this.Operand(a);
      var kb = this.Operand(b);
      return commutative && string.CompareOrdinal(ka, kb) > 0 ? $"{kb},{ka}" : $"{ka},{kb}";
    }

    private string Operand(IrValue v) => v switch {
      IrConstantInt ci => $"i{ci.Type}={ci.Value.ToString(CultureInfo.InvariantCulture)}",
      IrConstantFloat cf => $"f{cf.Type}={BitConverter.DoubleToInt64Bits(cf.Value)}",
      IrNullPtr => "null",
      IrUndef => "u" + this._nextId++,                // undef is never congruent with anything
      _ => "v" + this.IdOf(v),
    };

    private int IdOf(IrValue v) {
      if (this._ids.TryGetValue(v, out var id))
        return id;
      return this._ids[v] = this._nextId++;
    }
  }

  private static bool IsCommutative(IrBinaryOp op) =>
    op is IrBinaryOp.Add or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor
       or IrBinaryOp.FAdd or IrBinaryOp.FMul;

  private static bool IsCommutative(IrCmpPred p) =>
    p is IrCmpPred.Eq or IrCmpPred.Ne or IrCmpPred.Foeq or IrCmpPred.Fone;
}
