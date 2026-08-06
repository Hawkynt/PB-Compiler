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
public sealed class InstructionSelector {

  private readonly Dictionary<IrValue, MReg> _vregs = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The HIGH word of a 32-bit value. x86-16 has no 32-bit register, so a LONG/DWORD lives in a
  /// register <b>pair</b>: <see cref="_vregs"/> holds its low word and this its high one. Keeping the
  /// halves as two ordinary virtual registers means the allocator needs no notion of pairing - it
  /// allocates and spills them independently, and only the ABI-pinned spots (a LONG result in DX:AX)
  /// name physical registers.
  /// </summary>
  private readonly Dictionary<IrValue, MReg> _hiVregs = new(ReferenceEqualityComparer.Instance);
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

  /// <summary>Selects a function into machine IR, or null if it contains a construct this stage cannot model.</summary>
  public static MFunction? TrySelect(IrFunction fn) => TrySelect(fn, out _);

  /// <summary>
  /// Selects a function into machine IR, reporting <paramref name="declineReason"/> - the construct that
  /// stopped it - when the result is null. The reason is what the coverage census reads to rank which
  /// widening buys the most eligible functions, so it names the IR construct, not the failing routine.
  /// </summary>
  public static MFunction? TrySelect(IrFunction fn, out string? declineReason) {
    declineReason = null;
    if (fn.IsDeclaration || fn.Entry is null) {
      declineReason = "declaration";
      return null;
    }
    var selector = new InstructionSelector();
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
        else if (IsWide(phi.Type))
          this.FreshPair(phi);                 // a 32-bit phi needs both halves, like any other LONG value
        else
          this._vregs[phi] = this.FreshVreg(phi.Type);

    var mblocks = new Dictionary<string, MBlock>();
    foreach (var block in fn.Blocks) {
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
    return this._function;
  }

  /// <summary>
  /// Out-of-SSA: for every phi, copy each incoming value into the phi's register at the end of the
  /// corresponding predecessor block (before its terminator). Conservatively declines when the copies
  /// on one edge form a cycle (a copy reads a register another copy on the same edge overwrites) - the
  /// swap would need a temporary, a later refinement.
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

      // a cycle on this edge (one copy's source is another copy's destination) needs a temporary - decline
      var destinations = copies.Select(c => c.Dest).ToHashSet();
      if (copies.Any(c => c.Source is MOperand.Register r && destinations.Contains(r.Reg) && !r.Reg.Equals(c.Dest)))
        return this.Decline("phi: copy cycle on an edge (a swap needs a temporary)");

      var mblock = mblocks[predBlock.Label];
      var insertAt = mblock.Instructions.FindIndex(i => i.IsTerminator);
      if (insertAt < 0)
        insertAt = mblock.Instructions.Count;
      foreach (var (dest, source) in copies) {
        var copy = new MInstr(MOpcode.Mov, [new MOperand.Register(dest), source],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: source is MOperand.Register ? [1] : [],
            ReadsFlags: false, WritesFlags: false, ReadsMemory: source is MOperand.Memory, WritesMemory: false));
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
        if (lhs is not MOperand.Register)
          return this.Decline("compare: immediate left operand");   // CMP needs a register/memory left operand
        this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
          new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
            ReadsMemory: lhs is MOperand.Memory || rhs is MOperand.Memory, WritesMemory: false)));
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
      default:
        return this.Decline($"terminator: {terminator?.GetType().Name ?? "none"}"
          + (terminator is IrCondBr ? " (condition is not a folded compare)" : ""));
    }
  }

  /// <summary>The read-operand indices for a two-operand CMP: operand 0 (the left) and operand 1 when it is a register.</summary>
  private static int[] RegReadIndices(MOperand left, MOperand right)
    => right is MOperand.Register ? [0, 1] : left is MOperand.Register ? [0] : [];

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
    if (bin.Op is IrBinaryOp.SDiv or IrBinaryOp.SRem)
      return this.SelectDivide(bin, block);
    if (!TryMapBinary(bin.Op, out var opcode))
      return this.Decline($"binary: {bin.Op}");   // unsigned divide / remainder - not in this increment
    if (IsWide(bin.Type))
      return this.SelectWideBinary(bin, opcode, block);

    // two-address form: dest = lhs; dest <op>= rhs
    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryOperand(bin.Lhs, out var lhs) || !this.TryOperand(bin.Rhs, out var rhs))
      return false;
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, lhs], MovEffect(destOp, lhs)));
    // the two-operand IMUL has no immediate form - materialize an immediate multiplier in a register
    if (opcode == MOpcode.Imul && rhs is MOperand.Immediate) {
      var tmp = new MOperand.Register(this.FreshVreg(bin.Type));
      this._current.Instructions.Add(new MInstr(MOpcode.Mov, [tmp, rhs], MovEffect(tmp, rhs)));
      rhs = tmp;
    }
    this._current.Instructions.Add(new MInstr(opcode, [destOp, rhs],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],
        ReadsFlags: false, WritesFlags: true, ReadsMemory: rhs is MOperand.Memory, WritesMemory: false)));
    return true;
  }

  /// <summary>
  /// A 32-bit add/subtract/bitwise op over register pairs: the low halves combine first and the high
  /// halves follow, with <c>ADC</c>/<c>SBB</c> threading the carry for add and subtract. Multiply,
  /// divide and the shifts need a runtime helper or a CL count and are declined.
  /// </summary>
  private bool SelectWideBinary(IrBinary bin, MOpcode opcode, MBlock block) {
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
  private bool SelectWideShift(IrBinary bin, MOpcode opcode, MBlock block) {
    if (bin.Rhs is not IrConstantInt { Value: var count } || count is < 0 or > 8)
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
  /// an <c>$ERROR</c> option - so only a <b>non-zero compile-time constant</b> divisor is selected
  /// here, which is precisely the case where the direct emitter also drops the guard (O0220): a
  /// constant that cannot be zero cannot trap. A runtime 16-bit divisor still declines; the 32-bit
  /// helper owns the guard and calls <c>rt_raise</c> with Error 11.
  /// </summary>
  private bool SelectDivide(IrBinary bin, MBlock block) {
    if (IsWide(bin.Type))
      return this.SelectWideDivide(bin);
    if (bin.Type.Bits != 16)
      return this.Decline($"binary: {bin.Op} on {bin.Type} (16-bit only)");
    if (bin.Rhs is not IrConstantInt { Value: var divisor } || divisor == 0)
      return this.Decline($"binary: {bin.Op} by a runtime divisor (needs the Error-11 guard)");
    if (divisor == -1)
      return this.Decline($"binary: {bin.Op} by -1 (MININT / -1 overflows IDIV)");
    if (!this.TryOperand(bin.Lhs, out var dividend))
      return false;

    // the divisor must be a register or memory - IDIV has no immediate form
    var divisorReg = new MOperand.Register(this.FreshVreg(bin.Type));
    var divisorImm = new MOperand.Immediate(divisor);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [divisorReg, divisorImm], MovEffect(divisorReg, divisorImm)));

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

    // quotient in AX, remainder in DX
    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    var result = new MOperand.Register(MReg.Physical_(bin.Op == IrBinaryOp.SDiv ? Reg.AX : Reg.DX, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, result], MovEffect(destOp, result)));
    return true;
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
  /// </summary>
  private bool SelectInlineAsm(IrInlineAsm asm) {
    if (!asm.Routable)
      return this.Decline("inline asm: a name in it is not a variable this pass could bind");

    var operands = new List<MOperand> { new MOperand.InlineAsmText(asm.Text, asm.Names) };
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
    IrGlobalVariable g when g.Name.StartsWith("g.", System.StringComparison.Ordinal)
                            || g.Name.StartsWith("rt_", System.StringComparison.Ordinal)
      => new MOperand.DataCell(g.Name, 0, MRegSize.Word),
    _ => this.DeclineCell(pointer),
  };

  private MOperand? DeclineCell(IrValue pointer) {
    this.Decline($"inline asm: '{pointer.Name ?? pointer.GetType().Name}' has no frame cell to name");
    return null;
  }

  private bool SelectStore(IrStore store, MBlock block) {
    if (store.Value.Type.IsFloat)
      return this.Decline($"floating point: {store.Value.Type} through the scalar path");
    if (IsWide(store.Value.Type)) {
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
    if (gep.ElementType is not null)
      return this.Decline("gep: element-indexed");   // byte-offset only in this increment

    var dest = this.FreshVreg(IrType.Ptr);
    this._vregs[gep] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryOperand(gep.BasePtr, out var baseOp))
      return false;
    if (baseOp is not MOperand.Register baseReg)
      return this.Decline("gep: non-register base");
    // LEA dest, [base + offset]: a constant offset folds into the displacement, a register offset becomes the index
    MOperand.Memory mem = gep.ByteOffset is IrConstantInt c
      ? new(baseReg.Reg, null, 1, (int)c.Value, MRegSize.Word)
      : this.TryOperand(gep.ByteOffset, out var offset) && offset is MOperand.Register idx
        ? new(baseReg.Reg, idx.Reg, 1, 0, MRegSize.Word)
        : null!;
    if (mem is null)
      return this.Decline("gep: offset is neither a constant nor a register");
    this._current.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
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
  /// A 32-bit comparison materialized as PowerBASIC's -1/0 truth value. There is no 32-bit CMP on this
  /// target, so it becomes a compare of the high words, then - only when those are equal - a compare of
  /// the low ones:
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
    if (WideConditions(cmp.Pred) is not { } conditions)
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
        ReadsMemory: left is MOperand.Memory || right is MOperand.Memory, WritesMemory: false)));

  private void EmitBranch(Condition condition, string target)
    => this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(target)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false), condition));

  private bool SelectCmpValue(IrCmp cmp) {
    if (cmp.Lhs.Type.IsFloat)
      return this.SelectFloatCmpValue(cmp);
    if (IsWide(cmp.Lhs.Type))
      return this.SelectWideCmpValue(cmp);
    if (MapPredicate(cmp.Pred) is not { } cc)
      return this.Decline($"compare as a value: {cmp.Pred}");
    if (!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, out var rhs))
      return false;
    if (lhs is not MOperand.Register)
      return this.Decline("compare as a value: immediate left operand");

    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
      new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
        ReadsMemory: lhs is MOperand.Memory || rhs is MOperand.Memory, WritesMemory: false)));
    return this.MaterializeCondition(cmp, cc);
  }

  private bool SelectFloatCmpValue(IrCmp cmp) {
    if (MapFloatPredicate(cmp.Pred) is not { } cc)
      return this.Decline($"compare as a value: float {cmp.Pred}");
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
    if (IsWide(sel.Type) || sel.Type.IsFloat)
      return this.Decline($"select: {sel.Type} result");
    if (!this.TryOperand(sel.Condition, out var cond)
        || !this.TryOperand(sel.IfTrue, out var ifTrue)
        || !this.TryOperand(sel.IfFalse, out var ifFalse))
      return false;
    if (cond is not MOperand.Register)
      return this.Decline("select: condition is not in a register");

    var dest = this.FreshVreg(sel.Type);
    this._vregs[sel] = dest;
    var destOp = new MOperand.Register(dest);

    var falseBlock = new MBlock($"{this._current.Label}.selfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.seldone{this._splitCount}");
    ++this._splitCount;

    var zero = new MOperand.Immediate(0);
    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [cond, zero],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false)));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ifTrue], MovEffect(destOp, ifTrue)));
    this._current.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(doneBlock.Label)],
      new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false),
      Condition.NotEqual));
    this._current.Successors.Add(doneBlock.Label);
    this._current.Successors.Add(falseBlock.Label);

    falseBlock.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ifFalse], MovEffect(destOp, ifFalse)));
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
        this._current.Instructions.Add(new MInstr(MOpcode.Sar, [hi, new MOperand.Immediate(15)],
          new MInstrEffect(WrittenRegs: [0], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
            ReadsMemory: false, WritesMemory: false)));
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
      case IrCastOp.SIToFP when to.IsIeeeFloat:
        return this.SelectIntToFloat(cast);
      case IrCastOp.FPToSIRound when from.IsIeeeFloat && to.IsInteger && to.Bits is 16 or 32:
        return this.SelectFloatToInt(cast);
      case IrCastOp.FPExt or IrCastOp.FPTrunc when from.IsIeeeFloat && to.IsIeeeFloat:
        return this.SelectFloatResize(cast);
      default:
        return this.Decline($"cast: {cast.Op} {from} -> {to}");
    }
  }

  /// <summary>
  /// A direct call to a defined procedure, in the BASIC/PASCAL convention the direct codegen emits:
  /// arguments pushed <b>left to right</b>, <c>CALL</c>, and the callee cleaning them with its
  /// <c>RET n</c> - so a back-end function and a directly-emitted one call each other unchanged.
  /// The result arrives in <c>AX</c>.
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

  private bool SelectCall(IrCall call, MBlock block) {
    if (call.Callee is not IrFunction callee)
      return this.Decline("call: indirect (through a procedure pointer)");
    if (callee.IsDeclaration)
      return ErrorHandlerIntrinsics.Contains(callee.Name)
        ? this.SelectErrorHandlerIntrinsic(call, callee)
        : MathSequence(callee.Name) is { } sequence
          ? this.SelectMathIntrinsic(call, callee, sequence)
          : RuntimeAbi.For(callee.Name) is { } routine
            ? this.SelectRuntimeCall(call, callee, routine)
            : this.Decline($"call: {callee.Name} (runtime declaration - not in the runtime ABI table)");
    if (!call.Type.IsVoid && !call.Type.IsIeeeFloat && !IsWide(call.Type)
        && RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: {callee.Name} returns {call.Type} (unsupported result shape)");

    foreach (var arg in call.Args) {
      if (arg.Type.IsIeeeFloat) {
        if (!this.TryFloatOperand(arg, out var source))
          return false;
        var bytes = arg.Type.Bits / 8;
        if (bytes is not (4 or 8))
          return this.Decline($"call: {callee.Name} takes {arg.Type} (only SINGLE/DOUBLE arguments)");
        // Intermediates live in 80-bit cells. Storing to the parameter's declared width is both the
        // ABI representation and its required rounding boundary; pushing words from the TBYTE cell
        // itself would pass the x87 encoding as though it were IEEE bits.
        var staged = this._function.StackSlots.Count;
        this._function.StackSlots.Add(bytes);
        this.EmitX87(MOpcode.Fld, source, reads: true);
        this.EmitX87(MOpcode.Fstp, new MOperand.StackSlot(staged, RegSize(arg.Type)), reads: false);
        for (var offset = bytes - 2; offset >= 0; offset -= 2)
          this._current.Instructions.Add(PushOf(
            new MOperand.StackSlot(staged, MRegSize.Word, offset)));
        continue;
      }
      if (IsWide(arg.Type)) {
        // a 32-bit argument occupies two stack words, and the callee reads its LOW half at the
        // parameter's own offset - the stack grows down, so the high half is pushed first
        if (!this.TryOperandPair(arg, out var argLo, out var argHi))
          return false;
        this._current.Instructions.Add(PushOf(argHi));
        this._current.Instructions.Add(PushOf(argLo));
        continue;
      }
      if (RegSize(arg.Type) != MRegSize.Word)
        return this.Decline($"call: {callee.Name} takes {arg.Type} (word arguments only)");
      if (!this.TryOperand(arg, out var pushed))
        return false;
      this._current.Instructions.Add(PushOf(pushed));
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(callee.Name)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));

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
  /// The ON ERROR intrinsics the lowering emits. They are NOT runtime calls and cannot be: arming a
  /// handler captures the CURRENT frame - the BP and SP that <c>rt_raise</c> will restore before it
  /// jumps - and a CALL would capture its own. So they expand to the same few MOVs the direct emitter
  /// writes inline, which is why they live here rather than in the runtime ABI table.
  /// </summary>
  private static readonly HashSet<string> ErrorHandlerIntrinsics = new(StringComparer.Ordinal) {
    "rt_onerr_arm", "rt_onerr_disarm", "rt_onerr_resume_next",
    "rt_err_clear", "rt_resume_mark", "rt_resume_same", "rt_resume_next",
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

  private bool SelectErrorHandlerIntrinsic(IrCall call, IrFunction callee) {
    switch (callee.Name) {
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
        return this.Decline($"call: {callee.Name} (unhandled error-handler intrinsic)");
    }
  }

  private bool SelectRuntimeCall(IrCall call, IrFunction callee, RuntimeAbi.Routine routine) {
    // an x87 answer arrives on the stack rather than in a named register, so it needs no Result
    if (!call.Type.IsVoid && routine.Result is null && routine.Answer != RuntimeAbi.ResultKind.St0)
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
            condition: null, clobbers: [slot.Register]));
          break;
        }
        case RuntimeAbi.ArgKind.Word: {
          if (!this.TryWordOperand(arg, $"{callee.Name} takes a 32-bit value in a word register", out var source))
            return false;
          var dest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source),
            condition: null, clobbers: [slot.Register]));
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
            condition: null, clobbers: [slot.Register, slot.High]));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [high, zero], MovEffect(high, zero),
            condition: null, clobbers: [slot.Register, slot.High]));
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
            condition: null, clobbers: [slot.Register, slot.High]));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destHi, hi], MovEffect(destHi, hi),
            condition: null, clobbers: [slot.Register, slot.High]));
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

  /// <summary>Whether the call's IR result type is one this table entry knows how to hand back.</summary>
  private bool ResultShapeAgrees(IrCall call, IrFunction callee, RuntimeAbi.Routine routine) => routine.Answer switch {
    RuntimeAbi.ResultKind.WidenedWord when !IsWide(call.Type)
      => this.Decline($"call: {callee.Name} widens a word result, but the call is typed {call.Type}"),
    RuntimeAbi.ResultKind.St0 when !call.Type.IsIeeeFloat
      => this.Decline($"call: {callee.Name} answers on the x87 stack, but the call is typed {call.Type}"),
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
  private static (MOpcode[] Before, string? Call)? MathSequence(string name) {
    var bare = name.StartsWith("llvm.", StringComparison.Ordinal) ? name[5..] : name;
    var cut = bare.IndexOf(".f", StringComparison.Ordinal);
    return (cut > 0 ? bare[..cut] : bare) switch {
      "sqrt" => ([MOpcode.Fsqrt], null),
      "sin" => ([MOpcode.Fsin], null),
      "cos" => ([MOpcode.Fcos], null),
      "tan" => ([MOpcode.Fptan, MOpcode.FstpSt0], null),          // FPTAN pushes a 1.0 to discard
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
    this.EmitX87(MOpcode.Fstp, this.FloatCell(call), reads: false);
    return true;
  }

  /// <summary>
  /// A value as a WORD operand, narrowing a 32-bit one where that is sound.
  ///
  /// The IR types several things i32 that the runtime wants in a word register - a byte count, a PB
  /// file number, an error code. Taking the low half is only sound when the high half is known to
  /// carry nothing, which is exactly two cases: a constant that fits, and a value that was WIDENED
  /// from 16 bits in the first place, where the narrowing simply undoes the extension. Anything else
  /// declines rather than silently dropping the top word.
  /// </summary>
  private bool TryWordOperand(IrValue value, string what, out MOperand operand) {
    operand = null!;
    if (!IsWide(value.Type) && value.Type.IsInteger && value.Type.Bits == 8
        && value is not IrConstantInt) {
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
    if (narrowed is null)
      return this.Decline($"call: {what} (the IR types it 32-bit)");
    if (narrowed is IrConstantInt fits) {
      operand = new MOperand.Immediate(fits.Value);
      return true;
    }
    return this.TryOperand(narrowed, out operand);
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
    if (!this.TryFloatOperand(bin.Lhs, out var lhs) || !this.TryFloatOperand(bin.Rhs, out var rhs))
      return false;

    this.EmitX87(MOpcode.Fld, lhs, reads: true);
    this.EmitX87(MOpcode.Fld, rhs, reads: true);
    // The op itself touches neither memory nor registers; what it touches is the x87 stack, which the
    // scheduler orders by opcode (MOpcodes.UsesX87) because no effect descriptor can name it.
    this._current.Instructions.Add(new MInstr(opcode, [], MInstrEffect.None));
    this.EmitX87(MOpcode.Fstp, this.FloatCell(bin), reads: false);
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
  /// An integer widened to a float. x87 reads its integers from memory, so the value is parked in a
  /// frame cell first - a word for an INTEGER, both halves of the pair for a LONG - and <c>FILD</c>
  /// reads it back at that width.
  /// </summary>
  private bool SelectIntToFloat(IrCast cast) {
    var from = cast.Value.Type;
    if (!from.IsInteger || from.Bits is not (16 or 32))
      return this.Decline($"floating point: {cast.Op} from {from}");
    if (!from.Signed)
      return this.Decline($"floating point: {cast.Op} from unsigned {from} (FILD is signed)");

    var slot = this._function.StackSlots.Count;
    var wide = IsWide(from);
    this._function.StackSlots.Add(wide ? 4 : 2);
    var cell = new MOperand.StackSlot(slot, MRegSize.Word);
    if (wide) {
      if (!this.TryOperandPair(cast.Value, out var lo, out var hi))
        return false;
      this.StoreWord(cell, lo);
      this.StoreWord(Shifted(cell, 2), hi);
    } else {
      if (!this.TryOperand(cast.Value, out var value))
        return false;
      this.StoreWord(cell, value);
    }

    this.EmitX87(MOpcode.Fild, new MOperand.StackSlot(slot, wide ? MRegSize.Dword : MRegSize.Word), reads: true);
    this.EmitX87(MOpcode.Fstp, this.FloatCell(cast), reads: false);
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

  private void LoadWord(MOperand.Register dest, MOperand cell)
    => this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, cell],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false,
        ReadsMemory: true, WritesMemory: false)));

  /// <summary>A float widened or narrowed to the other float format: the load and the store do it.</summary>
  private bool SelectFloatResize(IrCast cast) {
    if (!this.TryFloatOperand(cast.Value, out var source))
      return false;
    this.EmitX87(MOpcode.Fld, source, reads: true);
    this.EmitX87(MOpcode.Fstp, this.FloatCell(cast), reads: false);
    return true;
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
      this._current.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
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
    }

    this._current.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    return true;
  }

  // ---- operand / vreg helpers -------------------------------------------------------------------

  private MReg FreshVreg(IrType type) => MReg.Virtual(this._nextVreg++, RegSize(type));

  /// <summary>True for a 32-bit integer, which this target holds in a register pair.</summary>
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
    lo = hi = null!;
    if (!this._vregs.TryGetValue(value, out var loReg) || !this._hiVregs.TryGetValue(value, out var hiReg))
      return this.Decline($"32-bit operand: {value.GetType().Name} has no register pair");
    lo = new MOperand.Register(loReg);
    hi = new MOperand.Register(hiReg);
    return true;
  }

  private static MInstrEffect PairEffect(MOperand rhs, bool readsFlags, bool writesFlags) =>
    new(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],
      ReadsFlags: readsFlags, WritesFlags: writesFlags, ReadsMemory: rhs is MOperand.Memory, WritesMemory: false);

  /// <summary>An SSA value as a machine operand: a constant is an immediate, anything else its virtual register.</summary>
  private MOperand Operand(IrValue value)
    => value is IrConstantInt c ? new MOperand.Immediate(c.Value) : new MOperand.Register(this._vregs[value]);

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
        operand = new MOperand.Immediate(c.Value);
        return true;
      case IrGlobalVariable g:
        operand = null!;
        return this.Decline($"operand: global '{g.Name}' (needs the data-layout bridge)");
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
    if (pointer is IrAlloca { Count: 1 } scalar && this._slots.TryGetValue(scalar, out var own))
      return new MOperand.StackSlot(own, size);
    if (this._vregs.TryGetValue(pointer, out var reg))
      return new MOperand.Memory(reg, null, 1, 0, size);
    if (pointer is IrGlobalVariable g) {
      // A module variable maps back to a symbol the codegen laid out, and an rt_ global IS the
      // runtime's own named cell - rt_err, rt_erl, rt_col - which the data bridge resolves to the very
      // storage the direct emitter reads. A STATIC local or a synthesized IR global (.data_cursor, a
      // string literal) still has no cell to borrow.
      if (!g.Name.StartsWith("g.", System.StringComparison.Ordinal)
          && !g.Name.StartsWith("rt_", System.StringComparison.Ordinal)) {
        this.Decline($"pointer: global '{g.Name}' (no module symbol to address)");
        return null;
      }
      return new MOperand.DataCell(g.Name, 0, size);
    }
    this.Decline($"pointer: {pointer.GetType().Name} has no register");
    return null;
  }

  /// <summary>The same cell shifted by <paramref name="delta"/> bytes - the high word of a 32-bit access.</summary>
  private static MOperand Shifted(MOperand cell, int delta) => cell switch {
    MOperand.Memory m => m with { Disp = m.Disp + delta },
    MOperand.DataCell d => d with { Disp = d.Disp + delta },
    MOperand.StackSlot s => s with { Disp = s.Disp + delta },
    _ => cell,
  };

  private static MInstrEffect MovEffect(MOperand.Register dest, MOperand src)
    => new(WrittenRegs: [0], ReadRegs: src is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: src is MOperand.Memory, WritesMemory: false);

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
