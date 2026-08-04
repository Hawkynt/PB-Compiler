using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// Stage 2 of the x86-16 back end (docs/X86-BACKEND.md): selects the typed-SSA IR into the
/// <see cref="MFunction"/> machine IR over virtual registers. Each SSA value becomes a virtual
/// register (or an immediate for an <see cref="IrConstantInt"/>); each instruction lowers to one or
/// more <see cref="MInstr"/> in two-address x86 form. This first increment handles the straight-line
/// integer core; anything it cannot model yet (branches, phis, calls, casts, division, floating
/// point) makes <see cref="TrySelect"/> return null, so the caller falls back to the direct codegen.
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
        if (IsWide(phi.Type))
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
      foreach (var block in fn.Blocks)
        foreach (var phi in block.Phis)
          if (phi.IncomingFrom(predBlock) is { } value) {
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
      if (copies.Count == 0)
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
    }

    return true;
  }

  /// <summary>The compare that feeds a block's conditional-branch terminator and nothing else (so it folds into the branch), or null.</summary>
  private static IrCmp? FoldedCompare(IrBasicBlock block)
    => block.Terminator is IrCondBr { Condition: IrCmp { Users.Count: 1 } cmp } ? cmp : null;

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

  private bool SelectInstruction(IrInstruction instr, MBlock block) {
    switch (instr) {
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

  /// <summary>
  /// A signed 16-bit divide or remainder. <c>IDIV</c> is fixed to <c>DX:AX</c>: the dividend is
  /// sign-extended into the pair by <c>CWD</c>, the quotient comes back in <c>AX</c> and the
  /// remainder in <c>DX</c>.
  ///
  /// PowerBASIC raises Error 11 on a zero divisor, and that guard is part of the language rather than
  /// an <c>$ERROR</c> option - so only a <b>non-zero compile-time constant</b> divisor is selected
  /// here, which is precisely the case where the direct emitter also drops the guard (O0220): a
  /// constant that cannot be zero cannot trap. A runtime divisor needs the guard, and the guard needs
  /// the runtime's error label, so it waits for that bridge rather than emitting a divide that would
  /// fault where the program should report.
  /// </summary>
  private bool SelectDivide(IrBinary bin, MBlock block) {
    if (IsWide(bin.Type) || bin.Type.Bits != 16)
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
    var slot = this._function.StackSlots.Count;
    for (var i = 0; i < System.Math.Max(1, alloca.Count); ++i)
      this._function.StackSlots.Add(byteSize);
    this._slots[alloca] = slot;
    // the alloca result is the slot's address: LEA dest, [slot]
    var dest = this.FreshVreg(IrType.Ptr);
    this._vregs[alloca] = dest;
    var destOp = new MOperand.Register(dest);
    this._current.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, new MOperand.StackSlot(slot, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
  }

  private bool SelectLoad(IrLoad load, MBlock block) {
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

  private bool SelectStore(IrStore store, MBlock block) {
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
  private bool SelectCmpValue(IrCmp cmp) {
    if (cmp.Lhs.Type.IsFloat)
      return this.Decline($"compare as a value: float {cmp.Pred}");
    if (IsWide(cmp.Lhs.Type))
      return this.Decline($"compare as a value: 32-bit {cmp.Pred} (needs a two-word compare)");
    if (MapPredicate(cmp.Pred) is not { } cc)
      return this.Decline($"compare as a value: {cmp.Pred}");
    if (!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, out var rhs))
      return false;
    if (lhs is not MOperand.Register)
      return this.Decline("compare as a value: immediate left operand");

    // the result is i1 in the IR, but it is materialized in a word register - a later sext to i16
    // finds it already sign-extended, which is exactly what -1/0 means
    var dest = MReg.Virtual(this._nextVreg++, MRegSize.Word);
    this._vregs[cmp] = dest;
    var destOp = new MOperand.Register(dest);

    var falseBlock = new MBlock($"{this._current.Label}.cmpfalse{this._splitCount}");
    var doneBlock = new MBlock($"{this._current.Label}.cmpdone{this._splitCount}");
    ++this._splitCount;

    this._current.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
      new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
        ReadsMemory: lhs is MOperand.Memory || rhs is MOperand.Memory, WritesMemory: false)));
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
  private bool SelectCall(IrCall call, MBlock block) {
    if (call.Callee is not IrFunction callee)
      return this.Decline("call: indirect (through a procedure pointer)");
    if (callee.IsDeclaration)
      return RuntimeAbi.For(callee.Name) is { } routine
        ? this.SelectRuntimeCall(call, callee, routine)
        : this.Decline($"call: {callee.Name} (runtime declaration - not in the runtime ABI table)");
    if (!call.Type.IsVoid && !IsWide(call.Type) && RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: {callee.Name} returns {call.Type} (word or 32-bit results only)");

    foreach (var arg in call.Args) {
      if (arg.Type.IsFloat)
        return this.Decline($"call: {callee.Name} takes {arg.Type} (no float arguments yet)");
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
  private bool SelectRuntimeCall(IrCall call, IrFunction callee, RuntimeAbi.Routine routine) {
    if (!call.Type.IsVoid && routine.Result is null)
      return this.Decline($"call: {callee.Name} returns a value the runtime ABI table does not place");
    if (!call.Type.IsVoid && (IsWide(call.Type) || RegSize(call.Type) != MRegSize.Word))
      return this.Decline($"call: {callee.Name} returns {call.Type} (word results only)");
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
          // the IR types a byte count i32; a constant that fits a word is the same value in CX
          MOperand source;
          if (IsWide(arg.Type)) {
            if (arg is not IrConstantInt { Value: >= short.MinValue and <= ushort.MaxValue } narrow)
              return this.Decline($"call: {callee.Name} takes a 32-bit value in a word register");
            source = new MOperand.Immediate(narrow.Value);
          } else if (!this.TryOperand(arg, out source!))
            return false;
          var dest = new MOperand.Register(MReg.Physical_(slot.Register, MRegSize.Word));
          this._current.Instructions.Add(new MInstr(MOpcode.Mov, [dest, source], MovEffect(dest, source),
            condition: null, clobbers: [slot.Register]));
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

    // the result is in the routine's own register; copy it into the call's virtual one so the
    // allocator may place the value anywhere
    var dest2 = this.FreshVreg(call.Type);
    this._vregs[call] = dest2;
    var destOp = new MOperand.Register(dest2);
    var resultReg = new MOperand.Register(MReg.Physical_(routine.Result!.Value, MRegSize.Word));
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, resultReg], MovEffect(destOp, resultReg)));
    return true;
  }

  /// <summary>Routes the console print routines at a PB file number (<c>rt_fselect</c>).</summary>
  private bool SelectFileRouting(IrValue file, RuntimeAbi.RuntimeArg slot) {
    MOperand source;
    if (IsWide(file.Type)) {
      if (file is not IrConstantInt { Value: >= 0 and <= 15 } number)
        return this.Decline("call: PRINT # to a runtime file number (the IR types it 32-bit)");
      source = new MOperand.Immediate(number.Value);
    } else if (!this.TryOperand(file, out source!))
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

  /// <summary>A PUSH of one argument word, with the effect descriptor that keeps it ordered against the call.</summary>
  private static MInstr PushOf(MOperand operand) => new(MOpcode.Push, [operand],
    new MInstrEffect(WrittenRegs: [], ReadRegs: operand is MOperand.Register ? [0] : [],
      ReadsFlags: false, WritesFlags: false, ReadsMemory: operand is MOperand.Memory, WritesMemory: true));

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
    if (this._vregs.TryGetValue(pointer, out var reg))
      return new MOperand.Memory(reg, null, 1, 0, size);
    if (pointer is IrGlobalVariable g) {
      // only a module variable maps back to a symbol the codegen laid out; a STATIC local or a
      // synthesized IR global (.data_cursor, a string literal) has no cell to borrow yet
      if (!g.Name.StartsWith("g.", System.StringComparison.Ordinal)) {
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
  private static MRegSize RegSize(IrType type) => type.IsPointer ? MRegSize.Word : type.Bits switch {
    <= 8 => MRegSize.Byte,
    <= 16 => MRegSize.Word,
    _ => MRegSize.Dword,
  };

  private static int SizeOf(IrType type) => type.IsPointer ? 2 : System.Math.Max(1, (type.Bits + 7) / 8);
}
