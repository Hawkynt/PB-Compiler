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
  private readonly Dictionary<IrAlloca, int> _slots = new(ReferenceEqualityComparer.Instance);
  private MFunction _function = null!;
  private int _nextVreg;

  /// <summary>Selects a function into machine IR, or null if it contains a construct this stage cannot model.</summary>
  public static MFunction? TrySelect(IrFunction fn) {
    if (fn.IsDeclaration || fn.Entry is null)
      return null;
    var selector = new InstructionSelector();
    return selector.Run(fn);
  }

  private MFunction? Run(IrFunction fn) {
    this._function = new MFunction(fn.Name);

    // arguments take the FIRST virtual registers (so argument i is vreg i, which the emitter's ABI
    // prologue relies on to load argument i into allocation[i]); they are function live-ins
    foreach (var arg in fn.Parameters)
      this._vregs[arg] = this.FreshVreg(arg.Type);

    // each phi then gets a virtual register; the value is materialized by copies on the incoming edges
    // (out-of-SSA), so a use of the phi simply reads this register
    foreach (var block in fn.Blocks)
      foreach (var phi in block.Phis)
        this._vregs[phi] = this.FreshVreg(phi.Type);

    var mblocks = new Dictionary<string, MBlock>();
    foreach (var block in fn.Blocks) {
      var mblock = new MBlock(block.Label);
      mblocks[block.Label] = mblock;
      this._function.Blocks.Add(mblock);
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
          if (phi.IncomingFrom(predBlock) is { } value)
            copies.Add((this._vregs[phi], this.Operand(value)));
      if (copies.Count == 0)
        continue;

      // a cycle on this edge (one copy's source is another copy's destination) needs a temporary - decline
      var destinations = copies.Select(c => c.Dest).ToHashSet();
      if (copies.Any(c => c.Source is MOperand.Register r && destinations.Contains(r.Reg) && !r.Reg.Equals(c.Dest)))
        return false;

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
        block.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(br.Target.Label)], MInstrEffect.None));
        block.Successors.Add(br.Target.Label);
        return true;
      case IrCondBr cond when folded is { } cmp && MapPredicate(cmp.Pred) is { } cc:
        var lhs = this.Operand(cmp.Lhs);
        if (lhs is not MOperand.Register)
          return false;            // CMP needs a register/memory left operand, not an immediate
        var rhs = this.Operand(cmp.Rhs);
        block.Instructions.Add(new MInstr(MOpcode.Cmp, [lhs, rhs],
          new MInstrEffect(WrittenRegs: [], ReadRegs: RegReadIndices(lhs, rhs), ReadsFlags: false, WritesFlags: true,
            ReadsMemory: lhs is MOperand.Memory || rhs is MOperand.Memory, WritesMemory: false)));
        block.Instructions.Add(new MInstr(MOpcode.Jcc, [new MOperand.LabelRef(cond.IfTrue.Label)],
          new MInstrEffect([], [], ReadsFlags: true, WritesFlags: false, ReadsMemory: false, WritesMemory: false), cc));
        block.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(cond.IfFalse.Label)], MInstrEffect.None));
        block.Successors.Add(cond.IfTrue.Label);
        block.Successors.Add(cond.IfFalse.Label);
        return true;
      default:
        return false;              // a non-compare condition, a switch, etc. - not in this increment
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
      default:
        return false;   // unsupported construct - decline the whole function
    }
  }

  private bool SelectBinary(IrBinary bin, MBlock block) {
    if (bin.IsFloatOp || !TryMapBinary(bin.Op, out var opcode))
      return false;   // float / division / remainder - not in this increment

    // two-address form: dest = lhs; dest <op>= rhs
    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    var lhs = this.Operand(bin.Lhs);
    block.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, lhs], MovEffect(destOp, lhs)));
    var rhs = this.Operand(bin.Rhs);
    // the two-operand IMUL has no immediate form - materialize an immediate multiplier in a register
    if (opcode == MOpcode.Imul && rhs is MOperand.Immediate) {
      var tmp = new MOperand.Register(this.FreshVreg(bin.Type));
      block.Instructions.Add(new MInstr(MOpcode.Mov, [tmp, rhs], MovEffect(tmp, rhs)));
      rhs = tmp;
    }
    block.Instructions.Add(new MInstr(opcode, [destOp, rhs],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: rhs is MOperand.Register ? [0, 1] : [0],
        ReadsFlags: false, WritesFlags: true, ReadsMemory: rhs is MOperand.Memory, WritesMemory: false)));
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
    block.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, new MOperand.StackSlot(slot, MRegSize.Word)],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
  }

  private bool SelectLoad(IrLoad load, MBlock block) {
    var dest = this.FreshVreg(load.Type);
    this._vregs[load] = dest;
    var destOp = new MOperand.Register(dest);
    var mem = this.PointerMemory(load.Pointer, RegSize(load.Type));
    block.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false)));
    return true;
  }

  private bool SelectStore(IrStore store, MBlock block) {
    var mem = this.PointerMemory(store.Pointer, RegSize(store.Value.Type));
    var value = this.Operand(store.Value);
    block.Instructions.Add(new MInstr(MOpcode.Mov, [mem, value],
      new MInstrEffect(WrittenRegs: [], ReadRegs: value is MOperand.Register ? [1] : [],
        ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: true)));
    return true;
  }

  private bool SelectGep(IrGep gep, MBlock block) {
    if (gep.ElementType is not null)
      return false;   // element-indexed (target-scaled) GEP - byte-offset only in this increment

    var dest = this.FreshVreg(IrType.Ptr);
    this._vregs[gep] = dest;
    var destOp = new MOperand.Register(dest);
    var baseOp = this.Operand(gep.BasePtr);
    if (baseOp is not MOperand.Register baseReg)
      return false;
    // LEA dest, [base + offset]: a constant offset folds into the displacement, a register offset becomes the index
    MOperand.Memory mem = gep.ByteOffset is IrConstantInt c
      ? new(baseReg.Reg, null, 1, (int)c.Value, MRegSize.Word)
      : this.Operand(gep.ByteOffset) is MOperand.Register idx ? new(baseReg.Reg, idx.Reg, 1, 0, MRegSize.Word) : null!;
    if (mem is null)
      return false;
    block.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
  }

  private bool SelectRet(IrRet ret, MBlock block) {
    if (ret.HasValue && ret.Value is { } value) {
      // the result is returned in AX (word) - a physical pin the allocator must honour
      var ax = MReg.Physical_(Reg.AX, RegSize(value.Type));
      var axOp = new MOperand.Register(ax);
      var src = this.Operand(value);
      block.Instructions.Add(new MInstr(MOpcode.Mov, [axOp, src], MovEffect(axOp, src)));
    }

    block.Instructions.Add(new MInstr(MOpcode.Ret, [], MInstrEffect.None));
    return true;
  }

  // ---- operand / vreg helpers -------------------------------------------------------------------

  private MReg FreshVreg(IrType type) => MReg.Virtual(this._nextVreg++, RegSize(type));

  /// <summary>An SSA value as a machine operand: a constant is an immediate, anything else its virtual register.</summary>
  private MOperand Operand(IrValue value)
    => value is IrConstantInt c ? new MOperand.Immediate(c.Value) : new MOperand.Register(this._vregs[value]);

  /// <summary>A pointer value as a memory operand <c>[ptrReg]</c> of the given access size.</summary>
  private MOperand.Memory PointerMemory(IrValue pointer, MRegSize size)
    => new(this._vregs[pointer], null, 1, 0, size);

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

  private static MRegSize RegSize(IrType type) => type.Bits switch {
    <= 8 => MRegSize.Byte,
    <= 16 => MRegSize.Word,
    _ => MRegSize.Dword,
  };

  private static int SizeOf(IrType type) => type.IsPointer ? 2 : System.Math.Max(1, (type.Bits + 7) / 8);
}
