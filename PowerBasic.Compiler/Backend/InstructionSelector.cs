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
          if (phi.IncomingFrom(predBlock) is { } value) {
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
        block.Instructions.Add(new MInstr(MOpcode.Jmp, [new MOperand.LabelRef(br.Target.Label)], MInstrEffect.None));
        block.Successors.Add(br.Target.Label);
        return true;
      case IrCondBr cond when folded is { } cmp && MapPredicate(cmp.Pred) is { } cc:
        if (!this.TryOperand(cmp.Lhs, out var lhs) || !this.TryOperand(cmp.Rhs, out var rhs))
          return false;
        if (lhs is not MOperand.Register)
          return this.Decline("compare: immediate left operand");   // CMP needs a register/memory left operand
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
      default:
        return this.Decline($"instruction: {instr.GetType().Name}");   // unsupported construct - decline the whole function
    }
  }

  private bool SelectBinary(IrBinary bin, MBlock block) {
    if (bin.IsFloatOp)
      return this.Decline($"binary: float {bin.Op}");
    if (!TryMapBinary(bin.Op, out var opcode))
      return this.Decline($"binary: {bin.Op}");   // division / remainder - not in this increment

    // two-address form: dest = lhs; dest <op>= rhs
    var dest = this.FreshVreg(bin.Type);
    this._vregs[bin] = dest;
    var destOp = new MOperand.Register(dest);
    if (!this.TryOperand(bin.Lhs, out var lhs) || !this.TryOperand(bin.Rhs, out var rhs))
      return false;
    block.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, lhs], MovEffect(destOp, lhs)));
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
    if (this.PointerMemory(load.Pointer, RegSize(load.Type)) is not { } mem)
      return false;
    block.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: true, WritesMemory: false)));
    return true;
  }

  private bool SelectStore(IrStore store, MBlock block) {
    if (this.PointerMemory(store.Pointer, RegSize(store.Value.Type)) is not { } mem)
      return false;
    if (!this.TryOperand(store.Value, out var value))
      return false;
    block.Instructions.Add(new MInstr(MOpcode.Mov, [mem, value],
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
    block.Instructions.Add(new MInstr(MOpcode.Lea, [destOp, mem],
      new MInstrEffect(WrittenRegs: [0], ReadRegs: [], ReadsFlags: false, WritesFlags: false, ReadsMemory: false, WritesMemory: false)));
    return true;
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
      return this.Decline($"call: {callee.Name} (runtime declaration - needs the runtime-label bridge)");
    if (!call.Type.IsVoid && RegSize(call.Type) != MRegSize.Word)
      return this.Decline($"call: {callee.Name} returns {call.Type} (word results only)");

    foreach (var arg in call.Args) {
      if (arg.Type.IsFloat || RegSize(arg.Type) != MRegSize.Word)
        return this.Decline($"call: {callee.Name} takes {arg.Type} (word arguments only)");
      if (!this.TryOperand(arg, out var pushed))
        return false;
      block.Instructions.Add(new MInstr(MOpcode.Push, [pushed],
        new MInstrEffect(WrittenRegs: [], ReadRegs: pushed is MOperand.Register ? [0] : [],
          ReadsFlags: false, WritesFlags: false, ReadsMemory: pushed is MOperand.Memory, WritesMemory: true)));
    }

    block.Instructions.Add(new MInstr(MOpcode.Call, [new MOperand.LabelRef(callee.Name)],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: true, WritesMemory: true),
      condition: null, clobbers: _callClobbers));

    if (call.Type.IsVoid)
      return true;

    // the result is in AX; copy it into the call's own virtual register so the allocator may place
    // the value anywhere (the copy is free when it lands in AX again)
    var dest = this.FreshVreg(call.Type);
    this._vregs[call] = dest;
    var destOp = new MOperand.Register(dest);
    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, RegSize(call.Type)));
    block.Instructions.Add(new MInstr(MOpcode.Mov, [destOp, ax], MovEffect(destOp, ax)));
    return true;
  }

  /// <summary>Every allocatable register a CALL destroys under this ABI - the callee saves none of them.</summary>
  private static readonly Reg[] _callClobbers = [Reg.AX, Reg.BX, Reg.CX, Reg.DX, Reg.SI, Reg.DI];

  private bool SelectRet(IrRet ret, MBlock block) {
    if (ret.HasValue && ret.Value is { } value) {
      // the result is returned in AX (word) - a physical pin the allocator must honour
      var ax = MReg.Physical_(Reg.AX, RegSize(value.Type));
      var axOp = new MOperand.Register(ax);
      if (!this.TryOperand(value, out var src))
        return false;
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

  /// <summary>A pointer value as a memory operand <c>[ptrReg]</c> of the given access size, or null when it has no register.</summary>
  private MOperand.Memory? PointerMemory(IrValue pointer, MRegSize size) {
    if (this._vregs.TryGetValue(pointer, out var reg))
      return new(reg, null, 1, 0, size);
    _ = pointer is IrGlobalVariable g
      ? this.Decline($"pointer: global '{g.Name}' (needs the data-layout bridge)")
      : this.Decline($"pointer: {pointer.GetType().Name} has no register");
    return null;
  }

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
