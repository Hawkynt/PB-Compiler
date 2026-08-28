using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 2 of the x86-16 back end (docs/X86-BACKEND.md): selects the typed-SSA IR into the
/// <see cref="MFunction"/> machine IR over virtual registers. Each SSA value becomes a virtual
/// register (or an immediate for an <see cref="IrConstantInt"/>); each instruction lowers to one or
/// more <see cref="MInstr"/> in two-address x86 form. Anything it cannot model makes
/// <see cref="TrySelect"/> return null, so the coverage census can name the unsupported construct and
/// the migration path can temporarily use the direct code generator.
/// Frame offsets are NOT resolved here - allocas become symbolic <see cref="MOperand.StackSlot"/>s and
/// register binding happens in later stages.
/// </summary>
public sealed partial class InstructionSelector {

  private readonly Dictionary<IrValue, MReg> _vregs = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The HIGH word of a baseline 32-bit value: <see cref="_vregs"/> holds its low word and this its
  /// high one. Keeping the halves as ordinary virtual registers means the allocator needs no notion
  /// of pairing. Eligible loop-carried values instead occupy one native dword virtual register under
  /// an optimized 386 SPEED target and therefore have no entry here.
  /// </summary>
  private readonly Dictionary<IrValue, MReg> _hiVregs = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Loop-carried 32-bit phis that may stay whole in a 386 register under the SPEED objective. Other
  /// LONG values keep the baseline word-pair representation, so selecting a newer CPU cannot change
  /// the 8086 ABI or make general allocation depend on paired registers.
  /// </summary>
  private readonly HashSet<IrPhi> _nativeDwordPhis = new(ReferenceEqualityComparer.Instance);
  private readonly Dictionary<IrAlloca, int> _slots = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Where each floating-point SSA value lives: a frame cell, not a register. x87 computes on a stack
  /// the linear-scan allocator does not model, so selection brackets every float operation with
  /// FLD/FSTP - the stack is empty again at each instruction boundary, and the value in between is
  /// simply its cell. That is also what the direct emitter does with ST0.
  /// </summary>
  private readonly Dictionary<IrValue, int> _fslots = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// IEEE float parameters already live in caller-owned stack cells at their declared widths. x87 can
  /// load those cells directly, so they need neither a virtual register nor a temporary frame slot.
  /// </summary>
  private readonly Dictionary<IrValue, MOperand.ParamCell> _floatParams =
    new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Where a QUAD read out of storage lives: an eight-byte frame cell of its own. A 64-bit integer
  /// has no register representation on this target - it would need four of them - and the x87 is
  /// the only unit that handles one whole, so the value is copied into a private cell the moment it
  /// is read and taken from there. The copy is not caution, it is correctness: an SSA load names the
  /// bytes AT THAT POINT, and re-reading the source at the use would see whatever a store in between
  /// had put there.
  /// </summary>
  private readonly Dictionary<IrValue, int> _qslots = new(ReferenceEqualityComparer.Instance);
  private MFunction _function = null!;

  /// <summary>
  /// The block instructions are appended to. Most selections stay inside one machine block, but
  /// materializing a comparison's value needs a branch - and therefore a split - so the cursor may
  /// move on while one IR block is being selected.
  /// </summary>
  private MBlock _current = null!;
  private int _nextVreg;
  private int _splitCount;
  private string? _decline;

  /// <summary>
  /// The instruction set and the optimization objective this selection is for - see
  /// <see cref="SelectionTarget"/>. Both reach encoding decisions the IR cannot express: whether a
  /// transcendental is one instruction or a call (<see cref="MathSequence"/>), and which shape a
  /// <see cref="IrSwitch"/> dispatch takes.
  /// </summary>
  private readonly SelectionTarget _target;

  /// <summary>
  /// The target's cost model, carried on <see cref="SelectionTarget"/> and supplied only when the
  /// caller wants the SPEED-objective selections that trade bytes for cycles - today the constant
  /// multiply decomposition (<see cref="TryDecomposeConstantMultiply"/>). Null means "emit the compact
  /// form", which is what every caller with no opinion gets, so nothing changes for them.
  /// </summary>
  private CodeGen.TargetCost? _cost => this._target.Cost;

  private bool UsesNativeDwordRegisters
    => this._target is { Cpu386: true, Optimize: true, OptimizeSpeed: true };

  private InstructionSelector(SelectionTarget target) => this._target = target;

  /// <summary>Selects a function into machine IR, or null if it contains a construct this stage cannot model.</summary>
  public static MFunction? TrySelect(IrFunction fn, bool cpu386 = false)
    => TrySelect(fn, out _, new SelectionTarget(Cpu386: cpu386));

  /// <summary>Selects a function into machine IR for a given target and objective, or null when it declines.</summary>
  public static MFunction? TrySelect(IrFunction fn, SelectionTarget target) => TrySelect(fn, out _, target);

  /// <summary>
  /// Selects a function into machine IR, reporting <paramref name="declineReason"/> - the construct that
  /// stopped it - when the result is null. The reason is what the coverage census reads to rank which
  /// widening buys the most eligible functions, so it names the IR construct, not the failing routine.
  /// </summary>
  public static MFunction? TrySelect(IrFunction fn, out string? declineReason, bool cpu386 = false)
    => TrySelect(fn, out declineReason, new SelectionTarget(Cpu386: cpu386));

  /// <summary>The same, for a given target and objective.</summary>
  public static MFunction? TrySelect(IrFunction fn, out string? declineReason, SelectionTarget target) {
    declineReason = null;
    if (fn.IsDeclaration || fn.Entry is null) {
      declineReason = "declaration";
      return null;
    }
    var selector = new InstructionSelector(target);
    var selected = selector.Run(fn);
    if (selected is null)
      declineReason = selector._decline ?? "unknown";
    return selected;
  }

  /// <summary>Records why selection stopped (the first reason wins - it is the one that fired).</summary>
  private bool Decline(string reason) {
    this._decline ??= reason;
    return false;
  }

  /// <summary>As <see cref="Decline"/>, for the paths that return a null <see cref="MFunction"/>.</summary>
  private MFunction? DeclineNull(string reason) {
    this._decline ??= reason;
    return null;
  }

  private MFunction? Run(IrFunction fn) {
    this._function = new MFunction(fn.Name) { HasArgumentPlan = true };

    if (this.UsesNativeDwordRegisters && IrDominators.Build(fn) is { } dominators)
      this._nativeDwordPhis.UnionWith(NativeDwordPhis(fn, dominators));

    // arguments take the FIRST virtual registers (so argument i is vreg i, which the emitter's ABI
    // prologue relies on to load argument i into allocation[i]); they are function live-ins
    for (var index = 0; index < fn.Parameters.Count; ++index) {
      var arg = fn.Parameters[index];
      if (arg.Type.IsFloat) {
        // The direct BASIC/PASCAL caller pushed the value's raw IEEE words. Keep the declared width:
        // FLD widens it to x87 only when an instruction consumes the parameter.
        this._floatParams[arg] = new MOperand.ParamCell(index, 0, RegSize(arg.Type));
        continue;
      }
      if (IsWide(arg.Type)) {
        // a 32-bit argument arrives as two words: its low half at the parameter's own offset and its
        // high half at +2, each into its own register
        var (lo, hi) = this.FreshPair(arg);
        this._function.ArgumentLoads.Add((lo.Reg.VirtualId, index, 0));
        this._function.ArgumentLoads.Add((hi.Reg.VirtualId, index, 2));
        continue;
      }
      var vreg = this.FreshVreg(arg.Type);
      this._vregs[arg] = vreg;
      this._function.ArgumentLoads.Add((vreg.VirtualId, index, 0));
    }

    // each phi then gets a virtual register; the value is materialized by copies on the incoming edges
    // (out-of-SSA), so a use of the phi simply reads this register
    foreach (var block in fn.Blocks)
      foreach (var phi in block.Phis)
        if (phi.Type.IsFloat)
          this.FloatCell(phi);                 // a float lives in a frame cell, never a register: the
                                               // edge copies below are FLD/FSTP through it
        else if (IsWide(phi.Type)) {
          if (this._nativeDwordPhis.Contains(phi))
            this._vregs[phi] = this.FreshVreg(phi.Type);
          else
            this.FreshPair(phi);               // a baseline LONG still needs both word halves
        }
        else
          this._vregs[phi] = this.FreshVreg(phi.Type);

    this.CollectIdioms(fn);

    var mblocks = new Dictionary<string, MBlock>();
    foreach (var block in SelectionOrder(fn)) {
      var mblock = new MBlock(block.Label);
      this._function.Blocks.Add(mblock);
      this._current = mblock;
      var folded = FoldedCompare(block);
      foreach (var instr in block.Instructions) {
        if (instr is IrPhi)
          continue;                 // phis emit no instruction - their edge copies are inserted below
        if (ReferenceEquals(instr, block.Terminator)) {
          if (!this.SelectTerminator(block.Terminator, folded, mblock))
            return null;
          break;
        }
        if (ReferenceEquals(instr, folded))
          continue;                 // the compare is folded into the conditional branch below
        if (this._consumed.Contains(instr))
          continue;                 // absorbed by a multi-instruction pattern that emits it later
        if (!this.SelectInstruction(instr, mblock))
          return null;
      }
      // a split leaves the cursor on a later block; the phi copies for this predecessor must be
      // inserted there, since that is the block control actually leaves from
      mblocks[block.Label] = this._current;
    }

    if (!this.InsertPhiCopies(fn, mblocks))
      return null;

    this._function.VirtualRegisterCount = this._nextVreg;
    if (this._target is { Optimize: true, OptimizeSpeed: true })
      MachineLoopRotation.Run(this._function);
    // the encoding-level idioms, which only exist once every IR instruction has been given its own
    // virtual register (see Peephole). Gated on the objective: with the optimizer off this stage must
    // write what it would have written.
    if (this._target.Optimize)
      Peephole.Run(this._function);
    return this._function;
  }

  /// <summary>
  /// The order the blocks are selected - and therefore laid out - in: REVERSE POSTORDER from the
  /// entry, with anything unreachable left in the order the function lists it.
  ///
  /// <para>
  /// Selection mints a value's register when it reaches the instruction that defines it, and reads
  /// that register when it reaches a use. SSA promises the definition DOMINATES the use; it promises
  /// nothing about the order the blocks happen to sit in the function's list, and the two are not the
  /// same thing. GOSUB is where they part company: the dispatch block is built at the first RETURN,
  /// so it is listed after the continuations it switches to - and once CSE commons the address the
  /// dispatch pops from with the one a continuation pushes to, the definition is in a block listed
  /// AFTER its use. The selector reached the use first, found no register, and declined the whole
  /// function with "IrGep has no register" - a true statement about a program that was never
  /// ill-formed.
  /// </para>
  ///
  /// <para>
  /// Reverse postorder places every block after every block that dominates it, which is exactly the
  /// promise SSA makes. It costs nothing elsewhere: block terminators are always explicit jumps here
  /// (nothing falls through to the next listed block), and liveness is a control-flow dataflow rather
  /// than a linear scan of the list, so the order is a layout choice and not a correctness one.
  /// </para>
  /// </summary>
  private static IEnumerable<IrBasicBlock> SelectionOrder(IrFunction fn) {
    if (IrDominators.Build(fn) is not { } dominators)
      return fn.Blocks;
    var reachable = new HashSet<IrBasicBlock>(dominators.ReversePostorder, ReferenceEqualityComparer.Instance);
    return reachable.Count == fn.Blocks.Count
      ? dominators.ReversePostorder
      : [.. dominators.ReversePostorder, .. fn.Blocks.Where(b => !reachable.Contains(b))];
  }

  /// <summary>
  /// Finds the loop-carried LONG phis whose complete recurrence can stay in native dwords. The set is
  /// reduced to a fixed point: a phi remains only when every incoming value is a constant, another
  /// remaining phi, or an arithmetic expression composed solely from those values. A runtime result,
  /// load, argument, cast, or unsupported operation keeps that whole recurrence on word pairs.
  /// </summary>
  private static HashSet<IrPhi> NativeDwordPhis(IrFunction function, IrDominators dominators) {
    var candidates = new HashSet<IrPhi>(ReferenceEqualityComparer.Instance);
    foreach (var block in function.Blocks)
      foreach (var phi in block.Phis)
        if (IsWide(phi.Type)
            && phi.IncomingBlocks.Any(predecessor => dominators.Dominates(block, predecessor)))
          candidates.Add(phi);

    bool IsNativeExpression(IrValue value) => value switch {
      IrConstantInt => true,
      IrPhi phi => candidates.Contains(phi),
      IrBinary binary when binary.Op is IrBinaryOp.Add or IrBinaryOp.Sub
          or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor
        => IsNativeExpression(binary.Lhs) && IsNativeExpression(binary.Rhs),
      _ => false,
    };

    for (var changed = true; changed;) {
      changed = false;
      foreach (var phi in candidates.ToList())
        if (phi.Operands.Any(value => !IsNativeExpression(value))) {
          candidates.Remove(phi);
          changed = true;
        }
    }
    return candidates;
  }

