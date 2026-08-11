namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Sparse Conditional Constant Propagation (Wegman-Zadeck): solves the constant
/// lattice and CFG reachability together, so a value is proven constant only along
/// edges that can actually execute, and a branch on a proven-constant condition kills
/// the untaken arm. After the solve, constant instructions are replaced by their
/// values, constant conditional branches become unconditional, and the blocks that
/// became unreachable are deleted. Strictly more powerful than running constant
/// folding alone, because it sees through phis and dead control flow.
/// </summary>
public static class Sccp {

  private enum State { Top, Const, Bottom }

  private readonly struct Lat {
    public State State { get; }
    public IrConstant? Const { get; }
    private Lat(State s, IrConstant? c) { this.State = s; this.Const = c; }
    public static readonly Lat Top = new(State.Top, null);
    public static readonly Lat Bottom = new(State.Bottom, null);
    public static Lat Constant(IrConstant c) => new(State.Const, c);
  }

  /// <summary>Runs SCCP on the function in place; returns the number of values proven constant.</summary>
  public static int Run(IrFunction fn) => fn.Entry is null ? 0 : new Solver(fn).Solve();

  private sealed class Solver(IrFunction fn) {

    private readonly Dictionary<IrValue, Lat> _lattice = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrBasicBlock> _execBlocks = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(IrBasicBlock, IrBasicBlock)> _execEdges = [];
    private readonly Queue<(IrBasicBlock From, IrBasicBlock To)> _flow = new();
    private readonly Queue<IrInstruction> _ssa = new();

    public int Solve() {
      this.MarkExecutable(fn.Entry!);
      while (this._flow.Count > 0 || this._ssa.Count > 0) {
        while (this._flow.Count > 0) {
          var (from, to) = this._flow.Dequeue();
          foreach (var phi in to.Phis)
            this.VisitPhi(phi);
          if (this._execBlocks.Add(to))
            this.VisitBlock(to);
        }
        while (this._ssa.Count > 0) {
          var inst = this._ssa.Dequeue();
          if (inst.Parent is null || !this._execBlocks.Contains(inst.Parent))
            continue;
          if (inst.IsTerminator)
            this.HandleTerminator(inst.Parent);
          else if (inst is IrPhi phi)
            this.VisitPhi(phi);
          else
            this.VisitValue(inst);
        }
      }
      return this.Rewrite();
    }

    private void VisitBlock(IrBasicBlock block) {
      foreach (var inst in block.Instructions)
        if (!inst.IsTerminator && inst is not IrPhi)
          this.VisitValue(inst);
      this.HandleTerminator(block);
    }

    private void MarkExecutable(IrBasicBlock block) {
      if (this._execBlocks.Add(block))
        this.VisitBlock(block);
    }

    private void AddEdge(IrBasicBlock from, IrBasicBlock to) {
      if (this._execEdges.Add((from, to)))
        this._flow.Enqueue((from, to));
    }

    private Lat Get(IrValue v) => v switch {
      IrConstant c => Lat.Constant(c),
      IrArgument or IrGlobalValue => Lat.Bottom,
      _ => this._lattice.GetValueOrDefault(v, Lat.Top),
    };

    private void Set(IrValue v, Lat lat) {
      if (StateEquals(this.Get(v), lat))
        return;
      this._lattice[v] = lat;
      foreach (var user in v.Users)
        this._ssa.Enqueue(user);
    }

    private void VisitPhi(IrPhi phi) {
      var result = Lat.Top;
      for (var i = 0; i < phi.IncomingBlocks.Count; ++i)
        if (this._execEdges.Contains((phi.IncomingBlocks[i], phi.Parent!)))
          result = Meet(result, this.Get(phi.GetOperand(i)));
      this.Set(phi, result);
    }

    private void VisitValue(IrInstruction inst) {
      Lat result;
      switch (inst) {
        case IrBinary or IrCmp or IrCast:
          result = this.EvalFoldable(inst);
          break;
        default:
          result = Lat.Bottom;                        // loads, calls, gep, alloca: unknown
          break;
      }
      this.Set(inst, result);
    }

    private Lat EvalFoldable(IrInstruction inst) {
      var operands = inst.Operands;
      var anyBottom = false;
      foreach (var op in operands) {
        var l = this.Get(op);
        if (l.State == State.Top)
          return Lat.Top;                             // wait for the operand to resolve
        if (l.State == State.Bottom)
          anyBottom = true;
      }
      if (anyBottom)
        return Lat.Bottom;

      // all operands constant: rebuild with the constants, fold, then detach the temp
      IrInstruction? temp = inst switch {
        IrBinary b => new IrBinary(b.Op, this.ConstOf(b.Lhs), this.ConstOf(b.Rhs)),
        IrCmp c => new IrCmp(c.Pred, this.ConstOf(c.Lhs), this.ConstOf(c.Rhs)),
        IrCast cast => new IrCast(cast.Op, this.ConstOf(cast.Value), cast.Type),
        _ => null,
      };
      if (temp is null)
        return Lat.Bottom;
      var folded = IrConstFold.TryFold(temp);
      temp.DropOperandUses();                          // throwaway temp must not linger in the constants' use-lists
      return folded is null ? Lat.Bottom : Lat.Constant(folded);
    }