  /// <summary>
  /// Out-of-SSA: for every phi, copy each incoming value into the phi's register at the end of the
  /// corresponding predecessor block (before its terminator).
  ///
  /// <para>
  /// The copies on one edge are a PARALLEL copy - they all read the values the predecessor ends with -
  /// so writing them out one after another is only correct in an order where no copy overwrites a
  /// register a later one still has to read. <see cref="SequenceEdgeCopies"/> finds such an order, and
  /// mints a scratch register for the one shape that has none: a cycle, of which the two-value swap is
  /// the smallest.
  /// </para>
  /// </summary>
  private bool InsertPhiCopies(IrFunction fn, Dictionary<string, MBlock> mblocks) {
    foreach (var predBlock in fn.Blocks) {
      var copies = new List<(MReg Dest, MOperand Source)>();
      var floatCopies = new List<(MOperand Destination, MOperand Source)>();
      foreach (var block in fn.Blocks)
        foreach (var phi in block.Phis)
          if (phi.IncomingFrom(predBlock) is { } value) {
            if (phi.Type.IsFloat) {
              // a float edge copy goes through the x87, not a register: load the incoming cell, store
              // the phi's. There is no copy CYCLE to worry about the way there is for registers -
              // each pair is a complete load-and-store, so nothing is half-overwritten in between
              if (!this.TryFloatOperand(value, out var incoming))
                return false;
              floatCopies.Add((this.FloatCell(phi), incoming));
              continue;
            }
            if (IsWide(phi.Type)) {
              if (this._vregs[phi].Size == MRegSize.Dword) {
                if (!this.TryNativeDwordOperand(value, out var nativeSource))
                  return this.Decline("phi: a native dword loop value has a non-dword incoming edge");
                copies.Add((this._vregs[phi], nativeSource));
                continue;
              }
              // both halves of a 32-bit phi are copied on the edge, low then high
              if (!this.TryOperandPair(value, out var lowSource, out var highSource))
                return false;
              copies.Add((this._vregs[phi], lowSource));
              copies.Add((this._hiVregs[phi], highSource));
              continue;
            }
            if (!this.TryOperand(value, out var source))
              return false;
            copies.Add((this._vregs[phi], source));
          }
      if (copies.Count == 0 && floatCopies.Count == 0)
        continue;

      var mblock = mblocks[predBlock.Label];
      var insertAt = mblock.Instructions.FindIndex(i => i.IsTerminator);
      if (insertAt < 0)
        insertAt = mblock.Instructions.Count;
      foreach (var (dest, source) in this.SequenceEdgeCopies(copies)) {
        var copy = new MInstr(MOpcode.Mov, [new MOperand.Register(dest), source],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
            ReadsFlags: false, WritesFlags: false, ReadsMemory: source.IsMemoryAccess(), WritesMemory: false));
        mblock.Instructions.Insert(insertAt++, copy);
      }
      foreach (var (destination, source) in floatCopies) {
        mblock.Instructions.Insert(insertAt++, new MInstr(MOpcode.Fld, [source],
          new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false)));
        mblock.Instructions.Insert(insertAt++, new MInstr(MOpcode.Fstp, [destination],
          new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));
      }
    }

    return true;
  }

  /// <summary>
  /// Puts the parallel copy on one CFG edge into an order that can be written out one instruction at a
  /// time. A copy may be emitted once no copy still waiting reads the register it overwrites; that
  /// rule alone orders every acyclic edge, which is nearly all of them - and used to be a decline,
  /// because the old test asked whether a source was ANY destination rather than whether the copies
  /// could be ordered at all.
  ///
  /// <para>
  /// What no order can answer is a CYCLE - <c>a &lt;- b</c> beside <c>b &lt;- a</c>, the loop-carried
  /// swap <c>DIFF39</c> and <c>DIFF49</c> write when their counters exchange residency. One value has
  /// to be kept somewhere outside the cycle while the rest move over it, so the cycle is broken by
  /// copying one destination's incoming value into a fresh virtual register FIRST and rewriting every
  /// remaining reader of it to read that register instead. The saving copy is appended before anything
  /// that overwrites the register it reads, which is what makes it a save rather than a second reader.
  /// </para>
  ///
  /// <para>
  /// A scratch register rather than <c>XCHG</c>: the values here are virtual, so an exchange would have
  /// to be undone in the allocator's terms rather than the selector's, and a value spilled to the frame
  /// has no exchange instruction at all. The register is minted at selection, so it is an ordinary
  /// value the allocator sees from the start - not a spiller-minted one, and so not a member of
  /// <see cref="MFunction.MovedValues"/>, whose whole meaning is "already moved once during spilling".
  /// </para>
  ///
  /// <para>
  /// Termination: every pass either removes a copy from the pending set or breaks one cycle by making
  /// one register unread, and a broken cycle cannot re-form because the scratch register is never a
  /// destination. So at most one scratch per cycle is minted, and the loop is bounded by the number of
  /// copies on the edge.
  /// </para>
  /// </summary>
  private List<(MReg Dest, MOperand Source)> SequenceEdgeCopies(List<(MReg Dest, MOperand Source)> copies) {
    var pending = new List<(MReg Dest, MOperand Source)>(copies);
    var ordered = new List<(MReg Dest, MOperand Source)>(copies.Count);
    while (pending.Count > 0) {
      var ready = -1;
      for (var i = 0; i < pending.Count && ready < 0; ++i) {
        var overwritten = pending[i].Dest;
        var stillNeeded = false;
        for (var j = 0; j < pending.Count && !stillNeeded; ++j)
          stillNeeded = j != i && ReadsRegister(pending[j].Source, overwritten);
        if (!stillNeeded)
          ready = i;
      }
      if (ready >= 0) {
        ordered.Add(pending[ready]);
        pending.RemoveAt(ready);
        continue;
      }
      // Every remaining destination is still read by another remaining copy, so the pending set is a
      // union of cycles. Lift one register out of its cycle into a scratch and the cycle opens.
      var held = pending[0].Dest;
      var scratch = MReg.Virtual(this._nextVreg++, held.Size);
      ordered.Add((scratch, new MOperand.Register(held)));
      for (var j = 0; j < pending.Count; ++j)
        pending[j] = (pending[j].Dest, RenameRegister(pending[j].Source, held, scratch));
    }
    return ordered;
  }

  /// <summary>
  /// Whether an operand's value depends on a register - as the value itself, or as part of the
  /// effective address of a memory reference (which is the same set <see cref="LivenessAnalysis"/>
  /// counts as reads). Size is deliberately not part of the comparison: a byte view and a word view of
  /// one virtual register are the same storage, and overwriting either destroys the other.
  /// </summary>
  private static bool ReadsRegister(MOperand operand, MReg register) => operand switch {
    MOperand.Register r => SameRegister(r.Reg, register),
    MOperand.Memory m => (m.Base is { } b && SameRegister(b, register))
      || (m.Index is { } x && SameRegister(x, register))
      || (m.Segment is { } s && SameRegister(s, register)),
    _ => false,
  };

  /// <summary>The same operand reading <paramref name="to"/> wherever it read <paramref name="from"/>, at the width it read it.</summary>
  private static MOperand RenameRegister(MOperand operand, MReg from, MReg to) => operand switch {
    MOperand.Register r when SameRegister(r.Reg, from) => new MOperand.Register(to with { Size = r.Reg.Size }),
    MOperand.Memory m => m with {
      Base = m.Base is { } b && SameRegister(b, from) ? to with { Size = b.Size } : m.Base,
      Index = m.Index is { } x && SameRegister(x, from) ? to with { Size = x.Size } : m.Index,
      Segment = m.Segment is { } s && SameRegister(s, from) ? to with { Size = s.Size } : m.Segment,
    },
    _ => operand,
  };

  /// <summary>Whether two references name the same storage, whatever width each reads it at.</summary>
  private static bool SameRegister(MReg left, MReg right)
    => left.IsVirtual == right.IsVirtual
       && (left.IsVirtual ? left.VirtualId == right.VirtualId : left.Physical == right.Physical);

  /// <summary>The compare that feeds a block's conditional-branch terminator and nothing else (so it folds into the branch), or null.</summary>
  private static IrCmp? FoldedCompare(IrBasicBlock block)
    // A compare only folds if the branch can actually USE it: one user, and a predicate that maps to
    // a condition code. Marking one folded makes the block loop skip it, so a compare marked folded
    // and then not consumed by the terminator has no register at all - which is what the branch's
    // value path then reported as "IrCmp has no register".
    => block.Terminator is IrCondBr { Condition: IrCmp { Users.Count: 1 } cmp }
       && MapPredicate(cmp.Pred) is not null
       && !IsWide(cmp.Lhs.Type)
       && !cmp.Lhs.Type.IsFloat
      ? cmp : null;

  private bool SelectTerminator(IrInstruction? terminator, IrCmp? folded, MBlock block) {
    switch (terminator) {
      case IrRet ret:
        return this.SelectRet(ret, block);
      case IrBr br:
        this._current.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(br.Target.Label)], MInstrEffect.None));
        this._current.Successors.Add(br.Target.Label);
        return true;
      case IrCondBr cond when folded is { } cmp && MapPredicate(cmp.Pred) is { } cc:
        if (!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, out var rhs))
          return false;
        if (lhs is not MOperand.Register) {
          // CMP wants a register on the left, and a constant there is not a dead end: comparing the
          // other way round asks the same question with the predicate mirrored - `5 > x` is `x < 5` -
          // so the operands swap and the condition follows them. Equality mirrors to itself.
          if (rhs is MOperand.Register) {
            (lhs, rhs) = (rhs, lhs);
            cc = MapPredicate(Mirrored(cmp.Pred))!.Value;
          } else {
            // Neither side is in a register - two memory cells, or a constant against one. Mirroring
            // cannot help when there is nothing to mirror ONTO, so the left operand is moved into a
            // register and the comparison proceeds unmirrored. One MOV, and only on the shape that
            // used to decline outright.
            var held = this.FreshVreg(cmp.Lhs.Type);
            var into = new MOperand.Register(held);
            this._current.Instructions.Add(new MInstr(MOpcode.Mov, [into, lhs], MovEffect(into, lhs)));
            lhs = into;
          }
        }
        this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
          new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
            ReadsMemory: lhs.IsMemoryAccess() || rhs.IsMemoryAccess(), WritesMemory: false)));
        this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(cond.IfTrue.Label)],
          new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false), cc));
        this._current.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(cond.IfFalse.Label)], MInstrEffect.None));
        this._current.Successors.Add(cond.IfTrue.Label);
        this._current.Successors.Add(cond.IfFalse.Label);
        return true;
      // Control does not get here, and the block already says so: RESUME jumps through a runtime cell
      // and never comes back, so the intrinsic has left a JmpIndirect behind. The IR still needs a
      // terminator on such a block, which is what this is - it emits nothing, and only when the block
      // really is closed already. An unreachable with no terminator in front of it would fall into
      // whatever follows, so that one still declines.
      case IrUnreachable when this._current.Instructions is [.., { IsTerminator: true }]:
        return true;
      // An unreachable block that is NOT already closed - the default arm of the GOSUB dispatch, which
      // a RETURN with nothing on the shadow stack would take - still needs an instruction that cannot
      // fall into whichever block is laid out next. Leaving the function is that instruction: it is
      // the one thing that is always available, always terminates, and is never taken.
      case IrUnreachable:
        this._current.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
        return true;
      // A branch whose condition is a VALUE rather than a compare it can fold - PB's -1/0 truth value
      // arriving from a materialized comparison, a logical operator, or a 32-bit compare that had to
      // build its answer in a register. Testing it against zero is what the direct emitter does too.
      case IrCondBr valued: {
        if (!this.TryOperand(valued.Condition, out var condition))
          return false;
        if (condition is not MOperand.Register)
          return this.Decline("terminator: IrCondBr on a non-register condition");
        this.EmitCompare(condition, new MOperand.Immediate(0));
        this.EmitBranch(Condition.NotEqual, valued.IfTrue.Label);
        this._current.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(valued.IfFalse.Label)], MInstrEffect.None));
        this._current.Successors.Add(valued.IfTrue.Label);
        this._current.Successors.Add(valued.IfFalse.Label);
        return true;
      }
      case IrSwitch sw:
        return this.SelectSwitch(sw);
      // GOTO / GOSUB DWORD: the destination is a VALUE, so the branch is `JMP reg` and the block's
      // successors are the instruction's own target list - the labels the function has taken the
      // address of. Nothing here chooses between them; they are what the allocator's liveness reads
      // so that a value live into a computed label is still live when the jump lands there.
      case IrIndirectBr indirect: {
        if (!this.TryOperand(indirect.Address, out var address))
          return false;
        if (address is not MOperand.Register)
          return this.Decline("terminator: IrIndirectBr on an address that is not in a register");
        this._current.Instructions.Add(new MInstr(MOpcode.JmpIndirect, [address],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: false,
            ReadsMemory: false, WritesMemory: false)));
        foreach (var target in indirect.Targets)
          AddSuccessor(this._current, target.Label);
        return true;
      }
      default:
        return this.Decline($"terminator: {terminator?.GetType().Name ?? "none"}"
          + (terminator is IrCondBr ? " (condition is not a folded compare)" : ""));
    }
  }

  /// <summary>
  /// An integer switch as an 8086 compare chain. Byte/word conditions compare each case directly.
  /// A dword condition first branches by high word, then compares the low word inside that group;
  /// this keeps equality bit-exact without pretending the 16-bit target has a 32-bit CMP.
  /// </summary>
  private bool SelectSwitch(IrSwitch sw) {
    if (!sw.Condition.Type.IsInteger || sw.Condition.Type.Bits is not (8 or 16 or 32))
      return this.Decline($"switch: condition {sw.Condition.Type} is not a supported integer width");
    foreach (var (value, _) in sw.Cases)
      if (!sw.IsCaseValueRepresentable(value))
        return this.Decline($"switch: case {value} does not fit {sw.Condition.Type}");

    if (sw.Condition is IrConstantInt constant) {
      var target = sw.TargetFor(constant.Value);
      this.EmitJump(target.Label);
      AddSuccessor(this._current, target.Label);
      return true;
    }

    // a shape that dispatches in constant (or logarithmic) time rather than a compare per case, when
    // the case set and the objective warrant one - see InstructionSelector.Dispatch.cs
    if (this.TrySelectDispatch(sw))
      return true;

    return sw.Condition.Type.Bits == 32
      ? this.SelectWideSwitch(sw)
      : this.SelectNarrowSwitch(sw);
  }

  private bool SelectNarrowSwitch(IrSwitch sw) {
    if (!this.TryOperand(sw.Condition, out var condition) || condition is not MOperand.Register conditionRegister)
      return this.Decline("switch: condition is not in a register");

    var dispatch = this._current;
    this.EmitEqualityChain(dispatch, conditionRegister,
      sw.Cases.Select(item => ((MOperand)SwitchImmediate(sw.Condition.Type, item.Value), item.Target.Label)).ToList(),
      sw.DefaultTarget.Label, "switch");
    this._current = dispatch;                    // phi copies belong before the IR predecessor's dispatch
    return true;
  }

  /// <summary>
  /// Groups dword cases by their high word. The dispatch block chooses a group; that group's block
  /// compares low words and otherwise reaches the default. Phi copies for every IR successor remain
  /// in the dispatch block, before its first branch, which preserves the selector's existing
  /// out-of-SSA model across the introduced machine-only blocks.
  /// </summary>
  private bool SelectWideSwitch(IrSwitch sw) {
    if (!this.TryOperandPair(sw.Condition, out var low, out var high)
        || low is not MOperand.Register lowRegister || high is not MOperand.Register highRegister)
      return this.Decline("switch: dword condition has no register pair");

    var dispatch = this._current;
    var groups = sw.Cases
      .Select(item => {
        var bits = unchecked((uint)item.Value);
        return (High: (ushort)(bits >> 16), Low: (ushort)bits, item.Target);
      })
      .GroupBy(item => item.High)
      .ToList();
    var checks = new List<(ushort High, MBlock Block, IReadOnlyList<(MOperand Value, string Target)> Cases)>();

    foreach (var group in groups) {
      var check = new MBlock($"{dispatch.Label}.switch.low{this._splitCount++}");
      this._function.Blocks.Add(check);
      checks.Add((group.Key, check, group
        .Select(item => ((MOperand)WordImmediate(item.Low), item.Target.Label))
        .ToList()));
    }
    this.EmitEqualityChain(dispatch, highRegister,
      checks.Select(item => ((MOperand)WordImmediate(item.High), item.Block.Label)).ToList(),
      sw.DefaultTarget.Label, "switch.high");

    foreach (var (_, check, cases) in checks)
      this.EmitEqualityChain(check, lowRegister, cases, sw.DefaultTarget.Label, "switch.low");
    this._current = dispatch;                    // phi copies belong before the IR predecessor's dispatch
    return true;
  }

  /// <summary>
  /// Emits one equality decision per machine block. Keeping each <c>Jcc/Jmp</c> pair trailing is a
  /// correctness requirement: the scheduler treats only trailing terminators as pinned, so a flat
  /// chain could move work or phi copies past an earlier taken branch.
  /// </summary>
  private void EmitEqualityChain(MBlock first, MOperand.Register condition,
      IReadOnlyList<(MOperand Value, string Target)> cases, string defaultTarget, string labelStem) {
    var blocks = new List<MBlock> { first };
    for (var i = 1; i < cases.Count; ++i) {
      var next = new MBlock($"{first.Label}.{labelStem}{this._splitCount++}");
      this._function.Blocks.Add(next);
      blocks.Add(next);
    }

    for (var i = 0; i < cases.Count; ++i) {
      this._current = blocks[i];
      var (value, target) = cases[i];
      this.EmitCompare(condition, value);
      this.EmitBranch(Condition.Equal, target);
      AddSuccessor(this._current, target);
      var next = i + 1 < blocks.Count ? blocks[i + 1].Label : defaultTarget;
      this.EmitJump(next);
      AddSuccessor(this._current, next);
    }

    if (cases.Count > 0)
      return;
    this._current = first;
    this.EmitJump(defaultTarget);
    AddSuccessor(first, defaultTarget);
  }

  private void EmitJump(string target)
    => this._current.Instructions.Add(new MInstr(MOpcode.Jmp,
      [new MOperand.LabelRef(target)], MInstrEffect.None));

  private static void AddSuccessor(MBlock block, string target) {
    if (!block.Successors.Contains(target, System.StringComparer.Ordinal))
      block.Successors.Add(target);
  }

  private static MOperand.Immediate WordImmediate(ushort value)
    => new(unchecked((short)value));

  private static MOperand.Immediate SwitchImmediate(IrType type, long value)
    => type.Bits == 8
      ? new(unchecked((sbyte)value))
      : WordImmediate(unchecked((ushort)value));

  /// <summary>The read-operand indices for a two-operand CMP: operand 0 (the left) and operand 1 when it is a register.</summary>
  private static int[] RegReadIndices(MOperand left, MOperand right)
    => right is MOperand.Register ? [0, 1] : left is MOperand.Register ? [0] : [];

  /// <summary>
  /// The predicate that asks the same question with the operands the other way round: the one to use
  /// after swapping them. Note this is the MIRROR, not the negation - <c>Slt</c> becomes <c>Sgt</c>
  /// and not <c>Sge</c>, because swapping the sides does not change what counts as true.
  /// </summary>
  private static IrCmpPred Mirrored(IrCmpPred pred) => pred switch {
    IrCmpPred.Slt => IrCmpPred.Sgt,
    IrCmpPred.Sgt => IrCmpPred.Slt,
    IrCmpPred.Sle => IrCmpPred.Sge,
    IrCmpPred.Sge => IrCmpPred.Sle,
    IrCmpPred.Ult => IrCmpPred.Ugt,
    IrCmpPred.Ugt => IrCmpPred.Ult,
    IrCmpPred.Ule => IrCmpPred.Uge,
    IrCmpPred.Uge => IrCmpPred.Ule,
    _ => pred,                     // Eq and Ne are their own mirror
  };

  private static Condition? MapPredicate(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq => Condition.Equal,
    IrCmpPred.Ne => Condition.NotEqual,
    IrCmpPred.Slt => Condition.Less,
    IrCmpPred.Sle => Condition.LessOrEqual,
    IrCmpPred.Sgt => Condition.Greater,
    IrCmpPred.Sge => Condition.GreaterOrEqual,
    IrCmpPred.Ult => Condition.Below,
    IrCmpPred.Ule => Condition.BelowOrEqual,
    IrCmpPred.Ugt => Condition.Above,
    IrCmpPred.Uge => Condition.AboveOrEqual,
    _ => null,                     // float predicates: not in this increment
  };

  private static Condition? MapFloatPredicate(IrCmpPred pred) => pred switch {
    IrCmpPred.Foeq => Condition.Equal,
    IrCmpPred.Fone => Condition.NotEqual,
    IrCmpPred.Folt => Condition.Below,
    IrCmpPred.Fole => Condition.BelowOrEqual,
    IrCmpPred.Fogt => Condition.Above,
    IrCmpPred.Foge => Condition.AboveOrEqual,
    _ => null,
  };

  private bool SelectInstruction(IrInstruction instr, MBlock block) {
    if (this.RefusesMbf(instr))
      return false;
    switch (instr) {
      case IrBinary bin when bin.Type.IsFloat:
        return this.SelectFloatBinary(bin);
      case IrLoad load when load.Type.IsFloat:
        return this.SelectFloatLoad(load);
      case IrLoad load when IsQuad(load.Type):
        return this.SelectQwordLoad(load);
      case IrStore store when IsQuad(store.Value.Type):
        return this.SelectQwordStore(store);
      case IrStore store when store.Value.Type.IsFloat:
        return this.SelectFloatStore(store);
      case IrBinary bin:
        return this.SelectBinary(bin, block);
      case IrAlloca alloca:
        return this.SelectAlloca(alloca, block);
      case IrLoad load:
        return this.SelectLoad(load, block);
      case IrStore store:
        return this.SelectStore(store, block);
      case IrGep gep:
        return this.SelectGep(gep, block);
      // a far pointer is not a value on this target - it is two of them, and there is no register pair
      // to put them in that a later use could still read as one address. It is formed at the point of
      // use instead (see PointerMemory), so the instruction itself emits nothing; a use that is not a
      // load or a store finds no register for it and declines, which is the intended answer.
      case IrFarPtr:
        return true;
      case IrInlineAsm asm:
        return this.SelectInlineAsm(asm);
      case IrRet ret:
        return this.SelectRet(ret, block);
      case IrCall call:
        return this.SelectCall(call, block);
      case IrCast cast:
        return this.SelectCast(cast, block);
      case IrCmp cmp:
        return this.SelectCmpValue(cmp);
      case IrSelect sel:
        return this.SelectSelect(sel);
      default:
        return this.Decline($"instruction: {instr.GetType().Name}");   // unsupported construct - decline the whole function
    }
  }

  private bool SelectBinary(IrBinary bin, MBlock block) {
    if (bin.IsFloatOp)
      return this.Decline($"binary: float {bin.Op}");
    if (this.TrySelectBranchlessAbs(bin) || this.TrySelectBranchlessSgn(bin))
      return true;
    if (bin.Op is IrBinaryOp.SDiv or IrBinaryOp.SRem)
      return this.SelectDivide(bin, block);
    // A 32-bit UNSIGNED divide has its own runtime entries beside the signed pair, on the identical
    // DX:AX / CX:BX convention - a DWORD divides unsigned, which is a different answer rather than
    // the same one reached differently. The 16-bit unsigned form still declines: that one is DIV
    // against a zero-extended dividend rather than a call, and it has no entry to borrow.
    if (bin.Op is IrBinaryOp.UDiv or IrBinaryOp.URem && IsWide(bin.Type))
      return this.SelectWideRuntimeBinary(bin, bin.Op == IrBinaryOp.UDiv ? "rt_uldiv" : "rt_ulmod");
    if (!TryMapBinary(bin.Op, out var opcode))
      return this.Decline($"binary: {bin.Op}");   // 16-bit unsigned divide / remainder
    if (IsQuad(bin.Type))
      return this.SelectQwordBinary(bin, opcode);
    if (IsWide(bin.Type))
      return this.SelectWideBinary(bin, opcode, block);
    if (opcode == MOpcode.Imul && this.TryDecomposeConstantMultiply(bin))
      return true;
    if (opcode == MOpcode.Imul && bin.Type.Bits == 16)
      return this.SelectAccumulatorMultiply(bin);

    // two-address form: dest = lhs; dest <op>= rhs
    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryOperand(bin.Lhs, out var lhs) || !this.TryOperand(bin.Rhs, out var rhs))
      return false;
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, lhs], MovEffect(destOp, lhs)));
    // a shift by a constant takes whichever of the 8086's two forms the count fits; anything else -
    // a count the program computed, or a literal outside the immediate encoding's own window - reads
    // the count from CL and nowhere else
    if (opcode is MOpcode.Shl or MOpcode.Shr or MOpcode.Sar)
      return rhs is MOperand.Immediate { Value: >= 1 and <= 31 } literal
        ? this.SelectConstantShift(opcode, destOp, (int)literal.Value, bin.Type)
        : this.SelectVariableShift(opcode, destOp, rhs, bin.Type);
    // the two-operand IMUL has no immediate form - materialize an immediate multiplier in a register
    if (opcode == MOpcode.Imul && rhs is MOperand.Immediate) {
      var tmp = new MOperand.Register(this.FreshVreg(bin.Type));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [tmp, rhs], MovEffect(tmp, rhs)));
      rhs = tmp;
    }
    this._current.Instructions.Add(new MInstr(opcode, [destOp, rhs],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],
        ReadsFlags: false, WritesFlags: true, ReadsMemory: rhs.IsMemoryAccess(), WritesMemory: false)));
    return true;
  }

  /// <summary>
  /// A byte- or word-wide shift whose COUNT the immediate form cannot carry: one the program computed,
  /// or a literal outside the encoding's own 1..31 window. The 8086 has exactly one other form and it
  /// reads the count from <c>CL</c>, so the count is staged there and the shift names that register;
  /// the destination stays wherever the allocator put it.
  ///
  /// <para>
  /// Both cases used to end the COMPILATION rather than the statement. A computed count reached
  /// <see cref="Asm.Assembler"/> in whatever register the allocator had chosen ("variable shift counts
  /// must be in CL") and a literal 32 or 40 reached it as an immediate ("shift count must be 1..31") -
  /// so <c>SHIFT RIGHT a%, n%</c> and <c>SHIFT LEFT a%, 32</c> threw out of the back end. No corpus
  /// program does either at 16 bits; the ones that shift by a runtime count are 32- and 64-bit, where
  /// <see cref="SelectWideShift"/> declines instead, because a register pair has to walk a bit per step
  /// and that needs a loop.
  /// </para>
  ///
  /// <para>
  /// The <c>CL</c> form is also what the DIRECT emitter writes for every narrow shift, count constant
  /// or not (<c>CodeGenerator.EmitShiftRotate</c>), so an out-of-window count answers the same way on
  /// both paths - whatever the part does with a count it was never masked to.
  /// </para>
  ///
  /// <para>
  /// The staging move writes a physical register, which is what <c>PinnedByIndex</c> reads to keep the
  /// destination (and anything else live here) out of <c>CX</c>; the clobber on both instructions says
  /// the same thing to the scheduler, so nothing lands between the staging and its reader.
  /// </para>
  /// </summary>
  private bool SelectVariableShift(MOpcode opcode, MOperand.Register destination, MOperand count, IrType type) {
    var staged = RegSize(type) == MRegSize.Byte ? Pinned(Reg.CL, MRegSize.Byte) : Pinned(Reg.CX);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [staged, count], MovEffect(staged, count),
      condition: null, clobbers: [Reg.CX]));
    this._current.Instructions.Add(new MInstr(opcode, [destination, Pinned(Reg.CL, MRegSize.Byte)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: [Reg.CX]));
    return true;
  }

  /// <summary>
  /// How many single-bit shifts are emitted rather than staging the count into <c>CL</c>. Four is the
  /// direct emitter's own threshold ("1-shifts up to four, CL beyond - never the 186+ immediate form",
  /// <c>CodeGenerator.EmitShiftLeft</c>), and matching it is what keeps a shift the same size on both
  /// paths: a fifth <c>D1</c> costs two bytes and the <c>CL</c> staging costs three.
  /// </summary>
  private const int _MAX_UNROLLED_SHIFT = 4;

  /// <summary>
  /// A byte- or word-wide shift by a CONSTANT count, in a form the DECLARED part actually has.
  ///
  /// <para>
  /// The 8086 has exactly two shift encodings: by one (<c>D0</c>/<c>D1</c>) and by <c>CL</c>
  /// (<c>D2</c>/<c>D3</c>). The shift-by-immediate <c>C0</c>/<c>C1</c> is an <b>80186</b> instruction,
  /// and this selector emitted it for every count above one - so under the default <c>$CPU 8086</c> a
  /// routed program shifting by four produced bytes the declared part cannot execute. That was true of
  /// three separate selections (this one, the subscript scaling in <see cref="TryGepOffset"/>, and the
  /// sign smear in <c>SelectCast</c>) and it is the same class of defect as the <c>0F AF</c> multiply
  /// <see cref="SelectAccumulatorMultiply"/> replaced: no oracle here can see it, because
  /// <c>Cpu8086</c> implements <c>C0</c>/<c>C1</c> and DOSBox emulates a 386.
  /// </para>
  ///
  /// <para>
  /// So a small count becomes repeated single-bit shifts and a larger one goes through <c>CL</c> -
  /// exactly the rule the direct emitter states - while an 80186-or-later target keeps the compact
  /// immediate form. The threshold matters because the <c>CL</c> form is not free: it pins <c>CX</c>
  /// across the staging and the shift, and subscript scaling is one of the hottest shapes the
  /// allocator sees.
  /// </para>
  /// </summary>
  /// <summary>
  /// Turns a word register holding a value into that value's SIGN, smeared over all sixteen bits -
  /// <c>0FFFFh</c> when it is negative and zero when it is not, which is the high half of a
  /// sign-extension to a register pair.
  ///
  /// <para>
  /// Written as <c>ADD r,r; SBB r,r</c> rather than the obvious <c>SAR r,15</c>, and not to save a
  /// byte: <c>SAR r,15</c> is <c>C1</c>, an <b>80186</b> encoding, and this is the one shift count in
  /// the selector too large for <see cref="_MAX_UNROLLED_SHIFT"/> single-bit steps. Staging 15 into
  /// <c>CL</c> would be legal but would put a <c>CX</c> clobber on every widening of an INTEGER to a
  /// LONG - one of the shapes the allocator meets most often. The pair costs the same four bytes as
  /// the <c>CL</c> form, needs no register, and runs on every part: the <c>ADD</c> leaves the sign bit
  /// in <c>CF</c> and the <c>SBB</c> of a register from itself is <c>-CF</c>.
  /// </para>
  ///
  /// <para>
  /// The two are joined by the FLAGS, which the scheduler models: the <c>SBB</c> declares
  /// <c>ReadsFlags</c> and the <c>ADD</c> <c>WritesFlags</c>, exactly as the <c>SHL</c>/<c>RCL</c>
  /// steps of <see cref="SelectWideShift"/> are joined, so nothing that writes flags can be moved
  /// between them.
  /// </para>
  /// </summary>
  private void EmitSignSmear(MOperand.Register register) {
    this._current.Instructions.Add(new MInstr(MOpcode.Add, [register, register],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    this._current.Instructions.Add(new MInstr(MOpcode.Sbb, [register, register],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: true, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
  }

  private bool SelectConstantShift(MOpcode opcode, MOperand.Register destination, int count, IrType type) {
    if (count == 1 || this._target.Cpu386) {
      this.Add(opcode, destination, new MOperand.Immediate(count));
      return true;
    }
    if (count > _MAX_UNROLLED_SHIFT)
      return this.SelectVariableShift(opcode, destination, new MOperand.Immediate(count), type);
    var one = new MOperand.Immediate(1);
    for (var step = 0; step < count; ++step)
      this.Add(opcode, destination, one);
    return true;
  }

  /// An optimized 386 applies a QUAD bitwise operation as two dword halves. QUAD values already live
  /// in exact eight-byte frame cells because four allocatable word registers would consume the whole
  /// 16-bit register file; EAX is therefore only an instruction-local bridge between those cells.
  /// Its AX overlap is declared as a clobber so allocation cannot keep a live word there.
  /// </summary>
  private bool SelectQwordBinary(IrBinary bin, MOpcode opcode) {
    if (opcode is MOpcode.Shl or MOpcode.Shr or MOpcode.Sar)
      return this.SelectQwordShift(bin, opcode);
    if (this._target is not { Cpu386: true, Optimize: true }
        || opcode is not (MOpcode.And or MOpcode.Or or MOpcode.Xor))
      return this.Decline($"64-bit binary: {bin.Op} (needs the direct runtime path)");
    if (!this.TryQwordSlot(bin.Lhs, out var lhs) || !this.TryQwordSlot(bin.Rhs, out var rhs))
      return false;

    var result = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    this._qslots[bin] = result;
    var eax = new MOperand.Register(MReg.Physical_(Reg.EAX, MRegSize.Dword));
    Reg[] clobbers = [Reg.AX];
    for (var offset = 0; offset <= 4; offset += 4) {
      var left = new MOperand.StackSlot(lhs, MRegSize.Dword, offset);
      var right = new MOperand.StackSlot(rhs, MRegSize.Dword, offset);
      var destination = new MOperand.StackSlot(result, MRegSize.Dword, offset);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [eax, left],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: true, WritesMemory: false), condition: null, clobbers: clobbers));
      this._current.Instructions.Add(new MInstr(opcode, [eax, right],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: true, WritesMemory: false), condition: null, clobbers: clobbers));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destination, eax],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
          ReadsMemory: false, WritesMemory: true), condition: null, clobbers: clobbers));
    }
    return true;
  }

  /// <summary>
  /// An optimized 386 shifts a QUAD by moving its two dword halves through EDX:EAX. Counts 1..31 can
  /// cross the half boundary with one SHLD/SHRD and one ordinary shift; zero and counts at least 32
  /// stay on the direct path because the processor masks them and would change BASIC's loop semantics.
  /// </summary>
  private bool SelectQwordShift(IrBinary bin, MOpcode opcode) {
    if (this._target is not { Cpu386: true, Optimize: true }
        || opcode is not (MOpcode.Shl or MOpcode.Shr)
        || WideShiftCount(bin.Rhs) is not { } count || count is < 1 or > 31)
      return this.Decline($"64-bit shift: {bin.Op} (needs an optimized 386 constant count 1..31)");
    if (!this.TryQwordSlot(bin.Lhs, out var source))
      return false;

    var result = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    this._qslots[bin] = result;
    var eax = new MOperand.Register(MReg.Physical_(Reg.EAX, MRegSize.Dword));
    var edx = new MOperand.Register(MReg.Physical_(Reg.EDX, MRegSize.Dword));
    var immediate = new MOperand.Immediate(count);
    Reg[] clobbers = [Reg.AX, Reg.DX];
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [eax, new MOperand.StackSlot(source, MRegSize.Dword)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false), condition: null, clobbers: clobbers));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [edx, new MOperand.StackSlot(source, MRegSize.Dword, 4)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false), condition: null, clobbers: clobbers));

    var (doubleOpcode, doubleDestination, doubleSource, singleOpcode, singleDestination) =
      opcode == MOpcode.Shl
        ? (MOpcode.Shld, edx, eax, MOpcode.Shl, eax)
        : (MOpcode.Shrd, eax, edx, MOpcode.Shr, edx);
    this._current.Instructions.Add(new MInstr(doubleOpcode,
      [doubleDestination, doubleSource, immediate],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), condition: null, clobbers: clobbers));
    this._current.Instructions.Add(new MInstr(singleOpcode, [singleDestination, immediate],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false), condition: null, clobbers: clobbers));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(result, MRegSize.Dword), eax],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true), condition: null, clobbers: clobbers));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.StackSlot(result, MRegSize.Dword, 4), edx],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [1], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: true), condition: null, clobbers: clobbers));
    return true;
  }

  /// <summary>A qword cell for a loaded/derived QUAD, or a newly staged literal.</summary>
  private bool TryQwordSlot(IrValue value, out int slot) {
    if (this._qslots.TryGetValue(value, out slot))
      return true;
    if (value is not IrConstantInt { Type: { IsInteger: true, Bits: 64 }, Value: var constant })
      return this.Decline($"64-bit operand: {value.GetType().Name} has no cell");

    slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);
    for (var offset = 0; offset < 8; offset += 2)
      this.StoreWord(Shifted(cell, offset), new MOperand.Immediate((short)(constant >> (offset * 8))));
    return true;
  }

  /// <summary>
  /// A 16-bit multiply in the accumulator form every 8086 has: <c>MOV AX,lhs; IMUL r16</c>, taking the
  /// product's low half back out of AX.
  ///
  /// The two-operand <c>IMUL r16, r/m16</c> this used to emit is <c>0F AF</c> - an 80386 encoding. On
  /// the default 8086 target it is not an instruction at all, so a routed program that multiplied was
  /// relying on the emulator being a 486. The accumulator form is what the part actually has and what
  /// the direct emitter writes on every tier, so both paths now spell a multiply the same way - which
  /// also makes the shape the optimizer's byte-pattern expectations name (<c>F7 /5</c>) the shape a
  /// routed function emits.
  ///
  /// <para>
  /// The shape is the one <see cref="SelectDivide"/> already uses for <c>IDIV</c>: the second operand
  /// goes to a register (there is no immediate form), <c>AX</c> and <c>DX</c> are declared clobbers so
  /// the allocator parks nothing live in them, and the result is copied straight back out. Only the
  /// low half is read, which is exactly the modular product the IR's <c>mul i16</c> means.
  /// </para>
  ///
  /// <para>
  /// The factor is left to the allocator rather than pinned to the <c>BX</c> the direct emitter always
  /// uses, and that is a measured decision: <c>BX</c> is one of only three registers that can address
  /// memory, and pinning it across every multiply left the spill loop unable to place an address value
  /// - the allocator retried past any useful bound. Matching the direct emitter's register exactly is
  /// not worth a compiler that does not finish.
  /// </para>
  /// </summary>
  private bool SelectAccumulatorMultiply(IrBinary bin) {
    if (!this.TryOperand(bin.Lhs, out var lhs) || !this.TryOperand(bin.Rhs, out var rhsSource))
      return false;
    var factor = new MOperand.Register(this.FreshVreg(bin.Type));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [factor, rhsSource], MovEffect(factor, rhsSource)));

    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, lhs], MovEffect(ax, lhs),
      condition: null, clobbers: [Reg.AX, Reg.DX]));
    this._current.Instructions.Add(new MInstr(MOpcode.Imul, [factor],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: [Reg.AX, Reg.DX]));

    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ax], MovEffect(destOp, ax)));
    return true;
  }

  /// <summary>
  /// O0078 - a 16-bit multiply by a compile-time constant, decomposed into shifts and adds instead of
  /// the multiply unit. The direct emitter does this while emitting (see
  /// <c>CodeGenerator.TryEmitModularConstMul</c>); this is the same decomposition on the same terms,
  /// so a routed function and a directly-emitted one make the same trade.
  ///
  /// <para>
  /// <b>Why it is sound.</b> The IR's <c>mul i16</c> is modular, and every chain below reproduces the
  /// product's low sixteen bits exactly: a power of two is one shift; <c>2^a + 2^b</c> is
  /// <c>(x + x&lt;&lt;(a-b)) &lt;&lt; b</c>; a contiguous run of ones <c>2^a - 2^b</c> is
  /// <c>(x&lt;&lt;(a-b) - x) &lt;&lt; b</c>; three and four set bits thread the running <c>x&lt;&lt;k</c>
  /// through one temporary. Shifting past the width only feeds in zeroes, which is what the discarded
  /// high bits of the multiply would have been.
  /// </para>
  ///
  /// <para>
  /// <b>What it deliberately refuses.</b> It only runs when a cost model was supplied, which the code
  /// generator does only under <c>$OPTIMIZE SPEED</c> - the chain is BIGGER than the compact
  /// <c>IMUL</c> and buys only cycles, so the default and SIZE keep the multiply. Four set bits are
  /// additionally priced per target (<see cref="CodeGen.TargetCost.PreferShiftAddMultiply"/>): a win
  /// against the 8086's ~124-cycle microcoded multiply, a loss against the 386's ten-ish. Five or more
  /// never pay. Multipliers 0, 1 and -1 are left alone because they are <c>InstCombine</c>'s to fold
  /// and folding them here would hide it. Negative multipliers are left alone too - the magnitude form
  /// needs a trailing <c>NEG</c> and they are rare enough not to be worth a second shape to verify.
  /// And any step needing a shift by more than four is refused outright, because this target is an
  /// 8086: <c>SHL r,imm</c> above one is an 80186 encoding, so a shift is spelled as repeated
  /// <c>SHL r,1</c> here and a long one would cost more bytes than the multiply it replaced.
  /// </para>
  /// </summary>
  private bool TryDecomposeConstantMultiply(IrBinary bin) {
    if (this._cost is not { } cost || IsWide(bin.Type) || !bin.Type.IsInteger || bin.Type.Bits != 16)
      return false;
    IrValue variable;
    long raw;
    if (bin.Rhs is IrConstantInt right) {
      variable = bin.Lhs;
      raw = right.Value;
    } else if (bin.Lhs is IrConstantInt left) {
      variable = bin.Rhs;
      raw = left.Value;
    } else {
      return false;
    }

    var m = (short)(raw & 0xFFFF);
    if (m <= 1)
      return false;                                // 0/1/-1 are folds, negatives need a NEG
    var mag = (uint)m;
    var lo = System.Numerics.BitOperations.TrailingZeroCount(mag);
    var setBits = System.Numerics.BitOperations.PopCount(mag);
    var run = mag >> lo;

    // the chain as (shiftOfTheRunningTerm, thenAddOrSubtractItIntoTheResult) steps, before the final
    // <<lo that puts the factored-out power of two back
    List<(int Shift, bool Subtract)> steps;
    if (setBits == 1)
      steps = [];
    else if (setBits == 2)
      steps = [(31 - System.Numerics.BitOperations.LeadingZeroCount(mag) - lo, false)];
    else if (System.Numerics.BitOperations.IsPow2(run + 1))
      steps = [(System.Numerics.BitOperations.TrailingZeroCount(run + 1), true)];
    else if (setBits == 3 || (setBits == 4 && cost.PreferShiftAddMultiply(4)))
      steps = [.. BitPositions(mag).Skip(1).Select(bit => (bit - lo, false))];
    else
      return false;

    // a run of ones shifts the RESULT and subtracts the original; every other shape shifts the
    // running term. Either way no single shift may exceed four - see the remarks above.
    var deltas = steps.Select((s, i) => i == 0 ? s.Shift : s.Shift - steps[i - 1].Shift).Append(lo);
    if (deltas.Any(d => d > 4))
      return false;

    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryOperand(variable, out var source))
      return false;
    this.Add(MOpcode.Mov, destOp, source);

    if (steps is [(var width, true)]) {            // 2^a - 2^b: shift the result, subtract the original
      var original = new MOperand.Register(this.FreshVreg(bin.Type));
      this.Add(MOpcode.Mov, original, destOp);
      this.ShiftLeftBy(destOp, width);
      this.Add(MOpcode.Sub, destOp, original);
    } else if (steps.Count > 0) {                  // a sum of powers of two: thread x<<k through one temp
      var running = new MOperand.Register(this.FreshVreg(bin.Type));
      this.Add(MOpcode.Mov, running, destOp);
      var shifted = 0;
      foreach (var (shift, _) in steps) {
        this.ShiftLeftBy(running, shift - shifted);
        shifted = shift;
        this.Add(MOpcode.Add, destOp, running);
      }
    }
    this.ShiftLeftBy(destOp, lo);
    return true;
  }

  /// <summary>The one-bit positions of <paramref name="value"/>, low to high.</summary>
  private static IEnumerable<int> BitPositions(uint value) {
    for (var bit = 0; bit < 32; ++bit)
      if ((value & (1u << bit)) != 0)
        yield return bit;
  }

  /// <summary>
  /// Shifts a register left by a small constant as repeated <c>SHL r,1</c> - the only left shift an
  /// 8086 has for a count above one is through <c>CL</c>, and a pinned register in the middle of an
  /// arithmetic chain costs the allocator more than the bytes save.
  /// </summary>
  private void ShiftLeftBy(MOperand.Register register, int count) {
    for (var i = 0; i < count; ++i)
      this.Add(MOpcode.Shl, register, new MOperand.Immediate(1));
  }

  /// <summary>
  /// Appends a two-address instruction of the decomposition chain. A MOV only writes its destination;
  /// every other opcode here reads it as well, which is what the two-address form means.
  /// </summary>
  private void Add(MOpcode opcode, MOperand.Register dest, MOperand source) {
    if (opcode == MOpcode.Mov) {
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source)));
      return;
    }
    this._current.Instructions.Add(new MInstr(opcode, [dest, source],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [0, 1] : [0],
        ReadsFlags: false, WritesFlags: true, ReadsMemory: source.IsMemoryAccess(), WritesMemory: false)));
  }

  /// <summary>
  /// A 32-bit add/subtract/bitwise op over register pairs: the low halves combine first and the high
  /// halves follow, with <c>ADC</c>/<c>SBB</c> threading the carry for add and subtract. Multiply,
  /// divide and the shifts need a runtime helper or a CL count and are declined.
  /// </summary>
  private bool SelectWideBinary(IrBinary bin, MOpcode opcode, MBlock block) {
    if (this.UsesNativeDwordRegisters
        && opcode is MOpcode.Add or MOpcode.Sub or MOpcode.And or MOpcode.Or or MOpcode.Xor
        && this.TryNativeDwordOperand(bin.Lhs, out var nativeLhs)
        && this.TryNativeDwordOperand(bin.Rhs, out var nativeRhs)) {
      var destination = this.FreshVreg(bin.Type);
      this._vregs[bin] = destination;
      var destinationOperand = new MOperand.Register(destination);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destinationOperand, nativeLhs],
        MovEffect(destinationOperand, nativeLhs)));
      this.Add(opcode, destinationOperand, nativeRhs);
      return true;
    }
    if (opcode is MOpcode.Shl or MOpcode.Shr or MOpcode.Sar)
      return this.SelectWideShift(bin, opcode, block);
    var high = opcode switch {
      MOpcode.Add => MOpcode.Adc,
      MOpcode.Sub => MOpcode.Sbb,
      MOpcode.And or MOpcode.Or or MOpcode.Xor => opcode,   // bitwise: the halves are independent
      _ => MOpcode.Ret,                                     // sentinel: no 32-bit form here
    };
    if (opcode == MOpcode.Imul)
      return this.SelectWideMultiply(bin);
    if (high == MOpcode.Ret)
      return this.Decline($"32-bit binary: {bin.Op} (needs a runtime helper)");
    if (!this.TryOperandPair(bin.Lhs, out var lhsLo, out var lhsHi)
        || !this.TryOperandPair(bin.Rhs, out var rhsLo, out var rhsHi))
      return false;

    var (destLo, destHi) = this.FreshPair(bin);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destLo, lhsLo], MovEffect(destLo, lhsLo)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, lhsHi], MovEffect(destHi, lhsHi)));
    this._current.Instructions.Add(new MInstr(opcode, [destLo, rhsLo], PairEffect(rhsLo, readsFlags: false, writesFlags: true)));
    // ADC/SBB read the carry the low half just wrote, so the two must stay adjacent - the effect
    // descriptor says so, which is what keeps the scheduler from separating them
    this._current.Instructions.Add(new MInstr(high, [destHi, rhsHi], PairEffect(rhsHi, readsFlags: high != opcode, writesFlags: true)));
    return true;
  }

  /// <summary>
  /// A 32-bit shift by a compile-time count, one bit at a time across the pair: each step shifts one
  /// half and rotates the bit that fell out of it through the carry into the other
  /// (<c>SHL lo,1 / RCL hi,1</c> going left, <c>SAR|SHR hi,1 / RCR lo,1</c> going right). The 386's
  /// <c>SHLD</c>/<c>SHRD</c> would do it in one instruction, but this target is an 8086; a variable
  /// count would need a loop, so it is declined, and a large count is left to the runtime rather than
  /// unrolled into a wall of instructions.
  /// </summary>
  /// <summary>
  /// The shift count as a compile-time number, seeing through the WIDENING the operand carries.
  ///
  /// <c>SHIFT LEFT s, 4</c> shifts a 32-bit value by a 16-bit constant, so the lowering widens the
  /// count to match the value being shifted and the count arrives as <c>zext i16 4 to i32</c> - a
  /// constant wearing a cast. Matching only the bare constant declined the statement over the cast
  /// rather than over anything about the shift, which is what kept LOWLEVEL.BAS off the IR path.
  /// Widening a constant cannot change it (the value is non-negative and the target is wider), so
  /// the cast is peeled rather than folded.
  /// </summary>
  private static long? WideShiftCount(IrValue count) => count switch {
    IrConstantInt c => c.Value,
    IrCast { Op: IrCastOp.ZExt or IrCastOp.SExt, Value: IrConstantInt c } => c.Value,
    // A TRUNC can change the value, so it is only safe where the result still fits.
    IrCast { Op: IrCastOp.Trunc, Value: IrConstantInt c, Type: { } to } when c.Value >= 0 && c.Value < (1L << Math.Min(to.Bits, 62)) => c.Value,
    _ => null,
  };

  private bool SelectWideShift(IrBinary bin, MOpcode opcode, MBlock block) {
    if (this._target is { Cpu386: true, Optimize: true }
        && WideShiftCount(bin.Rhs) is { } nativeCount && nativeCount is >= 1 and <= 31)
      return this.SelectNativeWideShift(bin, opcode, nativeCount);
    // ...except by exactly sixteen, which is not a shift on a register pair at all: it is the two
    // halves changing places. That is two moves rather than the thirty-two shift/rotate steps the
    // bit-at-a-time loop would need, and it is how a segment and an offset are joined into one
    // DWORD (CODEPTR32) or taken apart again.
    if (WideShiftCount(bin.Rhs) is 16 && opcode is MOpcode.Shl or MOpcode.Shr)
      return this.SelectWideWordSwap(bin, opcode == MOpcode.Shl);
    if (WideShiftCount(bin.Rhs) is not { } count || count is < 0 or > 8)
      return this.Decline($"32-bit binary: {bin.Op} (only a small constant count, not {bin.Rhs})");
    if (!this.TryOperandPair(bin.Lhs, out var lhsLo, out var lhsHi))
      return false;

    var (destLo, destHi) = this.FreshPair(bin);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destLo, lhsLo], MovEffect(destLo, lhsLo)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, lhsHi], MovEffect(destHi, lhsHi)));

    var one = new MOperand.Immediate(1);
    for (var step = 0; step < count; ++step) {
      // left: the low half shifts first and its carry rotates into the high half; right: the mirror
      var (first, firstOp, second, secondOp) = opcode == MOpcode.Shl
        ? (destLo, MOpcode.Shl, destHi, MOpcode.Rcl)
        : (destHi, opcode, destLo, MOpcode.Rcr);
      this._current.Instructions.Add(new MInstr(firstOp, [first, one],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false)));
      this._current.Instructions.Add(new MInstr(secondOp, [second, one],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: true, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false)));
    }
    return true;
  }

  /// <summary>
  /// A 386 shifts a staged LONG in one operand-size-prefixed instruction. The rest of the back end
  /// deliberately represents an i32 as two word registers; a four-byte frame cell is the lossless
  /// bridge to the native dword instruction without teaching allocation that those two resources are
  /// one register. Staging also works under register pressure because the shift accepts memory.
  /// </summary>
  private bool SelectNativeWideShift(IrBinary bin, MOpcode opcode, long count) {
    if (!this.TryOperandPair(bin.Lhs, out var lhsLo, out var lhsHi))
      return false;

    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(4);
    var lowCell = new MOperand.StackSlot(slot, MRegSize.Word);
    var highCell = new MOperand.StackSlot(slot, MRegSize.Word, 2);
    var dword = new MOperand.StackSlot(slot, MRegSize.Dword);
    this.StoreWord(lowCell, lhsLo);
    this.StoreWord(highCell, lhsHi);
    this._current.Instructions.Add(new MInstr(opcode, [dword, new MOperand.Immediate(count)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true)));

    var (destLo, destHi) = this.FreshPair(bin);
    this.LoadWord(destLo, lowCell);
    this.LoadWord(destHi, highCell);
    return true;
  }

  /// <summary>
  /// A 32-bit shift by exactly sixteen: the surviving half moves to the other end of the pair and the
  /// vacated one becomes zero. Left is <c>hi = lo, lo = 0</c> and logical right its mirror; the
  /// ARITHMETIC right shift is not here, because its vacated half is the sign rather than zero.
  /// </summary>
  private bool SelectWideWordSwap(IrBinary bin, bool left) {
    if (!this.TryOperandPair(bin.Lhs, out var lhsLo, out var lhsHi))
      return false;
    var (destLo, destHi) = this.FreshPair(bin);
    var (kept, keptSource, cleared) = left ? (destHi, lhsLo, destLo) : (destLo, lhsHi, destHi);
    var zero = new MOperand.Immediate(0);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [kept, keptSource], MovEffect(kept, keptSource)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [cleared, zero], MovEffect(cleared, zero)));
    return true;
  }

  /// <summary>A 32-bit multiply through the runtime's <c>DX:AX, CX:BX -&gt; DX:AX</c> ABI.</summary>
  private bool SelectWideMultiply(IrBinary bin) => this.SelectWideRuntimeBinary(bin, "rt_lmul");

  /// <summary>A signed 32-bit divide/remainder through the same pair-register ABI as the direct emitter.</summary>
  private bool SelectWideDivide(IrBinary bin) => this.SelectWideRuntimeBinary(bin,
    bin.Op == IrBinaryOp.SDiv ? "rt_ldiv" : "rt_lmod");

  /// <summary>
  /// A 32-bit runtime binary operation. x86-16 has no native 32x32 arithmetic form for these
  /// operations, so the runtime uses the convention <c>left DX:AX, right CX:BX -&gt; DX:AX</c>. The
  /// call destroys the caller-saved file, which lets the allocator spill values live across it just
  /// as it does for a user call.
  /// </summary>
  private bool SelectWideRuntimeBinary(IrBinary bin, string label) {
    if (!this.TryOperandPair(bin.Lhs, out var leftLo, out var leftHi))
      return false;
    if (!this.TryOperandPair(bin.Rhs, out var rightLo, out var rightHi))
      return false;

    // the pair registers are pinned, so each move declares them all as clobbers: nothing live may
    // sit in DX:AX or CX:BX while the sequence is being built
    var pinned = new[] { Reg.AX, Reg.DX, Reg.BX, Reg.CX };
    void Load(Reg reg, MOperand source) {
      var dest = new MOperand.Register(MReg.Physical_(reg, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source),
        condition: null, clobbers: pinned));
    }
    Load(Reg.AX, leftLo);
    Load(Reg.DX, leftHi);
    Load(Reg.BX, rightLo);
    Load(Reg.CX, rightHi);

    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(label)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));

    var (lo, hi) = this.FreshPair(bin);
    var axResult = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    var dxResult = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, axResult], MovEffect(lo, axResult)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, dxResult], MovEffect(hi, dxResult)));
    return true;
  }

  /// <summary>
  /// A signed divide or remainder. The 32-bit form uses the direct emitter's runtime ABI; the 16-bit
  /// form uses <c>IDIV</c>, fixed to <c>DX:AX</c>: <c>CWD</c> sign-extends the dividend, the quotient
  /// comes back in <c>AX</c>, and the remainder in <c>DX</c>.
  ///
  /// PowerBASIC raises Error 11 on a zero divisor, and that guard is part of the language rather than
  /// an <c>$ERROR</c> option. The lowering emits the guard, so a runtime divisor is safe here and a
  /// known nonzero constant lets the optimizer erase it. A literal <c>-1</c> still declines because
  /// <c>MININT / -1</c> overflows the hardware instruction.
  /// </summary>
  private bool SelectDivide(IrBinary bin, MBlock block) {
    if (IsWide(bin.Type))
      return this.SelectWideDivide(bin);
    if (bin.Type.Bits != 16)
      return this.Decline($"binary: {bin.Op} on {bin.Type} (16-bit only)");
    // No constant-divisor restriction any more: the Error 11 guard is emitted by the LOWERING, as a
    // comparison and a raise the optimizer folds away whenever the divisor is a non-zero constant.
    // What arrives here is therefore already guarded, whatever the divisor turned out to be.
    // MININT / -1 is the one quotient IDIV cannot produce - it overflows into a fault rather than a
    // number - so a divisor that is LITERALLY -1 still declines. A runtime divisor that happens to
    // hold -1 is a different question, and one the direct emitter does not answer either.
    if (bin.Rhs is IrConstantInt { Value: -1 })
      return this.Decline($"binary: {bin.Op} by -1 (MININT / -1 overflows IDIV)");
    if (!this.TryOperand(bin.Lhs, out var dividend) || !this.TryOperand(bin.Rhs, out var divisorSource))
      return false;

    // the divisor must be a register - IDIV has no immediate form, whether the value came from a
    // constant or from a variable
    var divisorReg = new MOperand.Register(this.FreshVreg(bin.Type));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [divisorReg, divisorSource],
      MovEffect(divisorReg, divisorSource)));

    // AX is written here, not by an allocated vreg - so it is declared a clobber too, which is what
    // keeps the allocator from parking some other live value (or the dividend itself) in AX
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, dividend], MovEffect(ax, dividend),
      condition: null, clobbers: [Reg.AX, Reg.DX]));
    this._current.Instructions.Add(new MInstr(MOpcode.Cwd, [],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: [Reg.AX, Reg.DX]));
    this._current.Instructions.Add(new MInstr(MOpcode.Idiv, [divisorReg],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: [Reg.AX, Reg.DX]));

    // One IDIV computes both answers. When the collector found a dominated matching operation,
    // capture both physical results now and let the block loop skip the second IR instruction.
    Capture(bin);
    if (this._sharedDivRem.TryGetValue(bin, out var paired))
      Capture(paired);
    return true;

    void Capture(IrBinary value) {
      var destination = new MOperand.Register(this.FreshVreg(value.Type));
      this._vregs[value] = destination.Reg;
      var physical = value.Op == IrBinaryOp.SDiv ? Reg.AX : Reg.DX;
      var result = new MOperand.Register(MReg.Physical_(physical, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destination, result], MovEffect(destination, result)));
    }
  }

  private bool SelectAlloca(IrAlloca alloca, MBlock block) {
    var byteSize = SizeOf(alloca.Allocated);
    var count = System.Math.Max(1, alloca.Count);
    var slot = this._function.StackSlots.Count;
    for (var i = 0; i < count; ++i)
      this._function.StackSlots.Add(byteSize);
    this._slots[alloca] = slot;
    // The alloca result is the address the ELEMENTS are indexed from, and the two run in opposite
    // directions: slots are laid out downward from BP (slot 0 at [BP-2], slot 1 at [BP-4], ...) while
    // a GEP walks upward from the base. So a multi-slot alloca has to point at its LAST slot, the
    // lowest address of the block - pointing at slot 0 puts element 0 at the block's TOP and sends
    // every later element climbing out of the frame into the saved BP, the return address and the
    // caller's arguments. A DIM a%(0 TO 49) that summed its fifty elements read the parameter list
    // back and reported plausible numbers for it. A single-slot alloca is unaffected: last IS first.
    var dest = this.FreshVreg(IrType.Ptr);
    this._vregs[alloca] = dest;
    var destOp = new MOperand.Register(dest);
    this._current.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, new MOperand.StackSlot(slot + count - 1, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
  }

  private bool SelectLoad(IrLoad load, MBlock block) {
    if (load.Type.IsFloat)
      return this.Decline($"floating point: {load.Type} through the scalar path");
    if (IsWide(load.Type)) {
      if (this.PointerMemory(load.Pointer, MRegSize.Word) is not { } lowCell)
        return false;
      var (lo, hi) = this.FreshPair(load);
      var highCell = Shifted(lowCell, 2);   // little-endian: the high word follows
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, lowCell], MovEffect(lo, lowCell)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, highCell], MovEffect(hi, highCell)));
      return true;
    }
    if (load.Type.Bits > 32)
      return this.Decline($"load: {load.Type} is wider than a register pair");
    var dest = this.FreshVreg(load.Type);
    this._vregs[load] = dest;
    var destOp = new MOperand.Register(dest);
    if (this.PointerMemory(load.Pointer, RegSize(load.Type)) is not { } mem)
      return false;
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false)));
    return true;
  }

  /// <summary>
  /// An inline-assembly block, carried to emission with every name it mentions already paired with the
  /// cell that name denotes IN THIS FRAME.
  ///
  /// The lowering did the binding, which is what makes this possible at all: names were resolved
  /// against the semantic model, not against whichever frame layout happened to be current. Here each
  /// bound pointer becomes the machine location it addresses, so the emitter can answer the assembler
  /// without knowing anything about BASIC.
  ///
  /// It declares every register clobbered and memory both read and written. That is not a guess about
  /// what the text does - it is a refusal to guess: the allocator keeps nothing live across it and the
  /// scheduler moves nothing over it.
  ///
  /// <para>
  /// The clobber list is only half the story, though, and the other half is the descriptor's
  /// <see cref="AsmRegisterEffect"/>. "Nothing of yours survives this block" does not say "something of
  /// mine must", so a countdown set in <c>CX</c> by one <c>!</c> statement and decremented by the next
  /// one across a BASIC statement was destroyed by whatever the allocator put there in between. The
  /// text is therefore READ - by the assembler that emits it, not by a scan - for the registers it
  /// defines and consumes, and <c>LinearScanAllocator.AsmHeldByIndex</c> denies those to everyone else
  /// over exactly the stretch between the two.
  /// </para>
  ///
  /// <para>
  /// A block that writes <c>BP</c> or <c>SP</c> declines instead. Those are not values in the register
  /// file, they ARE the frame this back end laid out - every local, spill slot and parameter is
  /// addressed through <c>BP</c> - so there is no allocation that could honour such a block.
  /// </para>
  ///
  /// <para>
  /// And a block the assembler cannot ASSEMBLE declines here, which is the only place left where
  /// declining is still possible. The lowering already proved the text parses, but it proved it
  /// against its OWN stand-in symbols - a name that is neither a variable nor a label answers there as
  /// memory and at emission as the runtime label it really is, and the two disagree about what is an
  /// instruction. <c>! LEA BX, GetStrLoc</c> parses as <c>LEA BX, [BP+0]</c> and does not parse at all
  /// as <c>LEA BX, &lt;label&gt;</c>; <c>INC</c>, <c>CMP</c> and <c>XCHG</c> against a documented string
  /// export are the same shape. Each of those ENDED the compilation out of
  /// <c>MachineEmitter.EmitInlineAsm</c>, where the direct emitter reports a diagnostic and carries on.
  /// The parse therefore runs once more here, through <see cref="AsmNameKinds"/> - which answers the
  /// same KINDS the emitter's own resolver will - and the failure becomes a decline, so the direct
  /// emitter takes the function and issues exactly the diagnostic it always did.
  /// </para>
  /// </summary>
  private bool SelectInlineAsm(IrInlineAsm asm) {
    if (!asm.Routable)
      return this.Decline("inline asm: a name in it is not a variable this pass could bind");

    var kinds = new AsmNameKinds(asm);
    if (!new TextAssembler(new Assembler()).TryParse(asm.Text, kinds, out var error))
      return this.Decline($"inline asm: {error}");

    var effect = TextAssembler.Analyze(asm.Text, kinds);
    if (effect.Defines.Contains(Reg.BP) || effect.Defines.Contains(Reg.SP))
      return this.Decline("inline asm: the block writes BP or SP, which the frame is addressed through");

    var operands = new List<MOperand> { new MOperand.InlineAsmText(asm.Text, asm.Names, effect) };
    foreach (var pointer in asm.Operands) {
      if (this.AsmCell(pointer) is not { } cell)
        return false;
      operands.Add(cell);
    }

    this._current.Instructions.Add(new MInstr(MOpcode.InlineAsm, operands,
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: true, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));
    return true;
  }

  /// <summary>
  /// The frame cell an inline-asm name denotes, addressed DIRECTLY rather than through a register.
  ///
  /// <see cref="PointerMemory"/> would answer <c>[v0]</c> for a local, because an alloca whose address
  /// is taken materializes that address with an <c>LEA</c> into a virtual register. That is fatal here
  /// for a reason that has nothing to do with addressing: the block clobbers every register, so a base
  /// register live across it can go nowhere - and a value used as a memory base is precisely the one
  /// thing the spiller cannot move. The function then selected and never allocated.
  ///
  /// Naming the slot itself removes the register entirely, and is what the asm meant anyway: <c>MOV n,
  /// AX</c> names a cell, not a computed address.
  /// </summary>
  private MOperand? AsmCell(IrValue pointer) => pointer switch {
    IrAlloca alloca when this._slots.TryGetValue(alloca, out var slot)
      => new MOperand.StackSlot(slot, MRegSize.Word),
    IrGlobalVariable g when IsAddressableGlobal(g)
      => new MOperand.DataCell(g.Name, 0, MRegSize.Word),
    // a BASIC label the text jumps to. Not a cell at all - the block's own machine label, which is
    // the same thing CODEPTR32 asks for and the same operand it is answered with
    IrBlockAddress block => new MOperand.BlockOffset(block.Block.Label),
    _ => this.DeclineCell(pointer),
  };

  private MOperand? DeclineCell(IrValue pointer) {
    this.Decline($"inline asm: '{pointer.Name ?? pointer.GetType().Name}' has no frame cell to name");
    return null;
  }

  /// <summary>
  /// Answers the effect analysis' questions about identifiers the same way <c>MachineEmitter</c>'s own
  /// resolver will answer the real assembly: a name the lowering paired with a block is a code label,
  /// any other bound name is storage, and an unbound one is a runtime export - code again.
  ///
  /// The VALUE it answers with is irrelevant (nothing is emitted here), but the KIND is not:
  /// <c>JNZ [BP+0]</c> is not an instruction, so a label answered as memory fails the parse and the
  /// statement reports itself as not understood - which would cost the very register promise this
  /// analysis exists to make.
  /// </summary>
  private sealed class AsmNameKinds(IrInlineAsm asm) : IAsmSymbolResolver {

    private readonly Assembler _labels = new();

    public bool TryResolve(string name, out AsmSymbol symbol) {
      var index = IndexOf(asm.Names, name);
      var isCode = index < 0 || asm.Operands[index] is IrBlockAddress;
      symbol = isCode ? AsmSymbol.OfLabel(this._labels.Lbl(name)) : AsmSymbol.OfMemory(Mem.Word(Reg.BP, 0));
      return true;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name) {
      for (var i = 0; i < names.Count; ++i)
        if (names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
          return i;
      return -1;
    }
  }

  private bool SelectStore(IrStore store, MBlock block) {
    if (store.Value.Type.IsFloat)
      return this.Decline($"floating point: {store.Value.Type} through the scalar path");
    if (IsWide(store.Value.Type)) {
      if (this.TryNativeDwordOperand(store.Value, out var native)) {
        if (this.PointerMemory(store.Pointer, MRegSize.Dword) is not { } cell)
          return false;
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [cell, native],
          new MInstrEffect([], native is MOperand.Register ? [1] : [], false, false, false,
            WritesMemory: true)));
        return true;
      }
      if (this.PointerMemory(store.Pointer, MRegSize.Word) is not { } lowCell)
        return false;
      if (!this.TryOperandPair(store.Value, out var lo, out var hi))
        return false;
      var highCell = Shifted(lowCell, 2);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lowCell, lo],
        new MInstrEffect([], lo is MOperand.Register ? [1] : [], false, false, false, WritesMemory: true)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [highCell, hi],
        new MInstrEffect([], hi is MOperand.Register ? [1] : [], false, false, false, WritesMemory: true)));
      return true;
    }
    // Anything wider than a pair has no scalar form here: sizing the access from the value's bit
    // width would emit a single 386-prefixed access carrying half of it, which is what a QUAD store
    // silently did before the x87 path below took the signed case.
    if (store.Value.Type.Bits > 32)
      return this.Decline($"store: {store.Value.Type} is wider than a register pair");
    if (this.PointerMemory(store.Pointer, RegSize(store.Value.Type)) is not { } mem)
      return false;
    if (!this.TryOperand(store.Value, out var value))
      return false;
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [mem, value],
      new MInstrEffect(WrittenRegs: [], ReadRegs: value is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));
    return true;
  }

  private bool SelectGep(IrGep gep, MBlock block) {
    var dest = this.FreshVreg(IrType.Ptr);
    this._vregs[gep] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryGepDisplacement(gep, out var offset))
      return false;
    if (gep.BasePtr is IrGlobalVariable global)
      return this.SelectGlobalGep(global, offset, destOp);
    if (!this.TryOperand(gep.BasePtr, out var baseOp))
      return false;
    if (baseOp is not MOperand.Register baseReg)
      return this.Decline("gep: non-register base");
    // LEA dest, [base + offset]: a constant offset folds into the displacement, a register offset becomes the index
    var mem = offset switch {
      MOperand.Immediate disp => new MOperand.Memory(baseReg.Reg, null, 1, (int)disp.Value, MRegSize.Word),
      MOperand.Register index => new MOperand.Memory(baseReg.Reg, index.Reg, 1, 0, MRegSize.Word),
      _ => null,
    };
    if (mem is null)
      return this.Decline("gep: offset is neither a constant nor a register");
    this._current.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
  }
  /// <summary>
  /// The BYTE displacement a GEP adds to its base, as an immediate when it is known here and a
  /// register otherwise.
  ///
  /// <para>
  /// A byte-offset GEP already carries bytes and this only reshapes it. An ELEMENT-indexed one
  /// carries an index that has to be multiplied by the element's stride, and the stride is this
  /// target's answer rather than the IR's: a <c>ptr</c> has no width in the IR at all (it is a target
  /// property), which is exactly why the lowering emits a typed GEP for a string array instead of
  /// pre-multiplying by a size it cannot know. Here it is two bytes, a near offset.
  /// </para>
  ///
  /// <para>
  /// A constant index is folded into the displacement and costs nothing. A runtime index is scaled
  /// into a temporary of its own rather than in place, because the index is an SSA value whose other
  /// uses still want it unscaled - <c>SHL</c> when the stride is a power of two, which every scalar
  /// and pointer element is, and <c>IMUL</c> for a record stride that is not. The 8086's
  /// <c>[base+index]</c> has no scale factor, so the shift is not an optimization but the only form
  /// there is.
  /// </para>
  /// </summary>
  private bool TryGepDisplacement(IrGep gep, out MOperand offset) {
    offset = null!;
    var stride = gep.ElementType is { } element ? SizeOf(element) : 1;

    if (gep.ByteOffset is IrConstantInt constant) {
      var bytes = constant.Value * stride;
      if (bytes is < int.MinValue or > int.MaxValue)
        return this.Decline($"gep: constant byte offset {bytes} does not fit the data displacement");
      offset = new MOperand.Immediate(bytes);
      return true;
    }

    if (!this.TryOperand(gep.ByteOffset, out var index))
      return false;
    if (index is not MOperand.Register indexReg)
      return this.Decline("gep: offset is neither a constant nor a register");
    if (stride == 1) {
      offset = indexReg;
      return true;
    }

    var scaled = new MOperand.Register(this.FreshVreg(IrType.I16));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [scaled, indexReg], MovEffect(scaled, indexReg)));
    if ((stride & (stride - 1)) == 0) {
      // through SelectConstantShift rather than a bare immediate: SHL r, 2 for a 4-byte element and
      // SHL r, 3 for an 8-byte one are 80186 encodings, and an array of LONG is as ordinary as a
      // program gets - so under $CPU 8086 this was the commonest way to emit an instruction the
      // declared part does not have
      if (!this.SelectConstantShift(MOpcode.Shl, scaled,
            System.Numerics.BitOperations.TrailingZeroCount(stride), IrType.I16))
        return false;
    } else {
      // the two-operand IMUL has no immediate form on this target, as SelectBinary already found
      var multiplier = new MOperand.Register(this.FreshVreg(IrType.I16));
      var strideOp = new MOperand.Immediate(stride);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [multiplier, strideOp], MovEffect(multiplier, strideOp)));
      this._current.Instructions.Add(new MInstr(MOpcode.Imul, [scaled, multiplier],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false)));
    }
    offset = scaled;
    return true;
  }

  /// <summary>
  /// Forms an address inside a module/static data object. A label is an immediate address on the
  /// 8086, not an SSA register: materialize its OFFSET first, then add a runtime byte offset when the
  /// index was not constant. The whole-program bridge resolves the name to the direct emitter's cell.
  /// </summary>
  private bool SelectGlobalGep(IrGlobalVariable global, MOperand offset, MOperand.Register dest) {
    if (!IsAddressableGlobal(global))
      return this.Decline($"gep: global '{global.Name}' has no addressable data cell");

    if (offset is MOperand.Immediate constant) {
      var address = new MOperand.DataOffset(global.Name, (int)constant.Value);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, address], MovEffect(dest, address)));
      return true;
    }

    if (offset is not MOperand.Register index)
      return this.Decline("gep: global offset is not a register");
    var baseAddress = new MOperand.DataOffset(global.Name, 0);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, baseAddress], MovEffect(dest, baseAddress)));
    this._current.Instructions.Add(new MInstr(MOpcode.Add, [dest, index],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [0, 1], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    return true;
  }

  /// <summary>
  /// A comparison whose RESULT is used - assigned, combined, passed - rather than folded into a
  /// branch. BASIC's truth value is -1/0 (not 1/0), and the 8086 has no <c>SETcc</c>, so the value is
  /// materialized by branching around it:
  /// <code>
  ///   CMP lhs, rhs
  ///   MOV dest, -1
  ///   Jcc  done          ; predicate holds - keep -1
  ///   MOV dest, 0
  /// done:
  /// </code>
  /// <c>MOV</c> does not disturb flags, so it may sit between the compare and the branch. This splits
  /// the machine block in three, which is why appends go through the block cursor and the phi copies
  /// for this IR block land in whichever machine block control finally leaves from.
  /// </summary>
  /// <summary>
  /// The three conditions a 32-bit comparison decomposes into: after comparing the HIGH words, one
  /// branch settles it true, another settles it false, and if neither fires the words were equal and
  /// the LOW words decide - always UNSIGNED, whatever the predicate's signedness, because the sign
  /// lives entirely in the high half.
  ///
  /// <c>Eq</c> and <c>Ne</c> have no ordering, so each leaves one of the high branches unused: equality
  /// can only be refuted by the high words, inequality can only be confirmed by them.
  /// </summary>
  private static (Condition? HighTrue, Condition? HighFalse, Condition Low)? WideConditions(IrCmpPred pred) => pred switch {
    IrCmpPred.Eq => (null, Condition.NotEqual, Condition.Equal),
    IrCmpPred.Ne => (Condition.NotEqual, null, Condition.NotEqual),
    IrCmpPred.Slt => (Condition.Less, Condition.Greater, Condition.Below),
    IrCmpPred.Sle => (Condition.Less, Condition.Greater, Condition.BelowOrEqual),
    IrCmpPred.Sgt => (Condition.Greater, Condition.Less, Condition.Above),
    IrCmpPred.Sge => (Condition.Greater, Condition.Less, Condition.AboveOrEqual),
    IrCmpPred.Ult => (Condition.Below, Condition.Above, Condition.Below),
    IrCmpPred.Ule => (Condition.Below, Condition.Above, Condition.BelowOrEqual),
    IrCmpPred.Ugt => (Condition.Above, Condition.Below, Condition.Above),
    IrCmpPred.Uge => (Condition.Above, Condition.Below, Condition.AboveOrEqual),
    _ => null,
  };

  /// <summary>
  /// A 32-bit comparison materialized as PowerBASIC's -1/0 truth value. An eligible 386-resident value
  /// uses one native CMP; the baseline pair becomes a compare of the high words, then - only when those
  /// are equal - a compare of the low ones:
  /// <code>
  ///   CMP hiL, hiR
  ///   Jcc  true          ; the high words already settle it
  ///   Jcc  false
  ///   CMP loL, loR       ; equal so far: the low words decide, unsigned
  ///   Jcc  true
  /// false: MOV dest, 0 ; JMP done
  /// true:  MOV dest, -1
  /// done:
  /// </code>
  /// The low compare is unsigned for every predicate including the signed ones, because a signed
  /// 32-bit order is decided by the high half and the low half is only a magnitude.
  /// </summary>
  private bool SelectWideCmpValue(IrCmp cmp) {
    if (this.UsesNativeDwordRegisters
        && MapPredicate(this.PredicateOf(cmp)) is { } nativeCondition
        && this.TryNativeDwordOperand(cmp.Lhs, out var nativeLhs)
        && this.TryNativeDwordOperand(cmp.Rhs, out var nativeRhs)
        && nativeLhs is MOperand.Register) {
      this.EmitCompare(nativeLhs, nativeRhs);
      return this.MaterializeCondition(cmp, nativeCondition);
    }
    if (WideConditions(this.PredicateOf(cmp)) is not { } conditions)
      return this.Decline($"compare as a value: 32-bit {cmp.Pred}");
    if (!this.TryOperandPair(cmp.Lhs, out var lhsLo, out var lhsHi)
        || !this.TryOperandPair(cmp.Rhs, out var rhsLo, out var rhsHi))
      return false;
    if (lhsHi is not MOperand.Register || lhsLo is not MOperand.Register)
      return this.Decline("compare as a value: 32-bit with an immediate left operand");

    var dest = MReg.Virtual(this._nextVreg++, MRegSize.Word);
    this._vregs[cmp] = dest;
    var destOp = new MOperand.Register(dest);

    var lowBlock = new MBlock($"{this._current.Label}.cmplo{this._splitCount}");
    var trueBlock = new MBlock($"{this._current.Label}.cmptrue{this._splitCount}");
    var falseBlock = new MBlock($"{this._current.Label}.cmpfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.cmpdone{this._splitCount}");
    ++this._splitCount;

    this.EmitCompare(lhsHi, rhsHi);
    if (conditions.HighTrue is { } highTrue) {
      this.EmitBranch(highTrue, trueBlock.Label);
      this._current.Successors.Add(trueBlock.Label);
    }
    if (conditions.HighFalse is { } highFalse) {
      this.EmitBranch(highFalse, falseBlock.Label);
      this._current.Successors.Add(falseBlock.Label);
    }
    this._current.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(lowBlock.Label)], MInstrEffect.None));
    this._current.Successors.Add(lowBlock.Label);

    this._current = lowBlock;
    this.EmitCompare(lhsLo, rhsLo);
    this.EmitBranch(conditions.Low, trueBlock.Label);
    lowBlock.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(falseBlock.Label)], MInstrEffect.None));
    lowBlock.Successors.Add(trueBlock.Label);
    lowBlock.Successors.Add(falseBlock.Label);

    var minusOne = new MOperand.Immediate(-1);
    trueBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, minusOne], MovEffect(destOp, minusOne)));
    trueBlock.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(doneBlock.Label)], MInstrEffect.None));
    trueBlock.Successors.Add(doneBlock.Label);

    var zero = new MOperand.Immediate(0);
    falseBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, zero], MovEffect(destOp, zero)));
    falseBlock.Successors.Add(doneBlock.Label);

    this._function.Blocks.Add(lowBlock);
    this._function.Blocks.Add(trueBlock);
    this._function.Blocks.Add(falseBlock);
    this._function.Blocks.Add(doneBlock);
    this._current = doneBlock;
    return true;
  }

  private void EmitCompare(MOperand left, MOperand right)
    => this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [left, right],
      new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(left, right), ReadsFlags: false, WritesFlags: true,
        ReadsMemory: left.IsMemoryAccess() || right.IsMemoryAccess(), WritesMemory: false)));

  private void EmitBranch(Condition condition, string target)
    => this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(target)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false), condition));

  private bool SelectCmpValue(IrCmp cmp) {
    if (cmp.Lhs.Type.IsFloat)
      return this.SelectFloatCmpValue(cmp);
    if (IsWide(cmp.Lhs.Type))
      return this.SelectWideCmpValue(cmp);
    var pred = this.PredicateOf(cmp);
    if (MapPredicate(pred) is not { } cc)
      return this.Decline($"compare as a value: {pred}");
    if (!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, out var rhs))
      return false;
    // CMP wants a register on the left. The same two answers the BRANCH path already gives: mirror
    // the predicate onto the other operand when THAT one is a register (`5 > x` is `x < 5`, and the
    // mirror is not the negation), and otherwise move the left side into one. Declining here was the
    // last thing keeping DIFF14 off the back end.
    if (lhs is not MOperand.Register) {
      if (rhs is MOperand.Register) {
        (lhs, rhs) = (rhs, lhs);
        cc = MapPredicate(Mirrored(pred))!.Value;
      } else {
        var held = this.FreshVreg(cmp.Lhs.Type);
        var into = new MOperand.Register(held);
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [into, lhs], MovEffect(into, lhs)));
        lhs = into;
      }
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
      new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
        ReadsMemory: lhs.IsMemoryAccess() || rhs.IsMemoryAccess(), WritesMemory: false)));
    return this.MaterializeCondition(cmp, cc);
  }

  private bool SelectFloatCmpValue(IrCmp cmp) {
    if (MapFloatPredicate(cmp.Pred) is not { } cc)
      return this.Decline($"compare as a value: float {cmp.Pred}");
    if (this.TrySelectFloatMemoryCompare(cmp, cc))
      return true;
    if (!this.TryFloatOperand(cmp.Lhs, out var lhs) || !this.TryFloatOperand(cmp.Rhs, out var rhs))
      return false;

    // FLD left; FLD right leaves the right operand on top. FCOMPP compares ST(0) against ST(1), so
    // FXCH restores source order. FSTSW AX + SAHF maps x87 C0/C3 to the unsigned CF/ZF conditions.
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX));
    this.EmitX87(MOpcode.Fld, lhs, reads: true);
    this.EmitX87(MOpcode.Fld, rhs, reads: true);
    this._current.Instructions.Add(new MInstr(MOpcode.Fxch, [], MInstrEffect.None));
    this._current.Instructions.Add(new MInstr(MOpcode.Fcompp, [], MInstrEffect.None));
    this._current.Instructions.Add(new MInstr(MOpcode.FstswAx, [ax],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: false, WritesMemory: false), clobbers: [Reg.AX]));
    this._current.Instructions.Add(new MInstr(MOpcode.Sahf, [ax],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    return this.MaterializeCondition(cmp, cc);
  }

  private bool MaterializeCondition(IrCmp cmp, Condition cc) {
    // the result is i1 in the IR, but it is materialized in a word register - a later sext to i16
    // finds it already sign-extended, which is exactly what -1/0 means
    var dest = MReg.Virtual(this._nextVreg++, MRegSize.Word);
    this._vregs[cmp] = dest;
    var destOp = new MOperand.Register(dest);

    var falseBlock = new MBlock($"{this._current.Label}.cmpfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.cmpdone{this._splitCount}");
    ++this._splitCount;

    var minusOne = new MOperand.Immediate(-1);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, minusOne], MovEffect(destOp, minusOne)));
    this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(doneBlock.Label)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false), cc));
    this._current.Successors.Add(doneBlock.Label);
    this._current.Successors.Add(falseBlock.Label);

    var zero = new MOperand.Immediate(0);
    falseBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, zero], MovEffect(destOp, zero)));
    falseBlock.Successors.Add(doneBlock.Label);

    this._function.Blocks.Add(falseBlock);
    this._function.Blocks.Add(doneBlock);
    this._current = doneBlock;                 // selection continues after the diamond
    return true;
  }

  /// <summary>
  /// A <c>select</c> - what the IR's if-conversion pass leaves where the source had
  /// <c>IF c THEN x = a ELSE x = b</c>. There is no <c>CMOV</c> before the Pentium Pro, so on this
  /// target it goes back to a branch over the two values:
  /// <code>
  ///   CMP cond, 0
  ///   MOV dest, ifTrue
  ///   Jne  done
  ///   MOV dest, ifFalse
  /// done:
  /// </code>
  /// Both arms are plain values by construction (the pass only forms a select from an empty diamond),
  /// so nothing is evaluated that the original would not have evaluated.
  /// </summary>
  private bool SelectSelect(IrSelect sel) {
    if (sel.Type.IsFloat)          // MBF never gets this far - RefusesMbf turns it away at the dispatch
      return this.SelectFloatSelect(sel);
    var wide = IsWide(sel.Type);
    // the min/max canonicalization may have inverted the predicate this select's condition emits, in
    // which case the arms go with it the other way round (see InstructionSelector.Idioms)
    var (whenTrue, whenFalse) = this.HasSwappedArms(sel)
      ? (sel.IfFalse, sel.IfTrue)
      : (sel.IfTrue, sel.IfFalse);
    MOperand ifTrue, ifFalse, ifTrueHi = null!, ifFalseHi = null!;
    if (wide) {
      if (!this.TryOperandPair(whenTrue, out ifTrue, out ifTrueHi)
          || !this.TryOperandPair(whenFalse, out ifFalse, out ifFalseHi))
        return false;
    } else if (!this.TryOperand(whenTrue, out ifTrue) || !this.TryOperand(whenFalse, out ifFalse)) {
      return false;
    }
    if (!this.TryOperand(sel.Condition, out var cond))
      return false;
    if (cond is not MOperand.Register)
      return this.Decline("select: condition is not in a register");

    // A 32-bit result is a register PAIR, so each arm moves twice - the diamond is the same shape,
    // and both halves have to be written on both paths or the untouched one keeps whatever the
    // other arm left in it.
    MOperand.Register destHi = null!;
    MOperand.Register destOp;
    if (wide) {
      var (lo, hi) = this.FreshPair(sel);
      destOp = lo;
      destHi = hi;
    } else {
      var dest = this.FreshVreg(sel.Type);
      this._vregs[sel] = dest;
      destOp = new MOperand.Register(dest);
    }

    var falseBlock = new MBlock($"{this._current.Label}.selfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.seldone{this._splitCount}");
    ++this._splitCount;

    var zero = new MOperand.Immediate(0);
    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [cond, zero],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ifTrue], MovEffect(destOp, ifTrue)));
    if (wide)
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, ifTrueHi], MovEffect(destHi, ifTrueHi)));
    this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(doneBlock.Label)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      Condition.NotEqual));
    this._current.Successors.Add(doneBlock.Label);
    this._current.Successors.Add(falseBlock.Label);

    falseBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ifFalse], MovEffect(destOp, ifFalse)));
    if (wide)
      falseBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, ifFalseHi], MovEffect(destHi, ifFalseHi)));
    falseBlock.Successors.Add(doneBlock.Label);

    this._function.Blocks.Add(falseBlock);
    this._function.Blocks.Add(doneBlock);
    this._current = doneBlock;
    return true;
  }

  /// <summary>
  /// The same diamond for a float result, with the one difference that decides the whole shape: a
  /// float on this target never lives in a register, so each arm is a load-and-store through the x87
  /// into the select's own frame cell rather than a <c>MOV</c> into its virtual register.
  ///
  /// <para>
  /// The cell is taken ONCE, before either arm, so both arms write the same slot - the mistake the
  /// wide integer arm's comment already warns about, where a half written on one path only keeps
  /// whatever the other path left. And <c>FLD</c>/<c>FSTP</c> leave the CPU flags alone (the x87 has
  /// a status word of its own), which is what lets the compare stay in front of the true arm and the
  /// conditional jump behind it, exactly as the integer form does.
  /// </para>
  ///
  /// <para>
  /// The IR reaches here from <c>MAX</c>/<c>MIN</c> and any other empty diamond <c>IfConversion</c>
  /// folds, with the result still at the width PB computed it. Optimized, the integer recovery pass
  /// usually turns the pair back into an integer select first, which is why this only showed as a
  /// decline with <c>--no-optimize</c> - selection is not allowed to depend on that.
  /// </para>
  /// </summary>
  private bool SelectFloatSelect(IrSelect sel) {
    var (whenTrue, whenFalse) = this.HasSwappedArms(sel)
      ? (sel.IfFalse, sel.IfTrue)
      : (sel.IfTrue, sel.IfFalse);
    if (!this.TryFloatOperand(whenTrue, out var ifTrue) || !this.TryFloatOperand(whenFalse, out var ifFalse))
      return false;
    if (!this.TryOperand(sel.Condition, out var cond))
      return false;
    if (cond is not MOperand.Register)
      return this.Decline("select: condition is not in a register");

    var destination = this.FloatCell(sel);
    var falseBlock = new MBlock($"{this._current.Label}.selfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.seldone{this._splitCount}");
    ++this._splitCount;

    var zero = new MOperand.Immediate(0);
    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [cond, zero],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    this.EmitX87(MOpcode.Fld, ifTrue, reads: true);
    this.EmitX87(MOpcode.Fstp, destination, reads: false);
    this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(doneBlock.Label)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      Condition.NotEqual));
    this._current.Successors.Add(doneBlock.Label);
    this._current.Successors.Add(falseBlock.Label);

    this._current = falseBlock;
    this.EmitX87(MOpcode.Fld, ifFalse, reads: true);
    this.EmitX87(MOpcode.Fstp, destination, reads: false);
    falseBlock.Successors.Add(doneBlock.Label);

    this._function.Blocks.Add(falseBlock);
    this._function.Blocks.Add(doneBlock);
    this._current = doneBlock;
    return true;
  }

  /// <summary>
  /// Width changes between the two integer sizes this target has registers for. Widening a word to a
  /// pair sets the high half from the source's sign (<c>SAR 15</c> smears the sign bit across it) or
  /// from zero; narrowing a pair to a word is just its low half, which is already a register of its
  /// own - so a truncation costs no instruction at all.
  /// </summary>
  private bool SelectCast(IrCast cast, MBlock block) {
    var from = cast.Value.Type;
    var to = cast.Type;
    switch (cast.Op) {
      // BASIC's comparison result is already -1/0 in a full word, so widening it to i16 is nothing
      case IrCastOp.SExt when from.IsBool && to.IsInteger && to.Bits == 16 && this._vregs.TryGetValue(cast.Value, out var truth): {
        this._vregs[cast] = truth;
        return true;
      }
      // BASIC truth is a FULL WORD of -1 or 0, so widening a bool to a number is not a copy: the
      // value wanted is 1 or 0. Masking the low bit is what turns one into the other, and it is the
      // reason this cannot share the integer widening below - that one would produce -1.
      case IrCastOp.ZExt when from.IsBool && to.IsInteger && to.Bits is 16 or 32: {
        if (!this.TryOperand(cast.Value, out var truth))
          return false;
        var one = new MOperand.Immediate(1);
        if (IsWide(to)) {
          var (low, high) = this.FreshPair(cast);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [low, truth], MovEffect(low, truth)));
          this._current.Instructions.Add(new MInstr(MOpcode.And, [low, one],
            new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
              ReadsMemory: false, WritesMemory: false)));
          var zero = new MOperand.Immediate(0);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, zero], MovEffect(high, zero)));
          return true;
        }
        var narrow = this.FreshVreg(cast.Type);
        var dest = new MOperand.Register(narrow);
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, truth], MovEffect(dest, truth)));
        this._current.Instructions.Add(new MInstr(MOpcode.And, [dest, one],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
            ReadsMemory: false, WritesMemory: false)));
        this._vregs[cast] = narrow;
        return true;
      }
      case IrCastOp.SExt or IrCastOp.ZExt when IsWide(to) && from.IsInteger && from.Bits == 16: {
        if (!this.TryOperand(cast.Value, out var source))
          return false;
        var (lo, hi) = this.FreshPair(cast);
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, source], MovEffect(lo, source)));
        if (cast.Op == IrCastOp.ZExt) {
          var zero = new MOperand.Immediate(0);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, zero], MovEffect(hi, zero)));
          return true;
        }
        // sign-extend: copy the value and smear its sign bit over the whole high word
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, source], MovEffect(hi, source)));
        this.EmitSignSmear(hi);
        return true;
      }
      case IrCastOp.SExt or IrCastOp.ZExt when IsQuad(to) && IsWide(from):
        return this.SelectWideToQword(cast);
      // A BYTE is the low half of the word already holding the value, so narrowing to one is a
      // change of VIEW rather than of content: the same virtual register, named at byte width. That
      // is the same reinterpretation the runtime-call staging does when it needs AL out of AX, and
      // it is why no masking instruction is emitted - nothing above the low eight bits is readable
      // through a byte-sized name.
      case IrCastOp.Trunc when from.IsInteger && from.Bits == 16 && to.IsInteger && to.Bits <= 8: {
        if (!this.TryOperand(cast.Value, out var source))
          return false;
        if (source is MOperand.Register word) {
          this._vregs[cast] = word.Reg with { Size = MRegSize.Byte };
          return true;
        }
        // a constant has no register to rename, so it is moved into one at byte width - the move is
        // what the narrowing costs when the value did not arrive in a register to begin with
        var narrowed = this.FreshVreg(cast.Type);
        var dest = new MOperand.Register(narrowed);
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source)));
        this._vregs[cast] = narrowed;
        return true;
      }
      // Narrowing a CONSTANT is arithmetic the compiler can do itself, whatever the widths. It
      // normally never reaches here - IrConstFold folds it - but a function with an armed error
      // handler is skipped by the whole optimizer, because a raise can enter it where the CFG shows
      // no edge. So the one place a folded constant is guaranteed NOT to have been folded is exactly
      // the place that has to select it.
      case IrCastOp.Trunc when cast.Value is IrConstantInt constant && to.IsInteger && to.Bits is 16 or 32: {
        var wrapped = to.Bits == 16 ? (short)constant.Value : (int)constant.Value;
        if (!IsWide(to)) {
          var narrow = this.FreshVreg(to);
          var dest = new MOperand.Register(narrow);
          var value = new MOperand.Immediate((short)wrapped);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, value], MovEffect(dest, value)));
          this._vregs[cast] = narrow;
          return true;
        }
        var (low, high) = this.FreshPair(cast);
        var lowHalf = new MOperand.Immediate((short)wrapped);
        var highHalf = new MOperand.Immediate((short)(wrapped >> 16));
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [low, lowHalf], MovEffect(low, lowHalf)));
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, highHalf], MovEffect(high, highHalf)));
        return true;
      }
      // A near pointer on this target IS its offset, so reading one as a number changes nothing about
      // the bits - only what they are called. That makes this the same change of VIEW the byte
      // truncation above performs: the register already holding the address is renamed, and the cast
      // costs no instruction. A frame cell reaches here having had its LEA emitted by its own alloca.
      //
      // A module-level or STATIC variable is a data LABEL rather than a register, so there is nothing
      // to rename and its offset is materialized the way an indexed access into it already is.
      case IrCastOp.PtrToInt when to.IsInteger && to.Bits == 16: {
        // CODEPTR of a label: a point in this function's own code, which is the one address no
        // instruction produces and only the assembler knows. MOperand.BlockOffset already names it -
        // it is how ON ERROR arms a handler - so this is that operand moved into a register.
        if (cast.Value is IrBlockAddress blockAddress) {
          var labelReg = this.FreshVreg(to);
          var labelDest = new MOperand.Register(labelReg);
          var here = new MOperand.BlockOffset(blockAddress.Block.Label);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [labelDest, here], MovEffect(labelDest, here)));
          this._vregs[cast] = labelReg;
          return true;
        }
        if (cast.Value is IrGlobalVariable global) {
          if (!IsAddressableGlobal(global))
            return this.Decline($"ptrtoint: global '{global.Name}' has no addressable data cell");
          var offsetReg = this.FreshVreg(to);
          var offsetDest = new MOperand.Register(offsetReg);
          var address = new MOperand.DataOffset(global.Name, 0);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [offsetDest, address], MovEffect(offsetDest, address)));
          this._vregs[cast] = offsetReg;
          return true;
        }
        if (!this.TryOperand(cast.Value, out var pointer))
          return false;
        if (pointer is not MOperand.Register held)
          return this.Decline("ptrtoint: the address is not in a register");
        this._vregs[cast] = held.Reg with { Size = MRegSize.Word };
        return true;
      }
      // ...and the same rename read the other way: a word becomes an address. GOTO DWORD's target is
      // the low half of a PB code pointer, and on a near target that half IS the address.
      case IrCastOp.IntToPtr when from.IsInteger && from.Bits == 16: {
        if (!this.TryOperand(cast.Value, out var word))
          return false;
        if (word is not MOperand.Register number)
          return this.Decline("inttoptr: the value is not in a register");
        this._vregs[cast] = number.Reg with { Size = MRegSize.Word };
        return true;
      }
      case IrCastOp.Trunc when IsWide(from) && to.IsInteger && to.Bits == 16: {
        if (!this.TryOperandPair(cast.Value, out var lo, out _))
          return false;
        if (lo is not MOperand.Register low)
          return this.Decline("cast: truncation of a constant pair");
        this._vregs[cast] = low.Reg;      // the low half IS the narrowed value - no instruction needed
        return true;
      }
      // ...and the FPToSI half emits NOTHING. Selection walks instructions in order, so it reaches
      // the truncation first and would decline there before the pair was ever recognised; the work
      // happens when its consumer is selected, which is the only point at which both halves are known.
      case IrCastOp.FPToSI when to.IsInteger && to.Bits == 64 && from.IsIeeeFloat
          && cast.Users is [IrCast { Op: IrCastOp.SIToFP, Type.IsIeeeFloat: true }]:
        return true;
      // ...and when the i64 is a value the program KEEPS - a FIX cell is a scaled int64, so the
      // conversion ends in storage rather than in a matching SIToFP - it becomes a qword cell of its
      // own. The truncation still goes through rt_trunc for the reason the pair below does: FISTP
      // rounds by the control word, and FPToSI means toward zero everywhere else in this compiler.
      case IrCastOp.FPToSI when IsQuad(to) && from.IsIeeeFloat:
        return this.SelectTruncationToQword(cast);
      // FIX and INT truncate a float toward zero by going through a 64-BIT integer, and the round
      // trip is the whole operation - the i64 is never a value the program can see, only the shape
      // the truncation takes. Selected as a pair it becomes the rt_trunc the direct emitter calls;
      // selected apart, the intermediate would need a four-register integer this back end does not
      // have, and declines.
      case IrCastOp.SIToFP when to.IsIeeeFloat
          && cast.Value is IrCast { Op: IrCastOp.FPToSI, Type: { IsInteger: true, Bits: 64 } } inner
          && inner.Value.Type.IsIeeeFloat && inner.Users.Count == 1:
        return this.SelectTruncationTowardZero(inner, cast);
      // ...unless every consumer is a floating operation that can read the INTEGER out of memory, in
      // which case the conversion is the operation's own and all this leaves behind is the cell to
      // read it from (see InstructionSelector.Idioms)
      case IrCastOp.SIToFP when to.IsIeeeFloat && this.StagesAsInteger(cast):
        return true;
      case IrCastOp.SIToFP when to.IsIeeeFloat:
        return this.SelectIntToFloat(cast);
      // The same routine: it stages an unsigned source one size larger with the extra half zeroed,
      // which is what makes FILD's signed read give the unsigned value back.
      case IrCastOp.UIToFP when to.IsIeeeFloat:
        return this.SelectIntToFloat(cast);
      case IrCastOp.FPToSIRound when from.IsIeeeFloat && to.IsInteger && to.Bits is 16 or 32:
        return this.SelectFloatToInt(cast);
      // The x87 stores only SIGNED integers, so an unsigned target is staged one size larger than
      // itself: a WORD's 65535 does not fit a signed word but fits a signed dword, and a DWORD's
      // 4294967295 needs the qword store. The bits that come back are the value either way.
      //
      // The ROUNDING opcode, and only it. FISTP rounds, so this sequence never was the truncating
      // FPToUI it used to be matched on - it is the unsigned twin of SelectFloatToInt, and a plain
      // FPToUI now declines rather than being answered with a conversion that rounds.
      case IrCastOp.FPToUIRound when from.IsIeeeFloat && to.IsInteger && to.Bits is 8 or 16 or 32:
        return this.SelectFloatToUnsigned(cast);
      case IrCastOp.FPExt or IrCastOp.FPTrunc when from.IsIeeeFloat && to.IsIeeeFloat:
        return this.SelectFloatResize(cast);
      default:
        return this.Decline($"cast: {cast.Op} {from} -> {to}");
    }
  }

  /// <summary>
  /// A direct near call using the convention recorded on <see cref="IrCall"/>. BASIC/PASCAL push
  /// argument groups left to right and let the callee clean; CDECL/STDCALL push them right to left,
  /// with CDECL alone restoring SP in the caller. Register conventions remain an explicit decline
  /// until their argument staging is represented in machine IR. Integer results arrive in AX or
  /// DX:AX and IEEE results in ST(0).
  ///
  /// The call is marked as clobbering every allocatable register, which is the truth on this ABI
  /// (a callee owns AX-DX as scratch and may use SI/DI for loop residency without saving them).
  /// Allocation therefore refuses to keep any value in a register across the call, and - having no
  /// spilling yet - declines such a function rather than miscompiling it.
  /// </summary>
  /// <summary>
  /// Microsoft Binary Format never reaches an instruction here. The x87 cannot compute on those bits
  /// - they are a storage encoding with a different exponent bias and layout - so a value carrying
  /// one has to be converted on load and back on store, which this back end does not emit. The IR now
  /// CARRIES the format rather than refusing to lower it, which makes checking for it the back end's
  /// job; treating mbf32 as f32 would read a different number.
  /// </summary>
  private bool RefusesMbf(IrInstruction instruction) {
    if (instruction.Type.IsMbf)
      return this.Decline($"Microsoft Binary Format ({instruction.Type}) needs the MBF/IEEE load-store conversion");
    foreach (var operand in instruction.Operands)
      if (operand.Type.IsMbf)
        return this.Decline($"an operand in Microsoft Binary Format ({operand.Type})");
    return false;
  }

  /// <summary>
  /// Whether a declaration names a RUNTIME routine rather than an external user procedure. The IR
  /// spells the runtime <c>rt_*</c> and the LLVM intrinsics <c>llvm.*</c>; a DECLARE in the source
  /// keeps the programmer's own name, and nothing else is reserved.
  /// </summary>
  private static bool IsRuntimeName(string name)
    => name.StartsWith("rt_", StringComparison.Ordinal) || name.StartsWith("llvm.", StringComparison.Ordinal);

  private bool SelectCall(IrCall call, MBlock block) {
    if (call.Callee is not IrFunction callee)
      return this.Decline("call: indirect (through a procedure pointer)");
    // A declaration is one of two very different things. A RUNTIME routine has a hand-written body
    // with a register convention, and reaching it needs an entry in the ABI table - anything not
    // listed declines, which is the signal the coverage census reads. An EXTERNAL user procedure has
    // no body HERE but a source-declared ABI, supplied by another object file and resolved by the
    // linker; its IrCall convention chooses the ordinary stack-call path below.
    if (callee.IsDeclaration) {
      if (NonLocalJumpIntrinsics.Contains(callee.Name))
        return this.SelectNonLocalJumpIntrinsic(call, callee);
      if (MathSequence(callee.Name, this._target.Cpu386) is { } sequence)
        return this.SelectMathIntrinsic(call, callee, sequence);
      if (callee.Name == "rt_str_concat_n")
        return this.SelectMultiConcat(call);
      if (RuntimeAbi.For(callee.Name) is { } routine)
        return this.SelectRuntimeCall(call, callee, routine);
      if (IsRuntimeName(callee.Name))
        return this.Decline($"call: {callee.Name} (runtime declaration - not in the runtime ABI table)");
    }
    if (!call.Type.IsVoid && !call.Type.IsIeeeFloat && !IsWide(call.Type)
        && RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: {callee.Name} returns {call.Type} (unsupported result shape)");

    var abi = X86CallAbi.For(call.Convention);
    if (abi.Distance != X86CallDistance.Near)
      return this.Decline($"call: {callee.Name} uses a far return address");
    if (abi.ArgumentRegisters.Count > 0)
      return this.Decline($"call: {callee.Name} uses {call.Convention} register arguments");

    var arguments = abi.StackArgumentOrder == X86StackArgumentOrder.RightToLeft
      ? call.Args.Reverse()
      : call.Args;
    var stackBytes = 0;
    foreach (var arg in arguments) {
      if (!this.PushStackCallArgument(arg, callee.Name, out var argumentBytes))
        return false;
      stackBytes += argumentBytes;
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(callee.Name)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));

    if (abi.StackCleanup == X86StackCleanup.Caller && stackBytes > 0) {
      var sp = new MOperand.Register(MReg.Physical_(Reg.SP, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Add,
        [sp, new MOperand.Immediate(stackBytes)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false),
        condition: null, clobbers: [Reg.SP]));
    }

    if (call.Type.IsVoid)
      return true;

    if (call.Type.IsIeeeFloat) {
      // The BASIC function ABI returns every IEEE real on ST(0); park it immediately so the x87
      // stack is empty again at the instruction boundary.
      this.EmitX87(MOpcode.Fstp, this.FloatCell(call), reads: false);
      return true;
    }

    if (IsWide(call.Type)) {
      // a 32-bit result comes back in DX:AX, the convention the direct codegen documents
      var (lo, hi) = this.FreshPair(call);
      var axResult = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
      var dxResult = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, axResult], MovEffect(lo, axResult)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, dxResult], MovEffect(hi, dxResult)));
      return true;
    }

    // the result is in AX; copy it into the call's own virtual register so the allocator may place
    // the value anywhere (the copy is free when it lands in AX again)
    var dest = this.FreshVreg(call.Type);
    this._vregs[call] = dest;
    var destOp = new MOperand.Register(dest);
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, RegSize(call.Type)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ax], MovEffect(destOp, ax)));
    return true;
  }

  private bool PushStackCallArgument(IrValue argument, string calleeName, out int bytes) {
    bytes = 0;
    if (argument.Type.IsIeeeFloat) {
      if (!this.TryFloatOperand(argument, out var source))
        return false;
      bytes = argument.Type.Bits / 8;
      if (bytes is not (4 or 8))
        return this.Decline($"call: {calleeName} takes {argument.Type} (only SINGLE/DOUBLE arguments)");
      // Intermediates live in 80-bit cells. Storing to the parameter's declared width is both the
      // ABI representation and its required rounding boundary; pushing words from the TBYTE cell
      // itself would pass the x87 encoding as though it were IEEE bits.
      var staged = this._function.StackSlots.Count;
      this._function.StackSlots.Add(bytes);
      this.EmitX87(MOpcode.Fld, source, reads: true);
      this.EmitX87(MOpcode.Fstp, new MOperand.StackSlot(staged, RegSize(argument.Type)), reads: false);
      for (var offset = bytes - 2; offset >= 0; offset -= 2)
        this._current.Instructions.Add(PushOf(
          new MOperand.StackSlot(staged, MRegSize.Word, offset)));
      return true;
    }
    if (IsWide(argument.Type)) {
      // A 32-bit argument occupies two stack words, and the callee reads its LOW half at the
      // parameter's own offset - the stack grows down, so the high half is pushed first.
      if (!this.TryOperandPair(argument, out var argumentLo, out var argumentHi))
        return false;
      this._current.Instructions.Add(PushOf(argumentHi));
      this._current.Instructions.Add(PushOf(argumentLo));
      bytes = 4;
      return true;
    }
    if (RegSize(argument.Type) != MRegSize.Word)
      return this.Decline($"call: {calleeName} takes {argument.Type} (word arguments only)");
    if (!this.TryOperand(argument, out var pushed))
      return false;
    this._current.Instructions.Add(PushOf(pushed));
    bytes = 2;
    return true;
  }

  /// <summary>
  /// A call to a runtime routine, in the DOS runtime's own register convention rather than the
  /// stack one (<see cref="RuntimeAbi"/>): each IR argument is moved into the register that routine
  /// reads it from, then <c>CALL rt_...</c>, and nothing is cleaned because nothing was pushed.
  ///
  /// Every argument MOV declares the register it writes as a clobber, because no allocated value
  /// names it - that is what stops the allocator from parking a live value (or the argument's own
  /// source) in a register the sequence is about to overwrite.
  /// </summary>
  /// <summary>
  /// The non-local-jump intrinsics the lowering emits - PB's two of them, ON ERROR and EXIT FAR. They
  /// are NOT runtime calls and cannot be: arming either one captures the CURRENT frame - the BP and SP
  /// that <c>rt_raise</c> or a bare <c>EXIT FAR</c> will restore before it jumps - and a CALL would
  /// capture its own. So they expand to the same few MOVs the direct emitter writes inline, which is
  /// why they live here rather than in the runtime ABI table.
  /// </summary>
  private static readonly HashSet<string> NonLocalJumpIntrinsics = new(StringComparer.Ordinal) {
    "rt_onerr_arm", "rt_onerr_disarm", "rt_onerr_resume_next",
    "rt_err_clear", "rt_resume_mark", "rt_resume_same", "rt_resume_next",
    "rt_efar_arm", "rt_efar_go",
  };

  /// <summary>A word store into a named runtime cell.</summary>
  private void StoreCell(string cell, MOperand source) =>
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.DataCell(cell, 0, MRegSize.Word), source],
      new MInstrEffect(WrittenRegs: [], ReadRegs: source is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));

  /// <summary>Arms the handler triple: where to jump, and the frame to restore before jumping there.</summary>
  private void ArmHandler(MOperand target) {
    this.StoreCell("rt_onerr", target);
    this.StoreCell("rt_onerr_bp", new MOperand.Register(MReg.Physical_(Reg.BP)));
    this.StoreCell("rt_onerr_sp", new MOperand.Register(MReg.Physical_(Reg.SP)));
    this.StoreCell("rt_err", new MOperand.Immediate(0));
  }

  /// <summary>
  /// A word load of a named runtime cell into a PHYSICAL register - the half of <see cref="StoreCell"/>
  /// that puts a captured frame back. Declared as clobbering what it writes, which is both true and
  /// what pins the sequence: the scheduler treats an instruction with explicit clobbers as a barrier,
  /// so nothing addressed off BP can be moved after the MOV that replaces BP.
  /// </summary>
  private void LoadCell(Reg register, string cell) =>
    this._current.Instructions.Add(new MInstr(MOpcode.Mov,
      [new MOperand.Register(MReg.Physical_(register)), new MOperand.DataCell(cell, 0, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false),
      condition: null, clobbers: [register]));

  private bool SelectNonLocalJumpIntrinsic(IrCall call, IrFunction callee) {
    switch (callee.Name) {
      // EXIT FAR AT label: the unwind point, in the same three cells the direct emitter uses - where
      // to land, and the SP/BP of the frame that has to be back in place when it does
      case "rt_efar_arm":
        if (call.Args.FirstOrDefault() is not IrBlockAddress unwind)
          return this.Decline("EXIT FAR: the unwind target is not a block address");
        this.StoreCell("rt_efar_tgt", new MOperand.BlockOffset(unwind.Block.Label));
        this.StoreCell("rt_efar_sp", new MOperand.Register(MReg.Physical_(Reg.SP)));
        this.StoreCell("rt_efar_bp", new MOperand.Register(MReg.Physical_(Reg.BP)));
        return true;

      // a bare EXIT FAR: put the recorded frame back and jump into it. Every frame between here and
      // there is simply abandoned - that is what the statement means, and the restored SP is what
      // discards them all at once
      case "rt_efar_go":
        this.LoadCell(Reg.SP, "rt_efar_sp");
        this.LoadCell(Reg.BP, "rt_efar_bp");
        this._current.Instructions.Add(new MInstr(MOpcode.JmpIndirect,
          [new MOperand.DataCell("rt_efar_tgt", 0, MRegSize.Word)],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
            ReadsMemory: true, WritesMemory: false)));
        return true;

      case "rt_onerr_arm":
        if (call.Args.FirstOrDefault() is not IrBlockAddress handler)
          return this.Decline("ON ERROR: the handler is not a block address");
        this.ArmHandler(new MOperand.BlockOffset(handler.Block.Label));
        return true;

      // ON ERROR RESUME NEXT arms the runtime's own stub, which hops to the latched successor
      case "rt_onerr_resume_next":
        this.ArmHandler(new MOperand.DataOffset("rt_resumenext_handler", 0));
        return true;

      case "rt_onerr_disarm":
        this.StoreCell("rt_onerr", new MOperand.Immediate(0));
        return true;

      case "rt_err_clear":
        this.StoreCell("rt_err", new MOperand.Immediate(0));
        return true;

      // every statement publishes where it begins and where the next one does, so a fault can latch
      // whichever the resume will jump through
      case "rt_resume_mark": {
        if (call.Args.ToList() is not [IrBlockAddress start, IrBlockAddress next])
          return this.Decline("RESUME: a statement boundary is not a pair of block addresses");
        this.StoreCell("rt_resume", new MOperand.BlockOffset(start.Block.Label));
        this.StoreCell("rt_resumenext", new MOperand.BlockOffset(next.Block.Label));
        return true;
      }

      // RESUME / RESUME NEXT jump through the cell the fault latched - an indirect jump whose target
      // is not known here, so the runtime performs it and never comes back
      case "rt_resume_same":
      case "rt_resume_next":
        this.StoreCell("rt_err", new MOperand.Immediate(0));
        this._current.Instructions.Add(new MInstr(MOpcode.JmpIndirect,
          [new MOperand.DataCell(callee.Name == "rt_resume_same" ? "rt_eresume" : "rt_eresumenext", 0, MRegSize.Word)],
          new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
            ReadsMemory: true, WritesMemory: false)));
        return true;

      default:
        return this.Decline($"call: {callee.Name} (unhandled non-local-jump intrinsic)");
    }
  }

  /// <summary>
  /// For each argument, every physical register the staging has filled BY THAT POINT - its own
  /// destination and all the earlier ones.
  ///
  /// <para>
  /// A register filled by one move has to survive until the CALL and has no live interval of its own
  /// to say so; the clobber list is the only thing the allocator reads. A PREFIX rather than the
  /// whole set, because the registers later arguments will use are not filled yet and may still hold
  /// a value now - and a value that lives on until they are filled is covered by their own prefix,
  /// since the allocator unions clobbers over an interval. Claiming all of them everywhere is sound
  /// too, and costs two corpus functions to register pressure.
  /// </para>
  private static IReadOnlyList<Reg>[] StagingDestinations(RuntimeAbi.Routine routine) {
    var prefixes = new IReadOnlyList<Reg>[routine.Args.Length];
    var filled = new List<Reg>();
    for (var i = 0; i < routine.Args.Length; ++i) {
      var slot = routine.Args[i];
      switch (slot.Kind) {
        case RuntimeAbi.ArgKind.VolatileFlag or RuntimeAbi.ArgKind.St0:
          break;                                   // no general register is written for these
        case RuntimeAbi.ArgKind.Pair or RuntimeAbi.ArgKind.Pointer or RuntimeAbi.ArgKind.ZeroPair:
          filled.Add(slot.Register);
          filled.Add(slot.High);
          break;
        default:
          filled.Add(slot.Register);
          break;
      }
      prefixes[i] = filled.Distinct().ToList();
    }
    return prefixes;
  }

  private bool SelectRuntimeCall(IrCall call, IrFunction callee, RuntimeAbi.Routine routine) {
    // an x87 answer arrives on the stack rather than in a named register, so it needs no Result
    if (!call.Type.IsVoid && routine.Result is null
        && routine.Answer is RuntimeAbi.ResultKind.Word or RuntimeAbi.ResultKind.WidenedWord)
      return this.Decline($"call: {callee.Name} returns a value the runtime ABI table does not place");
    if (!call.Type.IsVoid && !this.ResultShapeAgrees(call, callee, routine))
      return false;
    var args = call.Args.ToList();
    if (args.Count != routine.Args.Length)
      return this.Decline($"call: {callee.Name} arity disagrees with the runtime ABI table");

    // PRINT #n: route the console routines at the file first. The select destroys the caller-saved
    // file, so it goes BEFORE the remaining arguments are moved into place
    var first = 0;
    if (routine.FileSelect) {
      if (!this.SelectFileRouting(args[0], routine.Args[0]))
        return false;
      first = 1;
    }

    // EVERY staging move claims the call's WHOLE destination set, not just the register it writes.
    //
    // The narrow claim was a silent miscompile. A staging move says "I destroy DI" so that a value
    // live across it avoids DI - but it says nothing about the registers earlier moves already
    // filled, and those have no live interval of their own. So a virtual register created midway
    // through the sequence (the spiller inserts reloads exactly there) could legally be given AX,
    // overwriting a file number staged two instructions earlier. rt_frec_put then read 0xFFF4 - a
    // frame address - as its file number and raised ERR 57 from a routine that was correct at every
    // instruction.
    var stagedRegisters = StagingDestinations(routine);

    for (var i = first; i < args.Count; ++i) {
      var arg = args[i];
      var slot = routine.Args[i];
      switch (slot.Kind) {
        case RuntimeAbi.ArgKind.St0: {
          // the print routines take a float on ST(0) and pop it themselves
          if (!arg.Type.IsIeeeFloat)
            return this.Decline($"call: {callee.Name} wants a float on the x87 stack, got {arg.Type}");
          if (!this.TryFloatOperand(arg, out var loaded))
            return false;
          this.EmitX87(MOpcode.Fld, loaded, reads: true);
          break;
        }
        case RuntimeAbi.ArgKind.Offset: {
          // the address of the data object, not its contents - a string literal the codegen pools
          if (arg is not IrGlobalVariable global)
            return this.Decline($"call: {callee.Name} wants the address of a global, not {arg.GetType().Name}");
          var dest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          var address = new MOperand.DataOffset(global.Name, 0);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, address], MovEffect(dest, address),
            condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        case RuntimeAbi.ArgKind.Pointer: {
          if (!this.TryRuntimePointer(arg, callee.Name, out var source, out var segment))
            return false;
          var dest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          var segmentDest = new MOperand.Register(MReg.Physical_(slot.High, MRegSize.Word));
          var segmentSource = new MOperand.Register(MReg.Physical_(segment, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source),
            condition: null, clobbers: stagedRegisters[i]));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [segmentDest, segmentSource],
            MovEffect(segmentDest, segmentSource), condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        case RuntimeAbi.ArgKind.VolatileFlag:
          if (arg is not IrConstantInt { Type: { IsInteger: true, Bits: 1 }, Value: 0 or 1 })
            return this.Decline($"call: {callee.Name} has a non-constant LLVM volatility flag");
          break;
        case RuntimeAbi.ArgKind.Word: {
          if (!this.TryWordOperand(arg, $"{callee.Name} takes a 32-bit value in a word register", out var source))
            return false;
          var dest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source),
            condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        case RuntimeAbi.ArgKind.LowWord: {
          // the row claims the high half does not matter; see ArgKind.LowWord for what backs the claim
          if (!IsWide(arg.Type))
            return this.Decline($"call: {callee.Name} wants the low half of a 32-bit value, got {arg.Type}");
          if (!this.TryOperandPair(arg, out var low, out _))
            return false;
          var lowDest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lowDest, low], MovEffect(lowDest, low),
            condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        case RuntimeAbi.ArgKind.ZeroExtendedQwordSt0: {
          // four words into one qword cell - the value's own two, then two zeroes - and FILD it. The
          // zeroes are what make the 64-bit printer render the DWORD unsigned.
          if (!IsWide(arg.Type))
            return this.Decline($"call: {callee.Name} wants a 32-bit value to widen, got {arg.Type}");
          if (!this.TryOperandPair(arg, out var low, out var high))
            return false;
          var staged = this._function.StackSlots.Count;
          this._function.StackSlots.Add(8);
          var cell = new MOperand.StackSlot(staged, MRegSize.Word);
          this.StoreWord(cell, low);
          this.StoreWord(Shifted(cell, 2), high);
          this.StoreWord(Shifted(cell, 4), new MOperand.Immediate(0));
          this.StoreWord(Shifted(cell, 6), new MOperand.Immediate(0));
          this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(staged, MRegSize.Qword), reads: true);
          break;
        }
        case RuntimeAbi.ArgKind.SignedQwordSt0: {
          // A QUAD read out of storage is already in a qword cell of its own (SelectQwordLoad), so
          // there is nothing to stage: FILD it, which is exactly what the direct emitter does with
          // the variable's own cell before calling the 15-digit DOUBLE formatter.
          if (IsQuad(arg.Type) && this._qslots.TryGetValue(arg, out var loaded)) {
            this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(loaded, MRegSize.Qword), reads: true);
            break;
          }
          // The machine IR does not yet carry a general four-register i64 value. An optimized QUAD
          // literal does not need one: stage its four words verbatim, then FILD the qword just as the
          // direct emitter loads a QUAD cell before calling the 15-digit DOUBLE formatter.
          if (arg is not IrConstantInt { Type: { IsInteger: true, Bits: 64 }, Value: var value })
            return this.Decline($"call: {callee.Name} wants a constant signed 64-bit value, got {arg}");
          var staged = this._function.StackSlots.Count;
          this._function.StackSlots.Add(8);
          var cell = new MOperand.StackSlot(staged, MRegSize.Word);
          for (var offset = 0; offset < 8; offset += 2)
            this.StoreWord(Shifted(cell, offset), new MOperand.Immediate((short)(value >> (offset * 8))));
          this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(staged, MRegSize.Qword), reads: true);
          break;
        }
        case RuntimeAbi.ArgKind.ZeroPair: {
          // the word into the low register, the high one cleared - "XOR DX,DX" in the direct emitter
          if (IsWide(arg.Type))
            return this.Decline($"call: {callee.Name} zero-extends a word, got {arg.Type}");
          if (!this.TryOperand(arg, out var word))
            return false;
          var low = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          var high = new MOperand.Register(MReg.Physical_(slot.High, MRegSize.Word));
          var zero = new MOperand.Immediate(0);
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [low, word], MovEffect(low, word),
            condition: null, clobbers: stagedRegisters[i]));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, zero], MovEffect(high, zero),
            condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        case RuntimeAbi.ArgKind.Pair: {
          if (!IsWide(arg.Type))
            return this.Decline($"call: {callee.Name} wants a 32-bit argument, got {arg.Type}");
          if (!this.TryOperandPair(arg, out var lo, out var hi))
            return false;
          var destLo = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          var destHi = new MOperand.Register(MReg.Physical_(slot.High, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destLo, lo], MovEffect(destLo, lo),
            condition: null, clobbers: stagedRegisters[i]));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, hi], MovEffect(destHi, hi),
            condition: null, clobbers: stagedRegisters[i]));
          break;
        }
        default:
          return this.Decline($"call: {callee.Name} argument kind {slot.Kind}");
      }
    }

    // the constants a convention fixes rather than taking as an argument: HEX$'s "four bits per
    // digit, minimum one", INSTR's "start at position 1"
    foreach (var (dest, value) in routine.Constants ?? []) {
      var to = new MOperand.Register(MReg.Physical_(dest, MRegSize.Word));
      var immediate = new MOperand.Immediate(value);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [to, immediate], MovEffect(to, immediate),
        condition: null, clobbers: [dest]));
    }

    // whatever else the convention fixes: the string kernel wants the literal's segment in DX
    foreach (var (dest, source) in routine.Presets ?? []) {
      var to = new MOperand.Register(MReg.Physical_(dest, MRegSize.Word));
      var from = new MOperand.Register(MReg.Physical_(source, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [to, from], MovEffect(to, from),
        condition: null, clobbers: [dest]));
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(routine.Label)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: routine.Clobbers));

    if (routine.FileSelect)
      this.RestoreConsoleOutput();

    if (call.Type.IsVoid)
      return true;

    return this.PlaceRuntimeResult(call, routine);
  }

  /// <summary>Materializes a near pointer and identifies the segment containing its base object.</summary>
  private bool TryRuntimePointer(IrValue value, string callee, out MOperand offset, out Reg segment) {
    offset = null!;
    segment = default;
    if (value.Type.IsFarPointer)
      return this.Decline($"call: {callee} takes an address in the far array heap, whose segment is a runtime cell and not a register");
    if (PointerSegmentOf(value) is not { } sourceSegment)
      return this.Decline($"call: {callee} cannot derive the segment of pointer {value}");
    segment = sourceSegment;
    if (value is IrGlobalVariable global) {
      offset = new MOperand.DataOffset(global.Name, 0);
      return true;
    }
    if (!this.TryOperand(value, out offset) || offset is not MOperand.Register)
      return this.Decline($"call: {callee} pointer is not an address register");
    return true;
  }

  private static Reg? PointerSegmentOf(IrValue value) => value switch {
    IrGlobalVariable => Reg.DS,
    IrAlloca => Reg.SS,
    IrGep gep => PointerSegmentOf(gep.BasePtr),
    IrCast cast when cast.Type.IsPointer => PointerSegmentOf(cast.Value),
    _ => null,
  };

  /// <summary>Whether the call's IR result type is one this table entry knows how to hand back.</summary>
  private bool ResultShapeAgrees(IrCall call, IrFunction callee, RuntimeAbi.Routine routine) => routine.Answer switch {
    RuntimeAbi.ResultKind.WidenedWord when !IsWide(call.Type)
      => this.Decline($"call: {callee.Name} widens a word result, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.St0 when !call.Type.IsIeeeFloat
      => this.Decline($"call: {callee.Name} answers on the x87 stack, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.Pair when !call.Type.IsInteger || !IsWide(call.Type)
      => this.Decline($"call: {callee.Name} answers in DX:AX, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.ScratchI16 when !call.Type.IsInteger || IsWide(call.Type)
        || RegSize(call.Type) != MRegSize.Word
      => this.Decline($"call: {callee.Name} answers with a scratch word, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.ScratchU8ToWord when !call.Type.IsInteger || IsWide(call.Type)
        || RegSize(call.Type) != MRegSize.Word
      => this.Decline($"call: {callee.Name} zero-extends a scratch byte, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.ScratchI32 when !call.Type.IsInteger || !IsWide(call.Type)
      => this.Decline($"call: {callee.Name} answers with a scratch dword, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.ScratchF32 when !call.Type.IsIeeeFloat || call.Type.Bits != 32
      => this.Decline($"call: {callee.Name} answers with scratch binary32, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.ScratchF64 when !call.Type.IsIeeeFloat || call.Type.Bits is not (64 or 80)
      => this.Decline($"call: {callee.Name} answers with scratch binary64, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.LowByte when !call.Type.IsInteger || RegSize(call.Type) != MRegSize.Byte
      => this.Decline($"call: {callee.Name} answers with a byte, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.St0ToQword when !call.Type.IsInteger || call.Type.Bits != 64
      => this.Decline($"call: {callee.Name} answers on the x87 into a qword cell, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.Word when IsWide(call.Type) || RegSize(call.Type) != MRegSize.Word
      => this.Decline($"call: {callee.Name} returns {call.Type} (word results only)"),
    _ => true,
  };

  /// <summary>
  /// Moves the routine's answer out of the fixed register it arrives in and into the call's own
  /// virtual register (or frame cell), so the allocator may place the value wherever it likes.
  /// </summary>
  private bool PlaceRuntimeResult(IrCall call, RuntimeAbi.Routine routine) {
    // the x87 answer is already on the stack; popping it into the call's cell is the whole transfer
    if (routine.Answer == RuntimeAbi.ResultKind.St0) {
      this.EmitX87(MOpcode.Fstp, this.FloatCell(call), reads: false);
      return true;
    }

    if (routine.Answer == RuntimeAbi.ResultKind.Pair) {
      if (routine.Result != Reg.AX)
        return this.Decline($"call: {routine.Label} returns a pair outside the supported DX:AX convention");
      var (low, high) = this.FreshPair(call);
      var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
      var dx = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [low, ax], MovEffect(low, ax)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, dx], MovEffect(high, dx)));
      return true;
    }

    if (routine.Answer == RuntimeAbi.ResultKind.ScratchI16) {
      var dest = this.FreshVreg(call.Type);
      this._vregs[call] = dest;
      var destOp = new MOperand.Register(dest);
      var source = new MOperand.DataCell("rt_scratch", 0, MRegSize.Word);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, source], MovEffect(destOp, source)));
      return true;
    }

    if (routine.Answer == RuntimeAbi.ResultKind.ScratchU8ToWord) {
      var dest = this.FreshVreg(call.Type);
      this._vregs[call] = dest;
      var word = new MOperand.Register(dest);
      var lowByte = new MOperand.Register(dest with { Size = MRegSize.Byte });
      var zero = new MOperand.Immediate(0);
      var source = new MOperand.DataCell("rt_scratch", 0, MRegSize.Byte);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [word, zero], MovEffect(word, zero)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lowByte, source], MovEffect(lowByte, source)));
      return true;
    }

    if (routine.Answer == RuntimeAbi.ResultKind.ScratchI32) {
      var (low, high) = this.FreshPair(call);
      var lowSource = new MOperand.DataCell("rt_scratch", 0, MRegSize.Word);
      var highSource = new MOperand.DataCell("rt_scratch", 2, MRegSize.Word);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [low, lowSource], MovEffect(low, lowSource)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, highSource], MovEffect(high, highSource)));
      return true;
    }

    // The x87 is holding an INTEGER here, not a float, so the answer is popped with FISTP into a
    // qword frame cell rather than with FSTP into a float one - the same cell SelectQwordLoad mints
    // and TryQwordSlot looks values up in, which is what makes the result an ordinary 64-bit value
    // every later store and conversion already knows how to read.
    if (routine.Answer == RuntimeAbi.ResultKind.St0ToQword) {
      var qslot = this._function.StackSlots.Count;
      this._function.StackSlots.Add(8);
      this._qslots[call] = qslot;
      this.EmitX87(MOpcode.Fistp, new MOperand.StackSlot(qslot, MRegSize.Qword), reads: false);
      return true;
    }

    if (routine.Answer is RuntimeAbi.ResultKind.ScratchF32 or RuntimeAbi.ResultKind.ScratchF64) {
      var size = routine.Answer == RuntimeAbi.ResultKind.ScratchF32 ? MRegSize.Dword : MRegSize.Qword;
      this.EmitX87(MOpcode.Fld, new MOperand.DataCell("rt_scratch", 0, size), reads: true);
      this.EmitX87(MOpcode.Fstp, this.FloatCell(call), reads: false);
      return true;
    }

    // A BYTE result keeps the low half of the answer register. The routine computed a whole word -
    // there is no narrower thing for it to have computed - and the discard is the same one the direct
    // emitter makes when it stores AL into a BYTE cell.
    if (routine.Answer == RuntimeAbi.ResultKind.LowByte) {
      if (routine.Result!.Value != Reg.AX)
        return this.Decline($"call: {routine.Label} answers with a byte outside AL");
      var byteDest = this.FreshVreg(call.Type);
      this._vregs[call] = byteDest;
      var byteOp = new MOperand.Register(byteDest);
      var al = new MOperand.Register(MReg.Physical_(Reg.AL, MRegSize.Byte));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [byteOp, al], MovEffect(byteOp, al)));
      return true;
    }

    var resultReg = new MOperand.Register(MReg.Physical_(routine.Result!.Value, MRegSize.Word));
    if (routine.Answer == RuntimeAbi.ResultKind.Word) {
      var dest = this.FreshVreg(call.Type);
      this._vregs[call] = dest;
      var destOp = new MOperand.Register(dest);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, resultReg], MovEffect(destOp, resultReg)));
      return true;
    }

    // WidenedWord: CWD sign-extends AX into DX:AX, which is exactly what the direct emitter writes
    // after rt_len and rt_asc. It reads AX and writes DX, so both are declared clobbered - otherwise
    // the allocator could park some other live value in DX across it and lose it.
    if (routine.Result!.Value != Reg.AX)
      return this.Decline($"call: {routine.Label} widens from {routine.Result.Value}, but CWD only extends AX");
    this._current.Instructions.Add(new MInstr(MOpcode.Cwd, [],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: [Reg.AX, Reg.DX]));

    var (lo, hi) = this.FreshPair(call);
    var dxOperand = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lo, resultReg], MovEffect(lo, resultReg)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [hi, dxOperand], MovEffect(hi, dxOperand)));
    return true;
  }

  /// <summary>
  /// The transcendental intrinsics are INSTRUCTIONS, not runtime routines: the x87 has FSQRT, FSIN,
  /// FCOS, FPTAN, FPATAN and FYL2X, and the direct emitter writes each inline. The IR spells them as
  /// calls because that is how the C and LLVM back ends want them, so the names are recognised here and
  /// turned back into the sequences they always were.
  ///
  /// Each sequence is transcribed from CodeGenerator.Intrinsics.cs rather than derived. The three
  /// logarithms differ only in the constant loaded before FYL2X, and the three exponentials only in the
  /// constant multiplied in before <c>rt_pow2</c> - which is the one entry here that IS a call, because
  /// 2^x is a routine and not an instruction.
  /// </summary>
  /// <summary>
  /// How a transcendental is computed, which depends on the declared target.
  ///
  /// <para>
  /// FSIN, FCOS and the 387 reading of FPTAN are 80387 instructions. An image whose declared target is
  /// an 8086 must not contain them, and genuine PBC 3.5 does not emit them at all - it compiles SIN,
  /// COS and TAN through one shared FPTAN routine. Below a 386 this calls that routine; on a 386 the
  /// single instruction is kept, where the processor has it and it is both smaller and faster.
  /// </para>
  /// <para>
  /// This used to be hardcoded to the 8087 form on the grounds that the back end declared no CPU
  /// floor, which was true and was still wrong: the direct emitter has always chosen by target, and
  /// the two paths emit into the SAME image. Under <c>$CPU 386</c> a routed function called rt_sin
  /// while a directly-emitted one executed FSIN - one program computing sine two ways, and the two do
  /// not have to agree.
  /// </para>
  /// </summary>
  private static (MOpcode[] Before, string? Call)? MathSequence(string name, bool cpu386) {
    var bare = name.StartsWith("llvm.", StringComparison.Ordinal) ? name[5..] : name;
    var cut = bare.IndexOf(".f", StringComparison.Ordinal);
    return (cut > 0 ? bare[..cut] : bare) switch {
      "sqrt" => ([MOpcode.Fsqrt], null),
      "sin" => cpu386 ? ([MOpcode.Fsin], null) : ([], "rt_sin"),
      "cos" => cpu386 ? ([MOpcode.Fcos], null) : ([], "rt_cos"),
      // FPTAN; FSTP ST(0) is the 387 reading - discard what was pushed, keep the tangent under it.
      // An 8087's FPTAN leaves a ratio, not a tangent, and is only defined on [0, pi/4] besides.
      "tan" => cpu386 ? ([MOpcode.Fptan, MOpcode.FstpSt0], null) : ([], "rt_tan"),
      "atan" => ([MOpcode.Fld1, MOpcode.Fpatan], null),
      "log" => ([MOpcode.Fldln2, MOpcode.Fxch, MOpcode.Fyl2x], null),
      "log2" => ([MOpcode.Fld1, MOpcode.Fxch, MOpcode.Fyl2x], null),
      "log10" => ([MOpcode.Fldlg2, MOpcode.Fxch, MOpcode.Fyl2x], null),
      "exp" => ([MOpcode.Fldl2e, MOpcode.Fmulp], "rt_pow2"),
      "exp2" => ([], "rt_pow2"),
      "exp10" => ([MOpcode.Fldl2t, MOpcode.Fmulp], "rt_pow2"),
      _ => null,
    };
  }

  private bool SelectMathIntrinsic(IrCall call, IrFunction callee, (MOpcode[] Before, string? Call) sequence) {
    if (call.Args.ToList() is not [{ } operand])
      return this.Decline($"call: {callee.Name} takes one operand");
    if (!call.Type.IsIeeeFloat || !operand.Type.IsIeeeFloat)
      return this.Decline($"call: {callee.Name} is not floating point");
    if (!this.TryFloatOperand(operand, out var cell))
      return false;

    this.EmitX87(MOpcode.Fld, cell, reads: true);
    // No memory, no registers, no flags - which is the truth, and is safe because the scheduler orders
    // x87 instructions against each other by opcode (MOpcodes.UsesX87) rather than by their effects.
    foreach (var opcode in sequence.Before)
      this._current.Instructions.Add(new MInstr(opcode, [], MInstrEffect.None));
    if (sequence.Call is { } label)
      this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(label)],
        new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: true, WritesMemory: true),
        condition: null, clobbers: _callClobbers));
    // Rounded to the DECLARED width on the way out, then brought back up - the FSTP m64 / FLD m64
    // the direct emitter writes right after the FYL2X. Keeping all eighty bits looks more accurate
    // and is less faithful: LOG(2.718281828459045#) is .9999999999999999 at eighty bits and 1 once
    // rounded to a double, and genuine QuickBASIC prints 1. Four battery programs turned on it.
    this.PopRounded(call.Type, this.FloatCell(call));
    return true;
  }

  /// <summary>The runtime's multi-concat staging list holds this many handles (DosRuntime._STRCATN_MAX).</summary>
  private const int _CATLIST_SLOTS = 64;

  /// <summary>
  /// The single-allocation concatenation builder, which is the one runtime entry whose ABI the table
  /// cannot describe: it is VARIADIC, and it takes its operands in a runtime word array rather than
  /// in registers. The count goes in <c>CX</c>, handle <c>i</c> into <c>rt_catlist[i]</c>, and the
  /// result comes back in <c>AX</c> - exactly the sequence the direct emitter writes for pb36 O24.
  ///
  /// <para>
  /// The staging area is a single global, which is safe for the same reason the direct emitter's use
  /// of it is: every operand is a value already computed by the time this runs, the stores and the
  /// call are adjacent, and the routine consumes the whole list before returning - so no second
  /// builder can be part-way through it.
  /// </para>
  /// </summary>
  private bool SelectMultiConcat(IrCall call) {
    var args = call.Args.ToList();
    if (args is not [IrConstantInt count, ..] || count.Value != args.Count - 1)
      return this.Decline("call: rt_str_concat_n's leading count is not the number of operands that follow");
    if (count.Value is < 1 or > _CATLIST_SLOTS)
      return this.Decline($"call: rt_str_concat_n takes 1..{_CATLIST_SLOTS} operands, got {count.Value}");
    if (call.Type.IsVoid || IsWide(call.Type) || RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: rt_str_concat_n answers with a string handle, but the call is typed {call.Type}");

    // Every staging store claims the call's destination register, exactly as SelectRuntimeCall's
    // moves do. It is not about CX: an instruction carrying a clobber is a scheduling barrier, and
    // without one these stores are free to move ABOVE the MOV that captures the previous call's
    // result out of AX. The spiller then inserts a reload before each of them - inside the window
    // where AX still holds that result - and the last operand staged is the previous one's handle.
    // `a$ + (b$ + c$)` printed "aabbbb" for exactly that reason.
    IReadOnlyList<Reg> pending = [Reg.CX];
    for (var i = 1; i < args.Count; ++i) {
      if (!this.TryOperand(args[i], out var handle))
        return false;
      // through a register: x86 has no memory-to-memory MOV, and a spilled handle arrives as a cell
      if (handle is not (MOperand.Register or MOperand.Immediate)) {
        var staged = new MOperand.Register(this.FreshVreg(args[i].Type));
        this._current.Instructions.Add(new MInstr(MOpcode.Mov, [staged, handle], MovEffect(staged, handle),
          condition: null, clobbers: pending));
        handle = staged;
      }
      var slot = new MOperand.DataCell("rt_catlist", (i - 1) * 2, MRegSize.Word);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [slot, handle],
        new MInstrEffect(WrittenRegs: [], ReadRegs: handle is MOperand.Register ? [1] : [],
          ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true),
        condition: null, clobbers: pending));
    }

    var cx = new MOperand.Register(MReg.Physical_(Reg.CX, MRegSize.Word));
    var operands = new MOperand.Immediate(count.Value);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [cx, operands], MovEffect(cx, operands),
      condition: null, clobbers: pending));
    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_strcatn")],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));

    var result = this.FreshVreg(call.Type);
    this._vregs[call] = result;
    var destination = new MOperand.Register(result);
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destination, ax], MovEffect(destination, ax)));
    return true;
  }

  /// <summary>
  /// A value as a WORD operand, narrowing a 32-bit one where that is sound.
  ///
  /// The IR types several things i32 that the runtime wants in a word register - a byte count, a PB
  /// file number, a character code. Taking the low half is only sound when the high half is known to
  /// carry nothing. Three shapes say so outright: a constant that fits, a value that was WIDENED from
  /// 16 bits in the first place, and a runtime answer the ABI table declares a widened word. The
  /// fourth is COMPUTED - see <see cref="WordSizedRange"/> - and it is the one that keeps selection
  /// from depending on the optimizer. Anything else declines rather than silently dropping the top word.
  /// </summary>
  private bool TryWordOperand(IrValue value, string what, out MOperand operand) {
    operand = null!;
    if (!IsWide(value.Type) && value.Type.IsInteger && value.Type.Bits == 8) {
      // PB's BYTE is UNSIGNED and the IR types it i8, so 200 is carried as the bit pattern -56. A
      // row that wants it in a WORD wants the VALUE, which is why the register path below clears the
      // high half - and why a CONSTANT has to be zero-extended here rather than emitted as it
      // stands. It used to skip this branch entirely and hand the immediate over sign-extended, so
      // PRINT of a BYTE holding 200 rendered -56: the two halves of one rule disagreeing.
      if (value is IrConstantInt literal) {
        operand = new MOperand.Immediate(literal.Value & 0xFF);
        return true;
      }
      if (!this.TryOperand(value, out var narrow))
        return false;
      var id = this._nextVreg++;
      var word = new MOperand.Register(MReg.Virtual(id, MRegSize.Word));
      var lowByte = new MOperand.Register(MReg.Virtual(id, MRegSize.Byte));
      var zero = new MOperand.Immediate(0);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [word, zero], MovEffect(word, zero)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [lowByte, narrow], MovEffect(lowByte, narrow)));
      operand = word;
      return true;
    }
    if (!IsWide(value.Type))
      return this.TryOperand(value, out operand);

    // A runtime routine whose answer is a WIDENED word has a high half that is nothing but the sign
    // extension of the low one, so the low half IS the value. ASC is the case that matters: the IR
    // types it i32 because the same declaration feeds the C back end, and STRING$(n, s$) then hands
    // that i32 to a routine wanting a character in DL.
    if (value is IrCall { Callee: IrFunction { IsDeclaration: true } widened }
        && RuntimeAbi.For(widened.Name) is { Answer: RuntimeAbi.ResultKind.WidenedWord }
        && this.TryOperandPair(value, out var low, out _)) {
      operand = low;
      return true;
    }

    var narrowed = value switch {
      IrConstantInt { Value: >= short.MinValue and <= ushort.MaxValue } c => (IrValue)c,
      IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt } cast when !IsWide(cast.Value.Type) => cast.Value,
      _ => null,
    };
    if (narrowed is null) {
      // The last chance before declining: arithmetic the selector can PROVE stays inside a word, whose
      // low half is therefore the whole value. The wide form is still selected - this takes the low
      // register of the pair it produced, which is exactly the 16-bit result, because the low half of
      // an add/sub/and/or/xor depends only on the low halves of its operands.
      if (WordSizedRange(value, _NARROWING_DEPTH) is not { } range
          || range.Lo < short.MinValue || range.Hi > ushort.MaxValue)
        return this.Decline($"call: {what} (the IR types it 32-bit)");
      return this.TryOperandPair(value, out operand, out _);
    }
    if (narrowed is IrConstantInt fits) {
      operand = new MOperand.Immediate(fits.Value);
      return true;
    }
    return this.TryOperand(narrowed, out operand);
  }

  /// <summary>
  /// How far the word-size proof walks an expression before giving up. Every step is a real IR
  /// operation, so a bound this small costs nothing a BASIC subscript or character code ever reaches,
  /// and it keeps a shared sub-expression from being re-proved exponentially.
  /// </summary>
  private const int _NARROWING_DEPTH = 8;

  /// <summary>
  /// The interval a 32-bit value is PROVABLY confined to, or null when the selector cannot say - the
  /// proof obligation behind narrowing an i32 to one word register.
  ///
  /// <para>
  /// <b>The rule.</b> A value narrows only when narrowing cannot change what the consumer reads, and
  /// the consumer here is a 16-bit register: it cannot see more than sixteen bits whatever is done.
  /// So two things have to hold together, and each on its own is worthless.
  /// </para>
  /// <list type="number">
  ///   <item><b>The low half must be self-sufficient.</b> Only operations whose low sixteen bits are a
  ///   function of their operands' low sixteen bits qualify - <c>add</c>, <c>sub</c>, <c>and</c>,
  ///   <c>or</c>, <c>xor</c>. A shift, a divide and a comparison do NOT commute with truncation, and
  ///   neither does a LOAD: the high half of a value read out of a LONG variable is real data. A
  ///   multiply would qualify on this count and is left out on the second one - no interval a product
  ///   of two word-sized operands can be given fits a word often enough to be worth the arm.</item>
  ///   <item><b>The value must fit.</b> Every leaf contributes a known interval - a constant its own
  ///   value, a <c>sext</c>/<c>zext</c> from i8/i16 whatever its operand can be shown to be, and
  ///   failing that the span of the type it was widened from (<see cref="WidenedRange"/>) - and the
  ///   operations propagate those intervals. The result must land inside
  ///   <c>[short.MinValue, ushort.MaxValue]</c>, which is the same window the constant arm above
  ///   already accepts: everything in it is carried whole by one word, signed at one end and unsigned
  ///   at the other, and the caller decides which - as it already does for an immediate.</item>
  /// </list>
  ///
  /// <para>
  /// The second obligation is what makes this conservative rather than clever. <c>64 + i%</c> proves
  /// out at <c>[-32704, 32831]</c> and narrows; <c>i% + j%</c> spans <c>[-65536, 65534]</c> and does
  /// not, and neither does <c>i% - j%</c>, whose borrow reaches <c>-65535</c>. A one-word answer for
  /// those would be a silent miscompile, so they keep their register pair and the caller declines.
  /// </para>
  ///
  /// <para>
  /// Where the interval overhangs the SIGNED word - the case <c>64 + i%</c> is in - the narrowed word
  /// is what the direct emitter produces anyway: PB computes <c>64 + i%</c> in 16 bits and wraps, and
  /// the low half of the 32-bit sum IS that wrapped result, bit for bit. Fidelity is the reason the
  /// window is the union of the two words rather than either one alone.
  /// </para>
  /// </summary>
  private static (long Lo, long Hi)? WordSizedRange(IrValue value, int depth) {
    if (depth <= 0)
      return null;
    switch (value) {
      // The bound is not about what fits a word - the caller decides that - it is about what fits the
      // ARITHMETIC below: intervals are added and subtracted, and a 32-bit literal carried as a long
      // wider than 32 bits could sum past a long's end and come back looking small.
      case IrConstantInt { Value: >= int.MinValue and <= int.MaxValue } c:
        return (c.Value, c.Value);
      // i1 is deliberately absent: the IR reads a bool as 0/1 while this target holds BASIC truth as a
      // full -1/0 word, so a zext of one is the one widening whose low half is NOT the IR's value.
      case IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt } cast
          when cast.Value.Type is { IsInteger: true, Bits: 8 or 16 } source:
        return WidenedRange(cast.Op, source, WordSizedRange(cast.Value, depth - 1));
      case IrBinary bin when IsWide(bin.Type): {
        // An operand with NO interval is not fatal for AND, which is why the two sides travel as
        // nullables: a mask bounds the result on its own, however unknown the value it masks.
        var lhs = WordSizedRange(bin.Lhs, depth - 1);
        var rhs = WordSizedRange(bin.Rhs, depth - 1);
        if (bin.Op == IrBinaryOp.And)
          return MaskedRange(lhs, rhs);
        if (lhs is not { } left || rhs is not { } right)
          return null;
        return bin.Op switch {
          IrBinaryOp.Add => (left.Lo + right.Lo, left.Hi + right.Hi),
          IrBinaryOp.Sub => (left.Lo - right.Hi, left.Hi - right.Lo),
          IrBinaryOp.Or or IrBinaryOp.Xor => MergedBitsRange(left, right),
          _ => null,
        };
      }
      default:
        return null;
    }
  }

  /// <summary>
  /// What a widening leaves of its operand's interval.
  ///
  /// <para>
  /// The span of the source type is the floor of what can be said - a <c>sext i16</c> produces
  /// something in <c>[-32768, 32767]</c> whatever it widens. What it used to be is ALL that was said,
  /// and that threw away the one leaf that knows its value exactly: a widened CONSTANT. Nothing folds
  /// <c>sext i16 64</c> into <c>i32 64</c> until <c>instcombine</c> runs, so under a reduced pipeline
  /// <c>64 + i%</c> reaches here as two i16 spans and sums to <c>[-65536, 65534]</c> - which does not
  /// fit a word, so the argument declined and the function did not route. Reading the operand's own
  /// interval gives <c>[64, 64] + [-32768, 32767] = [-32704, 32831]</c>, which does.
  /// </para>
  ///
  /// <para>
  /// The operand's interval may only be used when the conversion REPRODUCES it, which is where the two
  /// signedness mismatches bite. A <c>sext</c> of an unsigned source reads its top bit as a sign, so a
  /// <c>WORD</c> holding 40000 comes out -25536 and the operand's <c>[40000, 40000]</c> would be a lie;
  /// a <c>zext</c> of a signed source is the mirror, turning -1 into 65535. In each case the honest
  /// answer is the span, which is what an unproven operand gets anyway.
  /// </para>
  /// </summary>
  private static (long Lo, long Hi) WidenedRange(IrCastOp op, IrType source, (long Lo, long Hi)? inner) {
    var span = 1L << source.Bits;
    var whole = op == IrCastOp.SExt ? (Lo: -(span / 2), Hi: span / 2 - 1) : (Lo: 0L, Hi: span - 1);
    if (inner is not { } value)
      return whole;
    var faithful = op == IrCastOp.SExt
      ? source.Signed || (value.Lo >= 0 && value.Hi <= whole.Hi)   // the sign bit the extension reads is clear
      : !source.Signed || value.Lo >= 0;                           // nothing negative to turn into a large positive
    if (!faithful)
      return whole;
    var narrowed = (Lo: System.Math.Max(value.Lo, whole.Lo), Hi: System.Math.Min(value.Hi, whole.Hi));
    return narrowed.Lo > narrowed.Hi ? whole : narrowed;
  }

  /// <summary>
  /// The interval of <c>a AND b</c>. ONE non-negative operand is enough, and that is the whole point
  /// of the arm: <c>x AND m</c> for <c>0 &lt;= m</c> clears every bit above m's highest whatever x is,
  /// so the result is in <c>[0, m]</c> even when x is a LONG read out of storage whose high half is
  /// real data. The AND has already discarded exactly what the narrowing would. With neither operand
  /// known non-negative there is no bound to give - <c>-1 AND -1</c> is -1, sign bits and all - so the
  /// proof stops.
  /// </summary>
  private static (long Lo, long Hi)? MaskedRange((long Lo, long Hi)? lhs, (long Lo, long Hi)? rhs) {
    var left = lhs is { Lo: >= 0 } a ? a.Hi : (long?)null;
    var right = rhs is { Lo: >= 0 } b ? b.Hi : (long?)null;
    return (left, right) switch {
      (not null, not null) => (0L, System.Math.Min(left.Value, right.Value)),
      (not null, null) => (0L, left.Value),
      (null, not null) => (0L, right.Value),
      _ => null,
    };
  }

  /// <summary>
  /// The interval of <c>a OR b</c> / <c>a XOR b</c>: with both operands non-negative the result cannot
  /// have a bit above the widest one either of them can have, so it is bounded by that bit width's
  /// mask. A negative operand makes the result negative and the bound meaningless, so the proof stops.
  /// </summary>
  private static (long Lo, long Hi)? MergedBitsRange((long Lo, long Hi) lhs, (long Lo, long Hi) rhs) {
    if (lhs.Lo < 0 || rhs.Lo < 0)
      return null;
    var widest = System.Math.Max(lhs.Hi, rhs.Hi);
    // Above a word the mask is above a word too and the caller declines anyway; stopping here also
    // keeps the doubling below away from the end of a long, where it would wrap and never terminate.
    if (widest > ushort.MaxValue)
      return null;
    var mask = 0L;
    while (mask < widest)
      mask = mask * 2 + 1;
    return (0L, mask);
  }

  /// <summary>Routes the console print routines at a PB file number (<c>rt_fselect</c>).</summary>
  private bool SelectFileRouting(IrValue file, RuntimeAbi.RuntimeArg slot) {
    // The IR types a PB file number 32-bit, and rt_fselect wants a word. Narrowing is sound in exactly
    // the cases the ordinary Word argument path allows - a constant that fits, or a value that was
    // WIDENED from 16 bits, where taking the low half only undoes the extension. It used to accept
    // the constant alone, which declined every `PRINT #n` whose n came from a variable.
    if (!this.TryWordOperand(file, "PRINT # to a runtime file number", out var source))
      return false;

    var ax = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, source], MovEffect(ax, source),
      condition: null, clobbers: [slot.Register]));
    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(RuntimeAbi.FileSelectLabel)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));
    return true;
  }

  /// <summary>Points the console routines back at stdout and its own print column, as the direct emitter does after a PRINT #.</summary>
  private void RestoreConsoleOutput() {
    var curout = new MOperand.DataCell("rt_curout", 0, MRegSize.Word);
    var stdout = new MOperand.Immediate(1);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [curout, stdout],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));
    var colptr = new MOperand.DataCell("rt_colptr", 0, MRegSize.Word);
    var col = new MOperand.DataOffset("rt_col", 0);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [colptr, col],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));
  }

  #region x87

  /// <summary>The frame cell a floating-point value lives in, minting one on first use.</summary>
  /// <summary>
  /// The frame cell an intermediate float value is parked in - always a TBYTE, whatever the IR types
  /// the value.
  ///
  /// This is not a rounding decision, it is the absence of one. In PowerBASIC an expression's type
  /// selects the FORMATTER (a SINGLE prints 7 significant digits, a DOUBLE 15) but does not round the
  /// value on the way there: genuine PBC computes in the x87 and prints what the register holds.
  /// <c>PRINT H?/3</c> with <c>H? = 200</c> gives <c>66.66667</c> for exactly that reason - rounded to
  /// SINGLE first it would be <c>66.66666</c>, which is what this back end produced until the cell
  /// stopped being four bytes wide.
  ///
  /// Only TEMPORARIES come through here. A store to a declared variable writes through the variable's
  /// own cell at the variable's own width, so <c>D! = x</c> still rounds to SINGLE as it must - the
  /// rounding PB does keeps happening, and the rounding it does not do stops.
  /// </summary>
  private MOperand FloatCell(IrValue value) {
    if (!this._fslots.TryGetValue(value, out var slot)) {
      slot = this._function.StackSlots.Count;
      this._function.StackSlots.Add(_X87_CELL_BYTES);
      this._fslots[value] = slot;
    }
    return new MOperand.StackSlot(slot, MRegSize.Tbyte);
  }

  /// <summary>The x87's own register width, which is what an intermediate float is stored and reloaded at.</summary>
  private const int _X87_CELL_BYTES = 10;

  /// <summary>
  /// A float value as something x87 can load: its own frame cell, or - for a literal - the code
  /// generator's float constant pool, which stores every one as a qword double. Loading a SINGLE
  /// literal from the qword form is the same value: x87 widens to its internal format either way,
  /// and it is the very cell the direct emitter loads too.
  /// </summary>
  private bool TryFloatOperand(IrValue value, out MOperand cell) {
    if (this._floatParams.TryGetValue(value, out var parameter)) {
      cell = parameter;
      return true;
    }
    if (this._fslots.ContainsKey(value)) {
      cell = this.FloatCell(value);
      return true;
    }
    if (value is IrConstantFloat constant) {
      cell = new MOperand.DataCell(FloatConstantName(constant.Value), 0, MRegSize.Qword);
      return true;
    }
    cell = null!;
    return this.Decline($"floating point: {value.GetType().Name} has no cell");
  }

  /// <summary>The pool name a float literal resolves through - its bits, so equal values share a cell.</summary>
  internal static string FloatConstantName(double value)
    => ".fc." + System.BitConverter.DoubleToInt64Bits(value).ToString("x16");

  private static readonly Dictionary<IrBinaryOp, MOpcode> _floatOps = new() {
    [IrBinaryOp.FAdd] = MOpcode.Faddp,
    [IrBinaryOp.FSub] = MOpcode.Fsubp,
    [IrBinaryOp.FMul] = MOpcode.Fmulp,
    [IrBinaryOp.FDiv] = MOpcode.Fdivp,
  };

  /// <summary>
  /// <c>FLD lhs; FLD rhs; F&lt;op&gt;P; FSTP result</c> - the textbook stack form. Pushing the left
  /// operand first leaves it in ST(1), which is the order the popping arithmetic computes
  /// (<c>FSUBP</c> is ST(1) - ST(0)), so subtraction and division come out the right way round.
  /// </summary>
  private bool SelectFloatBinary(IrBinary bin) {
    if (bin.Type.IsMbf)
      return this.Decline("floating point: MBF is not an x87 format");
    if (!_floatOps.TryGetValue(bin.Op, out var opcode))
      return this.Decline($"floating point: {bin.Op}");
    if (this.TrySelectFloatMemoryBinary(bin))
      return true;
    if (!this.TryFloatOperand(bin.Lhs, out var lhs) || !this.TryFloatOperand(bin.Rhs, out var rhs))
      return false;

    this.EmitX87(MOpcode.Fld, lhs, reads: true);
    this.EmitX87(MOpcode.Fld, rhs, reads: true);
    // The op itself touches neither memory nor registers; what it touches is the x87 stack, which the
    // scheduler orders by opcode (MOpcodes.UsesX87) because no effect descriptor can name it.
    this._current.Instructions.Add(new MInstr(opcode, [], MInstrEffect.None));
    // ...and the result goes back at the width the IR gave it, which for PB's own expressions is the
    // x87's own and costs nothing. See PopRounded for why a NARROWER one is not an intermediate.
    this.PopRounded(bin.Type, this.FloatCell(bin));
    return true;
  }

  /// <summary>
  /// True for a QUAD - the one integer width this target holds in neither a register nor a pair, and
  /// so the one that has to travel through the x87 instead.
  ///
  /// SIGNED only, deliberately. The instructions that move eight bytes at once are <c>FILD</c> and
  /// <c>FISTP</c>, and both read the qword as signed; a pb36 <c>QWORD</c> above 2^63 would come back
  /// negative. Excluding it here makes the selector decline such a value rather than answer wrongly.
  /// </summary>
  private static bool IsQuad(IrType type) => type is { IsInteger: true, Bits: 64, Signed: true };

  /// <summary>
  /// A QUAD read out of storage, copied into its own frame cell by the only unit that moves eight
  /// bytes in one instruction: <c>FILD qword</c> then <c>FISTP qword</c>. Both directions are exact -
  /// the x87's mantissa is sixty-four bits wide, which is precisely what an int64 needs - so this is
  /// a copy and not a conversion, and the cell holds the integer rather than a float of it.
  ///
  /// Without this a QUAD load fell into the scalar path, which sizes a value from its bit width and
  /// would have minted ONE dword-sized register for it - half the value, silently, and the same
  /// truncation that was once found for LONG.
  /// </summary>
  private bool SelectQwordLoad(IrLoad load) {
    if (this.PointerMemory(load.Pointer, MRegSize.Qword) is not { } source)
      return false;
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    this._qslots[load] = slot;
    this.EmitX87(MOpcode.Fild, source, reads: true);
    this.EmitX87(MOpcode.Fistp, new MOperand.StackSlot(slot, MRegSize.Qword), reads: false);
    return true;
  }

  /// <summary>
  /// A QUAD written back to storage - four immediate words for a literal, and an <c>FILD</c> from
  /// the value's cell followed by <c>FISTP</c> into the destination for anything else.
  ///
  /// This is not an optional companion to <see cref="SelectQwordLoad"/>. Without it a QUAD store
  /// fell into the scalar path too, where the pointer was sized from the value's bit width: the
  /// destination became a DWORD cell and the value a single immediate, so the low half was written
  /// through a 386 operand-size prefix on a target that has no such instruction, and the high half
  /// was never written at all.
  /// </summary>
  private bool SelectQwordStore(IrStore store) {
    if (this.PointerMemory(store.Pointer, MRegSize.Word) is not { } words)
      return false;
    if (store.Value is IrConstantInt { Value: var value }) {
      for (var offset = 0; offset < 8; offset += 2)
        this.StoreWord(Shifted(words, offset), new MOperand.Immediate((short)(value >> (offset * 8))));
      return true;
    }
    if (!this._qslots.TryGetValue(store.Value, out var slot))
      return this.Decline($"store: 64-bit {store.Value.GetType().Name} has no cell");
    this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(slot, MRegSize.Qword), reads: true);
    this.EmitX87(MOpcode.Fistp, this.PointerMemory(store.Pointer, MRegSize.Qword)!, reads: false);
    return true;
  }

  private bool SelectFloatLoad(IrLoad load) {
    if (this.PointerMemory(load.Pointer, RegSize(load.Type)) is not { } source)
      return false;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.EmitX87(MOpcode.Fstp, this.FloatCell(load), reads: false);
    return true;
  }

  private bool SelectFloatStore(IrStore store) {
    if (this.PointerMemory(store.Pointer, RegSize(store.Value.Type)) is not { } destination)
      return false;
    if (!this.TryFloatOperand(store.Value, out var source))
      return false;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.EmitX87(MOpcode.Fstp, destination, reads: false);
    return true;
  }

  /// <summary>
  /// <c>SIToFP(FPToSI(x, i64), f)</c> - truncation toward zero, which is what the IR's
  /// <see cref="IrCastOp.FPToSI"/> means everywhere else: the constant folder answers it with a C
  /// cast, the C back end writes one, and LLVM's <c>fptosi</c> is defined that way.
  ///
  /// <para>
  /// The x87 has <b>no truncating store</b>. <c>FISTP</c> rounds by the control word, which is
  /// nearest-with-ties-to-even unless something changed it, so the qword round trip this used to
  /// emit answered <c>FIX(-1.5)</c> with <c>-2</c> - PB's answer is <c>-1</c>. It was invisible
  /// because every <c>FIX</c> in the corpus has a constant argument the folder reaches first.
  /// </para>
  ///
  /// <para>
  /// So it goes through <c>rt_trunc</c>, which is the routine the direct emitter's <c>FIX</c> calls:
  /// <c>FRNDINT</c> under RC=11 with the caller's control word restored afterwards. Not because a
  /// control-word bracket could not be selected here, but because the two paths emit into the SAME
  /// image and one program must not truncate two ways - the argument
  /// <see cref="MathSequence"/> already makes about SIN. Sharing the routine also settles the
  /// magnitudes a qword cannot hold: <c>FRNDINT</c> answers <c>FIX(1E30)</c> with <c>1E30</c> where
  /// <c>FISTP</c> stores the indefinite value.
  /// </para>
  ///
  /// <para>
  /// <c>INT</c> lowers through this same pair and stays correct across the change: it subtracts one
  /// when <c>x &lt; trunc(x)</c>, which is the floor under any rounding the round trip performs.
  /// <c>CINT</c> does not come here at all - it is <see cref="IrCastOp.FPToSIRound"/>, and
  /// <see cref="SelectFloatToInt"/> wants exactly the nearest-with-ties-to-even <c>FISTP</c> gives.
  /// </para>
  /// </summary>
  private bool SelectTruncationTowardZero(IrCast toInteger, IrCast backToFloat) {
    if (!this.TryFloatOperand(toInteger.Value, out var source))
      return false;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_trunc")],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));
    this.EmitX87(MOpcode.Fstp, this.FloatCell(backToFloat), reads: false);
    return true;
  }

  /// <summary>
  /// A float truncated toward zero into a QUAD the program keeps - the store half of a <c>FIX</c>
  /// cell, whose contents are the value scaled by <c>pbvFixDigits</c> and held as an int64.
  /// <see cref="SelectTruncationTowardZero"/> is the same conversion whose result goes straight back
  /// to a float; this one ends in the qword cell every other 64-bit value in this back end lives in,
  /// so <see cref="SelectQwordStore"/> and the QUAD argument path can read it.
  ///
  /// <para>
  /// <c>rt_trunc</c> for the reason stated there: <c>FISTP</c> rounds by the control word, so a bare
  /// qword store would answer <c>FIX(-1.5)</c> with <c>-2</c>. It costs a call the direct emitter's
  /// FIX store does not make - <c>rt_fixup</c> has already applied <c>FRNDINT</c> by the time the
  /// value arrives, so the truncation finds nothing to remove - and it is emitted anyway, because
  /// this arm is <see cref="IrCastOp.FPToSI"/> in general and not the FIX idiom in particular. A
  /// conversion that happens to be exact must not be the reason a rounding one is selected.
  /// </para>
  /// </summary>
  private bool SelectTruncationToQword(IrCast cast) {
    if (!this.TryFloatOperand(cast.Value, out var source))
      return false;
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    this._qslots[cast] = slot;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef("rt_trunc")],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));
    this.EmitX87(MOpcode.Fistp, new MOperand.StackSlot(slot, MRegSize.Qword), reads: false);
    return true;
  }

  /// <summary>
  /// An integer widened to a float. x87 reads its integers from memory, so the value is parked in a
  /// frame cell first - a word for an INTEGER, both halves of the pair for a LONG - and <c>FILD</c>
  /// reads it back at that width.
  /// </summary>
  private bool SelectIntToFloat(IrCast cast) {
    var from = cast.Value.Type;
    // A QUAD is already in a qword cell rather than in registers - the only integer width on this
    // target that is - so FILD reads it where it lies and there is nothing to stage. This is the load
    // half of a FIX cell (its contents scaled back down by rt_fixdn afterwards), and the mirror of
    // SelectTruncationToQword. Signed only, which IsQuad is: FILD reads the eight bytes as signed.
    if (IsQuad(from)) {
      if (!this.TryQwordSlot(cast.Value, out var qslot))
        return false;
      this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(qslot, MRegSize.Qword), reads: true);
      this.PopRounded(cast.Type, this.FloatCell(cast));
      return true;
    }
    if (!from.IsInteger || from.Bits is not (16 or 32))
      return this.Decline($"floating point: {cast.Op} from {from}");
    // FILD reads a SIGNED integer, so an unsigned source is staged one size LARGER than itself with
    // the extra half zeroed - the mirror of what SelectFloatToUnsigned does going the other way. A
    // DWORD's 4294967295 is negative read as a signed dword and itself read as a signed qword; the
    // zero above it is what makes the sign bit a value bit again.
    var unsignedWiden = !from.Signed;
    var slot = this._function.StackSlots.Count;
    var wide = IsWide(from);
    this._function.StackSlots.Add((wide ? 4 : 2) * (unsignedWiden ? 2 : 1));
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);
    if (wide) {
      if (!this.TryOperandPair(cast.Value, out var lo, out var hi))
        return false;
      this.StoreWord(cell, lo);
      this.StoreWord(Shifted(cell, 2), hi);
      if (unsignedWiden) {
        this.StoreWord(Shifted(cell, 4), new MOperand.Immediate(0));
        this.StoreWord(Shifted(cell, 6), new MOperand.Immediate(0));
      }
    } else {
      if (!this.TryOperand(cast.Value, out var value))
        return false;
      this.StoreWord(cell, value);
      if (unsignedWiden)
        this.StoreWord(Shifted(cell, 2), new MOperand.Immediate(0));
    }

    var read = (wide, unsignedWiden) switch {
      (true, true) => MRegSize.Qword,
      (true, false) => MRegSize.Dword,
      (false, true) => MRegSize.Dword,
      _ => MRegSize.Word,
    };
    this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(slot, read), reads: true);
    // A conversion INTO a narrow float rounds like any other narrow float result: a LONG above 2^24
    // has no exact SINGLE, and sitofp says which one it becomes (see PopRounded).
    this.PopRounded(cast.Type, this.FloatCell(cast));
    return true;
  }

  /// <summary>
  /// Extends a LONG/DWORD register pair into the qword-cell representation used for a signed QUAD.
  /// The low dword is copied word for word; the upper dword is either zero or the source sign word
  /// repeated twice. No 64-bit register is created or required.
  /// </summary>
  private bool SelectWideToQword(IrCast cast) {
    if (!this.TryOperandPair(cast.Value, out var low, out var high))
      return false;
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(8);
    this._qslots[cast] = slot;
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);
    this.StoreWord(cell, low);
    this.StoreWord(Shifted(cell, 2), high);

    MOperand extension = new MOperand.Immediate(0);
    if (cast.Op == IrCastOp.SExt) {
      var sign = new MOperand.Register(this.FreshVreg(IrType.I16));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [sign, high], MovEffect(sign, high)));
      this._current.Instructions.Add(new MInstr(MOpcode.Sar, [sign, new MOperand.Immediate(15)],
        new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
          ReadsMemory: false, WritesMemory: false)));
      extension = sign;
    }
    this.StoreWord(Shifted(cell, 4), extension);
    this.StoreWord(Shifted(cell, 6), extension);
    return true;
  }

  /// <summary>
  /// A float rounded into an integer, which is what BASIC does on assignment (<c>n% = 2.7</c> is 3).
  /// <c>FISTP</c> rounds by the x87 control word, and the runtime leaves it at its default of nearest
  /// with ties to even - so the instruction IS the semantics, no bias sequence needed.
  ///
  /// It stores through a <b>dword</b> even for an INTEGER and then keeps the low word, which is what
  /// the direct emitter does and for the same reason: an out-of-range value then wraps like a genuine
  /// 16-bit store rather than becoming FISTP's 8000h indefinite.
  /// </summary>
  private bool SelectFloatToInt(IrCast cast) {
    if (!this.TryFloatOperand(cast.Value, out var source))
      return false;
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(4);
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);

    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.EmitX87(MOpcode.Fistp, new MOperand.StackSlot(slot, MRegSize.Dword), reads: false);

    if (IsWide(cast.Type)) {
      var (lo, hi) = this.FreshPair(cast);
      this.LoadWord(lo, cell);
      this.LoadWord(hi, Shifted(cell, 2));
      return true;
    }
    var dest = this.FreshVreg(cast.Type);
    this._vregs[cast] = dest;
    this.LoadWord(new MOperand.Register(dest), cell);
    return true;
  }

  /// <summary>
  /// A float ROUNDED into an UNSIGNED integer - <c>FISTP</c>, nearest with ties to even, exactly as
  /// <see cref="SelectFloatToInt"/> does for a signed one, because PB rounds either way (its
  /// <c>b?? = 3.5</c> is 4, oracle-verified against genuine PBC 3.5 for BYTE, WORD and DWORD). See
  /// the note at the call site for why the staging cell is a size larger than the destination.
  /// </summary>
  private bool SelectFloatToUnsigned(IrCast cast) {
    if (!this.TryFloatOperand(cast.Value, out var source))
      return false;
    var wide = cast.Type.Bits == 32;
    var slot = this._function.StackSlots.Count;
    this._function.StackSlots.Add(wide ? 8 : 4);
    // The read is the DESTINATION's width, not the staging cell's - a BYTE target lands in AL, and
    // asking for a word there is an operand-size mismatch rather than a wrong answer.
    var cell = new MOperand.StackSlot(slot, wide ? MRegSize.Word : RegSize(cast.Type));

    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.EmitX87(MOpcode.Fistp, new MOperand.StackSlot(slot, wide ? MRegSize.Qword : MRegSize.Dword), reads: false);

    if (wide) {
      var (lo, hi) = this.FreshPair(cast);
      this.LoadWord(lo, cell);
      this.LoadWord(hi, Shifted(cell, 2));
      return true;
    }
    var dest = this.FreshVreg(cast.Type);
    this._vregs[cast] = dest;
    this.LoadWord(new MOperand.Register(dest), cell);
    return true;
  }

  private void LoadWord(MOperand.Register dest, MOperand cell)
    => this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, cell],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false)));

  /// <summary>A float widened or narrowed to the other float format: the load and the store do it.</summary>
  /// <summary>
  /// <c>fpext</c> and <c>fptrunc</c>. Widening needs no instruction of its own - every float value
  /// here lives in a ten-byte cell at the x87's own width, and the wider format holds the narrower
  /// one exactly - but NARROWING has to round, and that is what a copy between two ten-byte cells
  /// does not do.
  ///
  /// <para>
  /// The round trip through a cell of the TARGET width <b>is</b> the rounding, and it is the
  /// <c>FSTP m32 / FLD m32</c> pair the direct emitter writes when a value is stored into a SINGLE
  /// variable. <see cref="FloatCell"/> explains why an INTERMEDIATE deliberately keeps all eighty
  /// bits, and says a store to a declared variable rounds because it goes through the variable's own
  /// cell - which stops being true the moment <c>mem2reg</c> promotes that variable, since then there
  /// is no cell and the <c>fptrunc</c> the lowering emitted is the only thing left that says SINGLE.
  /// Eliding it left <c>D! = p / q</c> holding the quotient at eighty bits: <c>1.66666666666667</c>
  /// where PB (and the direct emitter) answer <c>1.66666662693024</c>.
  /// </para>
  /// </summary>
  private bool SelectFloatResize(IrCast cast) {
    if (!this.TryFloatOperand(cast.Value, out var source))
      return false;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.PopRounded(cast.Type, this.FloatCell(cast));
    return true;
  }

  /// <summary>
  /// Pops the x87 top into <paramref name="destination"/> at the width <paramref name="type"/> names,
  /// which for anything narrower than the register's own eighty bits means a round trip through a cell
  /// of that width first - the <c>FSTP m32 / FLD m32</c> pair the direct emitter writes when a value
  /// is stored into a SINGLE variable.
  ///
  /// <para>
  /// This is the one place the ten-byte cell doctrine has to be read carefully.
  /// <see cref="FloatCell"/> parks every value at the x87's own width because PB computes an
  /// expression at the register's width and lets the declared type pick only the FORMATTER - which is
  /// why <c>H? / 3</c> prints 66.66667 and not 66.66666. That is a statement about the IR the lowering
  /// writes, and the lowering says it by TYPING those intermediates <c>x86_fp80</c>: every ordinary PB
  /// float expression comes through as f80 arithmetic with an <c>fptrunc</c> at the store. So a value
  /// the IR types f32 or f64 is not an intermediate PB left wide - it is a place the front end (or a
  /// middle-end pass) has already decided a rounding happens, and dropping it is a miscompile in the
  /// other direction.
  /// </para>
  /// <para>
  /// Both spellings of that decision were being ignored. <c>fptrunc</c> was one, and a float
  /// arithmetic instruction typed narrower than f80 is the other: <c>FOR x! = 0 TO 1 STEP .1</c>
  /// increments through <c>fadd float</c> - the counter is a SINGLE and <c>mem2reg</c> has taken its
  /// four-byte cell away - and computing that at eighty bits accumulated a different sum, 4.5000000670
  /// against the 4.5000002607 genuine PBC 3.50 and the direct emitter both answer.
  /// </para>
  /// </summary>
  private void PopRounded(IrType type, MOperand destination) {
    if (RegSize(type) is var narrow and (MRegSize.Dword or MRegSize.Qword)) {
      var slot = this._function.StackSlots.Count;
      this._function.StackSlots.Add(narrow == MRegSize.Dword ? 4 : 8);
      this.EmitX87(MOpcode.Fstp, new MOperand.StackSlot(slot, narrow), reads: false);
      this.EmitX87(MOpcode.Fld, new MOperand.StackSlot(slot, narrow), reads: true);
    }
    this.EmitX87(MOpcode.Fstp, destination, reads: false);
  }

  private void StoreWord(MOperand cell, MOperand value)
    => this._current.Instructions.Add(new MInstr(MOpcode.Mov, [cell, value],
      new MInstrEffect([], value is MOperand.Register ? [1] : [], false, false, false, WritesMemory: true)));

  /// <summary>An x87 instruction with a single memory operand it either reads or writes.</summary>
  private void EmitX87(MOpcode opcode, MOperand cell, bool reads)
    => this._current.Instructions.Add(new MInstr(opcode, [cell],
      new MInstrEffect([], [], ReadsFlags: false, WritesFlags: false, ReadsMemory: reads, WritesMemory: !reads)));

  #endregion

  /// <summary>A PUSH of one argument word, with the effect descriptor that keeps it ordered against the call.</summary>
  private static MInstr PushOf(MOperand operand) => new(MOpcode.Push, [operand],
    new MInstrEffect(WrittenRegs: [], ReadRegs: operand is MOperand.Register ? [0] : [],
      ReadsFlags: false, WritesFlags: false,
      ReadsMemory: operand is MOperand.Memory or MOperand.StackSlot or MOperand.DataCell or MOperand.ParamCell,
      WritesMemory: true));

  /// <summary>Every allocatable register a CALL destroys under this ABI - the callee saves none of them.</summary>
  private static readonly Reg[] _callClobbers = [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  private bool SelectRet(IrRet ret, MBlock block) {
    if (ret.HasValue && ret.Value is { } wide && IsWide(wide.Type)) {
      // the PB convention returns a LONG in DX:AX (docs: "Results: AX / DX:AX / ST0 / string handle in AX")
      if (!this.TryOperandPair(wide, out var lo, out var hi))
        return false;
      var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
      var dx = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, lo], MovEffect(ax, lo)));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dx, hi], MovEffect(dx, hi)));
      this._current.Instructions.Add(ReturningIn(ax, dx));
      return true;
    }
    if (ret.HasValue && ret.Value is { Type.IsFloat: true } floating) {
      // "Results: AX / DX:AX / ST0 / string handle in AX" - a float goes back on the x87 stack, so
      // the value is loaded and deliberately NOT popped: the caller pops it
      if (!this.TryFloatOperand(floating, out var cell))
        return false;
      this.EmitX87(MOpcode.Fld, cell, reads: true);
      this._current.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
      return true;
    }
    if (ret.HasValue && ret.Value is { } value) {
      // the result is returned in AX (word) - a physical pin the allocator must honour
      var ax = MReg.Physical_(Reg.AX, RegSize(value.Type));
      var axOp = new MOperand.Register(ax);
      if (!this.TryOperand(value, out var src))
        return false;
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [axOp, src], MovEffect(axOp, src)));
      this._current.Instructions.Add(ReturningIn(axOp));
      return true;
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    return true;
  }

  /// <summary>
  /// A <c>RET</c> that says which physical registers the result is leaving in. The emitter never looks
  /// at a RET's operands - it special-cases the opcode and writes the epilogue - so these are here for
  /// the ANALYSES, and one of them needs them badly.
  ///
  /// <para>
  /// <c>LinearScanAllocator.InFlightByIndex</c> protects a physical register over the window between
  /// the instruction that fills it and the instruction that names it as a read. Without a reader there
  /// is no window: the <c>MOV AX, v</c> that places a result was the last mention of AX in the block,
  /// so the allocator was free to hand AX to a value defined after it. It did, on the very next
  /// instruction. <c>FUNCTION Live&amp;(BYVAL a&amp;) : Live&amp; = a&amp; + 1 : PRINT "out" : END FUNCTION</c>
  /// spills the result across the PRINT, and the two reloads come back as
  /// <c>MOV AX,[lo] / MOV AX,AX / MOV AX,[hi] / MOV DX,AX</c> - the high half overwriting the low one
  /// on the way out. The function returned 0 for every input, in both optimizer modes.
  /// </para>
  /// <para>
  /// A float result rides the x87 stack and a SUB returns nothing, so both keep the bare form.
  /// </para>
  /// </summary>
  private static MInstr ReturningIn(params MOperand.Register[] registers)
    => new(MOpcode.Ret, registers,
      new MInstrEffect(WrittenRegs: [], ReadRegs: [.. Enumerable.Range(0, registers.Length)],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false));

  // ---- operand / vreg helpers -------------------------------------------------------------------

  private MReg FreshVreg(IrType type) => MReg.Virtual(this._nextVreg++, RegSize(type));

  /// <summary>True for a 32-bit integer, represented by a baseline pair or an eligible 386 dword.</summary>
  private static bool IsWide(IrType type) => type.IsInteger && type.Bits == 32;

  /// <summary>Mints the low/high register pair for a 32-bit value and records both halves.</summary>
  private (MOperand.Register Lo, MOperand.Register Hi) FreshPair(IrValue value) {
    var lo = MReg.Virtual(this._nextVreg++, MRegSize.Word);
    var hi = MReg.Virtual(this._nextVreg++, MRegSize.Word);
    this._vregs[value] = lo;
    this._hiVregs[value] = hi;
    return (new MOperand.Register(lo), new MOperand.Register(hi));
  }

  /// <summary>Both halves of a 32-bit value as operands, declining when it has no register pair.</summary>
  private bool TryOperandPair(IrValue value, out MOperand lo, out MOperand hi) {
    if (value is IrConstantInt c) {
      lo = new MOperand.Immediate((short)(c.Value & 0xFFFF));
      hi = new MOperand.Immediate((short)((c.Value >> 16) & 0xFFFF));
      return true;
    }
    if (this.UsesNativeDwordRegisters
        && this._vregs.TryGetValue(value, out var native) && native.Size == MRegSize.Dword) {
      var slot = this._function.StackSlots.Count;
      this._function.StackSlots.Add(4);
      var cell = new MOperand.StackSlot(slot, MRegSize.Dword);
      var source = new MOperand.Register(native);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [cell, source],
        new MInstrEffect([], [1], false, false, false, WritesMemory: true)));
      lo = new MOperand.StackSlot(slot, MRegSize.Word);
      hi = new MOperand.StackSlot(slot, MRegSize.Word, 2);
      return true;
    }
    lo = hi = null!;
    if (!this._vregs.TryGetValue(value, out var loReg) || !this._hiVregs.TryGetValue(value, out var hiReg))
      return this.Decline($"32-bit operand: {value.GetType().Name} has no register pair");
    lo = new MOperand.Register(loReg);
    hi = new MOperand.Register(hiReg);
    return true;
  }

  /// <summary>A whole 386 dword operand, without manufacturing one from a baseline word pair.</summary>
  private bool TryNativeDwordOperand(IrValue value, out MOperand operand) {
    if (!this.UsesNativeDwordRegisters) {
      operand = null!;
      return false;
    }
    if (value is IrConstantInt constant) {
      operand = new MOperand.Immediate((int)constant.Value);
      return true;
    }
    if (this._vregs.TryGetValue(value, out var register) && register.Size == MRegSize.Dword) {
      operand = new MOperand.Register(register);
      return true;
    }
    operand = null!;
    return false;
  }

  private static MInstrEffect PairEffect(MOperand rhs, bool readsFlags, bool writesFlags) =>
    new(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],
      ReadsFlags: readsFlags, WritesFlags: writesFlags, ReadsMemory: rhs.IsMemoryAccess(), WritesMemory: false);

  /// <summary>
  /// A constant integer as an immediate, in THIS TARGET's spelling of it.
  ///
  /// <para>
  /// <c>i1</c> is the one type whose IR value and machine value differ. The IR writes truth as 1;
  /// every comparison this back end materializes writes BASIC's full word of -1 (see
  /// <see cref="SelectCmpValue"/> and <see cref="RegSize"/>), and the whole file is built on that.
  /// So a bool CONSTANT has to be widened to the same shape, or a bitwise operation mixing a
  /// computed bool with a literal one answers a third thing: <c>xor i1 %c, true</c> - which is how
  /// both <c>IrLowering</c> and <c>InstCombine</c> spell a logical NOT - became <c>XOR reg, 1</c>,
  /// turning -1 into -2. Still non-zero, so the negation of a true condition stayed true.
  /// </para>
  /// <para>
  /// What that cost: <c>FOR i = a TO b STEP s</c> with a RUNTIME step tests
  /// <c>(s &gt;= 0 AND i &lt;= limit) OR (s &lt; 0 AND i &gt;= limit)</c>, and the second conjunct's
  /// negated guard never went false - so an ASCENDING loop with a runtime step never terminated,
  /// with the optimizer on or off. A descending one was correct throughout, which is what kept it
  /// out of sight.
  /// </para>
  /// </summary>
  private static MOperand.Immediate ImmediateOf(IrConstantInt constant)
    => new(constant.Type.IsBool ? (constant.Value != 0 ? -1 : 0) : constant.Value);

  /// <summary>An SSA value as a machine operand: a constant is an immediate, anything else its virtual register.</summary>
  private MOperand Operand(IrValue value)
    => value is IrConstantInt c ? ImmediateOf(c) : new MOperand.Register(this._vregs[value]);

  /// <summary>
  /// An SSA value as a machine operand, declining instead of throwing when it has no virtual
  /// register. Not every operand is one: an <see cref="IrGlobalVariable"/> is a data label (a
  /// module-level or SHARED variable), an <see cref="IrConstantFloat"/> needs an x87 constant, and
  /// <see cref="IrUndef"/> has no value at all. None of those are selectable yet, and a back end
  /// that throws on them would crash the compiler where falling back to the direct codegen is
  /// correct - so every operand goes through here.
  /// </summary>
  private bool TryOperand(IrValue value, out MOperand operand) {
    switch (value) {
      case IrConstantInt c:
        operand = ImmediateOf(c);
        return true;
      // A null pointer IS zero on this target - a string handle of 0 is the empty string, which is
      // what a string variable holds before its first assignment.
      case IrNullPtr:
        operand = new MOperand.Immediate(0);
        return true;
      // A global's VALUE is its ADDRESS - MOV reg, OFFSET name - which is what DataOffset is. The
      // test is IsAddressableGlobal rather than a fresh list of prefixes, and deliberately the same
      // one PointerMemory uses to turn a global into a DataCell: having a cell and having an offset
      // are the same fact, so a second spelling of the question could only drift away from the first
      // and answer it differently for some name neither list was written with in mind.
      case IrGlobalVariable g when IsAddressableGlobal(g):
        operand = new MOperand.DataOffset(g.Name, 0);
        return true;
      case IrGlobalVariable g:
        operand = null!;
        return this.Decline($"operand: global '{g.Name}' (no module symbol to address)");
      default:
        if (this._vregs.TryGetValue(value, out var reg)) {
          operand = new MOperand.Register(reg);
          return true;
        }
        operand = null!;
        return this.Decline($"operand: {value.GetType().Name} has no register");
    }
  }

  /// <summary>
  /// A pointer value as a memory operand: <c>[ptrReg]</c> for an address held in a register, or the
  /// named data cell for a module-level variable - which the whole-program codegen resolves to the
  /// very <c>Mem</c> the direct emitter uses, so both paths address the same storage.
  ///
  /// Reading that cell is sound because a global a procedure can see is <c>SHARED</c>, and
  /// <c>SsaForm.IsTrackableShape</c> excludes SHARED variables from SSA tracking - so no store to it
  /// is ever elided and no read is ever folded away. Register residency cannot strand a value there
  /// either: it requires an SI/DI-clean region, and a call is not clean.
  /// </summary>
  private MOperand? PointerMemory(IrValue pointer, MRegSize size) {
    // A SINGLE-slot alloca is addressed as the slot itself rather than through the register its LEA
    // put the address in. Nothing indexes it - only a multi-slot block needs a base to walk from - and
    // the register costs real allocations: it is live wherever the variable is used, a value used as a
    // memory BASE is the one thing the spiller cannot move, and any instruction clobbering the whole
    // register file in between then has nowhere to put it. Inline asm is exactly such an instruction.
    if (pointer is IrFarPtr far)
      return this.FarMemory(far, size);
    if (pointer is IrAlloca { Count: 1 } scalar && this._slots.TryGetValue(scalar, out var own))
      return new MOperand.StackSlot(own, size);
    if (this._vregs.TryGetValue(pointer, out var reg))
      return new MOperand.Memory(reg, null, 1, 0, size, SegmentCell: SegmentCellOf(pointer.Type));
    if (pointer is IrGlobalVariable g) {
      // A source global or STATIC maps back to the symbol the codegen laid out, and an rt_ global IS
      // the runtime's own named cell. Synthesized globals such as .data_cursor still have no cell.
      if (!IsAddressableGlobal(g)) {
        this.Decline($"pointer: global '{g.Name}' (no module symbol to address)");
        return null;
      }
      return new MOperand.DataCell(g.Name, 0, size);
    }
    this.Decline($"pointer: {pointer.GetType().Name} has no register");
    return null;
  }

  /// <summary>
  /// A <see cref="IrFarPtr"/> as the memory operand of the access that reads or writes through it:
  /// <c>ES:[offset]</c>, with the segment named by the operand so the emitter loads <c>ES</c> right in
  /// front of it.
  ///
  /// <para>
  /// Both halves are materialized into registers here rather than reused from wherever the far pointer
  /// was formed, because <c>MOV ES, imm</c> does not exist - a constant segment, which is what
  /// <c>DIM a(...) AT &amp;HB800</c> gives, still has to travel through a general register. The offset
  /// register is an ordinary memory base and the allocator constrains it accordingly; the segment
  /// register is not, and is only required to be a word.
  /// </para>
  /// </summary>
  private MOperand? FarMemory(IrFarPtr far, MRegSize size) {
    if (!this.TryOperand(far.Segment, out var segment) || !this.TryOperand(far.Offset, out var offset))
      return null;
    var segmentReg = this.FreshVreg(IrType.I16);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(segmentReg), segment],
      MovEffect(new MOperand.Register(segmentReg), segment)));
    // a constant offset is a DISPLACEMENT, not a register - ES:[0020h] is an addressing mode, and
    // spending a register on it would cost one at every element of a fixed-subscript sequence
    if (offset is MOperand.Immediate constant)
      return new MOperand.Memory(null, null, 1, unchecked((short)constant.Value), size, segmentReg);
    if (offset is not MOperand.Register offsetReg) {
      var fresh = this.FreshVreg(IrType.I16);
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [new MOperand.Register(fresh), offset],
        MovEffect(new MOperand.Register(fresh), offset)));
      offsetReg = new MOperand.Register(fresh);
    }
    return new MOperand.Memory(offsetReg.Reg, null, 1, 0, size, segmentReg);
  }
  /// <summary>
  /// The runtime cell holding the segment a pointer of this type is relative to, or null when it is
  /// relative to the segment the instruction would use anyway.
  ///
  /// One address space needs one, and it is the only memory the program reaches that is not its own:
  /// dynamic array storage, which the runtime bump-allocates out of the far array heap at
  /// <c>rt_arrseg</c>. The direct emitter loads <c>ES</c> from the same cell before every element
  /// access; this is that instruction, arrived at from the type rather than from the statement.
  /// </summary>
  private static string? SegmentCellOf(IrType type) => type.IsFarPointer ? "rt_arrseg" : null;
  private static bool IsAddressableGlobal(IrGlobalVariable global)
    => global.Name.StartsWith("g.", System.StringComparison.Ordinal)
       || global.Name.StartsWith("static.", System.StringComparison.Ordinal)
       || global.Name.StartsWith("rt_", System.StringComparison.Ordinal)
       // the IR's own DATA pool and read cursor, emitted beside the direct emitter's pair rather
       // than shared with it - see CodeGenerator.DataCellOf
       || global.Name is ".data" or ".data_cursor";

  /// <summary>The same cell shifted by <paramref name="delta"/> bytes - the high word of a 32-bit access.</summary>
  private static MOperand Shifted(MOperand cell, int delta) => cell switch {
    MOperand.Memory m => m with { Disp = m.Disp + delta },
    MOperand.DataCell d => d with { Disp = d.Disp + delta },
    MOperand.StackSlot s => s with { Disp = s.Disp + delta },
    _ => cell,
  };

  private static MInstrEffect MovEffect(MOperand.Register dest, MOperand src)
    => new(WrittenRegs: [0], ReadRegs: src is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: src.IsMemoryAccess(), WritesMemory: false);

  private static bool TryMapBinary(IrBinaryOp op, out MOpcode opcode) {
    opcode = op switch {
      IrBinaryOp.Add => MOpcode.Add,
      IrBinaryOp.Sub => MOpcode.Sub,
      IrBinaryOp.And => MOpcode.And,
      IrBinaryOp.Or => MOpcode.Or,
      IrBinaryOp.Xor => MOpcode.Xor,
      IrBinaryOp.Mul => MOpcode.Imul,
      IrBinaryOp.Shl => MOpcode.Shl,
      IrBinaryOp.LShr => MOpcode.Shr,
      IrBinaryOp.AShr => MOpcode.Sar,
      _ => MOpcode.Ret,   // sentinel for "unsupported"
    };
    return opcode != MOpcode.Ret;
  }

  /// <summary>
  /// The machine register size a value of this type occupies. A pointer carries no bit width in the
  /// IR (it is a target property), and on x86-16 it is a 2-byte near offset - so it is a word, not
  /// the byte a naive width test would give it, which would size its loads and stores wrongly.
  /// </summary>
  /// <summary>
  /// The width a value of this type occupies in memory.
  ///
  /// Floats are sized by their OWN width rather than falling into the integer ladder's Dword. That
  /// ladder is right for integers, where a 32-bit value lives in a register PAIR and each half is
  /// addressed a word at a time, and wrong for every float wider than a SINGLE: a DOUBLE addressed as
  /// a dword is half a value and an EXTENDED is less than half of one. Nothing had caught it because
  /// no routed corpus program had yet spilled a DOUBLE temporary.
  /// </summary>
  private static MRegSize RegSize(IrType type) {
    if (type.IsPointer)
      return MRegSize.Word;
    if (type.Kind == IrTypeKind.Float)
      return type.Bits switch { <= 32 => MRegSize.Dword, <= 64 => MRegSize.Qword, _ => MRegSize.Tbyte };
    if (type.IsBool)
      return MRegSize.Word;                   // BASIC truth is the full word -1/0, even though IR uses i1
    return type.Bits switch {
      <= 8 => MRegSize.Byte,
      <= 16 => MRegSize.Word,
      _ => MRegSize.Dword,
    };
  }

  private static int SizeOf(IrType type)
    => type.IsPointer || type.IsBool ? 2 : System.Math.Max(1, (type.Bits + 7) / 8);
}