    private IrConstant ConstOf(IrValue v) => this.Get(v).Const!;

    private void HandleTerminator(IrBasicBlock block) {
      switch (block.Terminator) {
        case IrBr br:
          this.AddEdge(block, br.Target);
          break;
        case IrCondBr cb: {
          var c = this.Get(cb.Condition);
          if (c.State == State.Top)
            break;
          if (c.State == State.Bottom) {
            this.AddEdge(block, cb.IfTrue);
            this.AddEdge(block, cb.IfFalse);
          } else {
            this.AddEdge(block, IsTrue(c.Const!) ? cb.IfTrue : cb.IfFalse);
          }
          break;
        }
        case IrSwitch sw: {
          var c = this.Get(sw.Condition);
          if (c.State == State.Top)
            break;
          if (c.State == State.Bottom) {
            foreach (var s in sw.Successors)
              this.AddEdge(block, s);
          } else {
            var value = ((IrConstantInt)c.Const!).Value;
            this.AddEdge(block, sw.TargetFor(value));
          }
          break;
        }
      }
    }

    // ---- rewrite -----------------------------------------------------------

    private int Rewrite() {
      var proven = 0;

      // 1) replace value-producing instructions proven constant
      foreach (var inst in fn.AllInstructions.ToList()) {
        if (inst.Type.IsVoid || inst is IrPhi { Parent: null })
          continue;
        var lat = this.Get(inst);
        if (lat.State != State.Const)
          continue;
        inst.ReplaceAllUsesWith(Clone(lat.Const!));
        ++proven;
      }

      // 2) fold constant conditional branches to unconditional ones
      foreach (var block in fn.Blocks.ToList()) {
        if (!this._execBlocks.Contains(block) || block.Terminator is not IrCondBr cb)
          continue;
        var c = this.Get(cb.Condition);
        if (c.State != State.Const)
          continue;
        var taken = IsTrue(c.Const!) ? cb.IfTrue : cb.IfFalse;
        cb.EraseFromParent();
        block.Append(new IrBr(taken));
      }

      // 3) delete blocks that can no longer be reached
      this.RemoveUnreachable();
      return proven;
    }

    private void RemoveUnreachable() {
      var reachable = new HashSet<IrBasicBlock>(ReferenceEqualityComparer.Instance);
      var stack = new Stack<IrBasicBlock>();
      stack.Push(fn.Entry!);
      reachable.Add(fn.Entry!);
      while (stack.Count > 0)
        foreach (var s in stack.Pop().Successors)
          if (reachable.Add(s))
            stack.Push(s);

      var dead = fn.Blocks.Where(b => !reachable.Contains(b)).ToList();
      foreach (var block in reachable)
        foreach (var phi in block.Phis.ToList())
          foreach (var pred in phi.IncomingBlocks.ToList())
            if (!reachable.Contains(pred))
              phi.RemoveIncoming(pred);

      foreach (var block in dead) {
        foreach (var inst in block.Instructions.ToList())
          inst.EraseFromParent();
        fn.RemoveBlock(block);
      }
    }

    private static IrConstant Clone(IrConstant c) => c switch {
      IrConstantInt i => new IrConstantInt(i.Type, i.Value),
      IrConstantFloat f => new IrConstantFloat(f.Type, f.Value),
      IrNullPtr n => new IrNullPtr(n.Type),
      _ => new IrUndef(c.Type),
    };

    private static bool IsTrue(IrConstant c) => c is IrConstantInt i && !i.IsZero;

    private static bool StateEquals(Lat a, Lat b) =>
      a.State == b.State && (a.State != State.Const || ConstEquals(a.Const!, b.Const!));

    private static Lat Meet(Lat a, Lat b) {
      if (a.State == State.Top) return b;
      if (b.State == State.Top) return a;
      if (a.State == State.Bottom || b.State == State.Bottom) return Lat.Bottom;
      return ConstEquals(a.Const!, b.Const!) ? a : Lat.Bottom;
    }

    private static bool ConstEquals(IrConstant a, IrConstant b) => (a, b) switch {
      (IrConstantInt x, IrConstantInt y) => x.Type.Equals(y.Type) && x.Value == y.Value,
      (IrConstantFloat x, IrConstantFloat y) => x.Type.Equals(y.Type) && x.Value.Equals(y.Value),
      (IrNullPtr, IrNullPtr) => true,
      _ => false,
    };
  }
}
