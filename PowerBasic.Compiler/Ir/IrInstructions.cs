namespace PowerBasic.Compiler.Ir;

/// <summary>Binary arithmetic / bitwise opcodes. Signedness is encoded in the opcode (sdiv vs udiv), as in LLVM.</summary>
public enum IrBinaryOp {
  Add, Sub, Mul, SDiv, UDiv, SRem, URem,
  And, Or, Xor, Shl, LShr, AShr,
  FAdd, FSub, FMul, FDiv,
}

/// <summary>Comparison predicates. Integer predicates carry signedness; float predicates are ordered (the common case).</summary>
public enum IrCmpPred {
  // integer
  Eq, Ne, Slt, Sle, Sgt, Sge, Ult, Ule, Ugt, Uge,
  // float (ordered: false if either operand is NaN)
  Foeq, Fone, Folt, Fole, Fogt, Foge,
}

/// <summary>Type-conversion opcodes (the LLVM cast set, restricted to what the dialects need).</summary>
public enum IrCastOp {
  Trunc, ZExt, SExt,
  FPToSI, FPToUI, SIToFP, UIToFP,

  /// <summary>
  /// Float to integer with ROUNDING, not truncation - what BASIC does when a real is assigned to an
  /// integer variable, and what CINT/CLNG spell explicitly. It is a separate operation from
  /// <see cref="FPToSI"/> because the two disagree on every value with a fraction: PowerBASIC's
  /// <c>n% = 2.7</c> is 3, a C cast is 2. The rounding is to nearest, ties to even - the x87's own
  /// default, which is what the runtime leaves the control word set to.
  /// </summary>
  FPToSIRound,
  FPTrunc, FPExt,
  IntToPtr, PtrToInt, BitCast,
  /// <summary>Microsoft Binary Format → IEEE, the conversion a load of an MBF cell performs.</summary>
  MbfToFP,
  /// <summary>IEEE → Microsoft Binary Format, the conversion a store into an MBF cell performs.</summary>
  FPToMbf,
}

/// <summary>A binary arithmetic or bitwise instruction: <c>result = op lhs, rhs</c>.</summary>
public sealed class IrBinary : IrInstruction {
  public IrBinaryOp Op { get; }

  public IrBinary(IrBinaryOp op, IrValue lhs, IrValue rhs) : base(lhs.Type) {
    this.Op = op;
    this.AddOperand(lhs);
    this.AddOperand(rhs);
  }

  public IrValue Lhs => this.GetOperand(0);
  public IrValue Rhs => this.GetOperand(1);

  public bool IsFloatOp => this.Op is IrBinaryOp.FAdd or IrBinaryOp.FSub or IrBinaryOp.FMul or IrBinaryOp.FDiv;
}

/// <summary>A comparison producing an <c>i1</c>: <c>result = icmp/fcmp pred lhs, rhs</c>.</summary>
public sealed class IrCmp : IrInstruction {
  public IrCmpPred Pred { get; }

  public IrCmp(IrCmpPred pred, IrValue lhs, IrValue rhs) : base(IrType.I1) {
    this.Pred = pred;
    this.AddOperand(lhs);
    this.AddOperand(rhs);
  }

  public IrValue Lhs => this.GetOperand(0);
  public IrValue Rhs => this.GetOperand(1);
}

/// <summary>A type conversion: <c>result = op value to type</c>.</summary>
public sealed class IrCast : IrInstruction {
  public IrCastOp Op { get; }

  public IrCast(IrCastOp op, IrValue value, IrType toType) : base(toType) {
    this.Op = op;
    this.AddOperand(value);
  }

  public IrValue Value => this.GetOperand(0);
}

/// <summary>
/// Stack-allocates space for <see cref="Count"/> consecutive values of
/// <see cref="Allocated"/> (one by default) and yields a pointer to the first.
/// </summary>
public sealed class IrAlloca(IrType allocated) : IrInstruction(IrType.Ptr) {
  public IrType Allocated { get; } = allocated;

  /// <summary>The number of elements; greater than one for an array allocation.</summary>
  public int Count { get; init; } = 1;
}

/// <summary>Loads a value of <see cref="Type"/> from a pointer: <c>result = load type, ptr</c>.</summary>
public sealed class IrLoad : IrInstruction {
  public IrLoad(IrType resultType, IrValue pointer) : base(resultType) => this.AddOperand(pointer);
  public IrValue Pointer => this.GetOperand(0);
}

/// <summary>Stores a value through a pointer: <c>store value, ptr</c> (yields void).</summary>
public sealed class IrStore : IrInstruction {
  public IrStore(IrValue value, IrValue pointer) : base(IrType.Void) {
    this.AddOperand(value);
    this.AddOperand(pointer);
  }

  public IrValue Value => this.GetOperand(0);
  public IrValue Pointer => this.GetOperand(1);
}

/// <summary>
/// A block of inline assembly, carried through the IR as an opaque barrier.
///
/// The text is not parsed here. What the IR needs to know about it is not what it says but what it
/// can do: PowerBASIC's <c>!</c> statements reach local variables and module globals by NAME, jump to
/// BASIC labels in the enclosing scope, and may touch any register - so from the middle end's point of
/// view this reads and writes everything, and nothing may be moved across it, folded through it, or
/// deleted because it looked unused.
///
/// <para>
/// It is deliberately a BARRIER rather than a modelled instruction. A modelled one would need every
/// operand, result and clobber the text implies, and a list that is one entry short miscompiles
/// silently - the same failure as an under-declared machine effect, which is exactly how an FSQRT
/// ended up scheduled past the store that captured its answer. The function carrying it is marked
/// <see cref="IrFunction.HasInlineAsm"/> and the optimizer skips it whole, which is the trade the
/// direct emitter already makes.
/// </para>
/// </summary>
public sealed class IrInlineAsm(string text) : IrInstruction(IrType.Void) {

  /// <summary>The assembly source, exactly as it was written after the <c>!</c>.</summary>
  public string Text { get; } = text;
}

/// <summary>
/// Pointer displacement. In the default (byte) mode it adds a byte count to a pointer —
/// the flattened form used for fixed-size scalar arrays. When <see cref="ElementType"/>
/// is set it is an element-indexed GEP (LLVM scales the index by the element's target
/// size), used for pointer-element arrays whose stride is target-dependent.
/// </summary>
public sealed class IrGep : IrInstruction {
  public IrGep(IrValue basePtr, IrValue byteOffset) : base(IrType.Ptr) {
    this.AddOperand(basePtr);
    this.AddOperand(byteOffset);
  }

  public IrGep(IrValue basePtr, IrValue index, IrType elementType) : base(IrType.Ptr) {
    this.ElementType = elementType;
    this.AddOperand(basePtr);
    this.AddOperand(index);
  }

  public IrValue BasePtr => this.GetOperand(0);

  /// <summary>The displacement: a byte count when <see cref="ElementType"/> is null, else an element index.</summary>
  public IrValue ByteOffset => this.GetOperand(1);

  /// <summary>The element type for an element-indexed GEP, or null for a byte-offset GEP.</summary>
  public IrType? ElementType { get; }
}

/// <summary>An SSA phi: picks an incoming value according to the predecessor control came from.</summary>
public sealed class IrPhi : IrInstruction {

  private readonly List<IrBasicBlock> _blocks = [];

  public IrPhi(IrType type) : base(type) { }

  /// <summary>The predecessor blocks, positionally aligned with <see cref="Operands"/>.</summary>
  public IReadOnlyList<IrBasicBlock> IncomingBlocks => this._blocks;

  /// <summary>Adds an incoming (value, predecessor) edge.</summary>
  public void AddIncoming(IrValue value, IrBasicBlock from) {
    this._blocks.Add(from);
    this.AddOperand(value);
  }

  /// <summary>The incoming value flowing in from the given predecessor, or null if none recorded.</summary>
  public IrValue? IncomingFrom(IrBasicBlock block) {
    for (var i = 0; i < this._blocks.Count; ++i)
      if (ReferenceEquals(this._blocks[i], block))
        return this.GetOperand(i);
    return null;
  }

  /// <summary>Repoints an incoming edge's predecessor (used when a predecessor block is merged away).</summary>
  public void RenameIncomingBlock(IrBasicBlock from, IrBasicBlock to) {
    for (var i = 0; i < this._blocks.Count; ++i)
      if (ReferenceEquals(this._blocks[i], from))
        this._blocks[i] = to;
  }

  /// <summary>Drops the incoming edge from the given predecessor (used when a CFG edge disappears).</summary>
  public void RemoveIncoming(IrBasicBlock block) {
    for (var i = 0; i < this._blocks.Count; ++i)
      if (ReferenceEquals(this._blocks[i], block)) {
        this._blocks.RemoveAt(i);
        this.RemoveOperandAt(i);
        return;
      }
  }
}

/// <summary>A branchless choice: <c>result = select cond, ifTrue, ifFalse</c> (cond is i1).</summary>
public sealed class IrSelect : IrInstruction {
  public IrSelect(IrValue condition, IrValue ifTrue, IrValue ifFalse) : base(ifTrue.Type) {
    this.AddOperand(condition);
    this.AddOperand(ifTrue);
    this.AddOperand(ifFalse);
  }

  public IrValue Condition => this.GetOperand(0);
  public IrValue IfTrue => this.GetOperand(1);
  public IrValue IfFalse => this.GetOperand(2);
}

/// <summary>A call: <c>[result =] call callee(args...)</c>. The callee is an operand (so indirect calls are uniform).</summary>
public sealed class IrCall : IrInstruction {
  public IrCall(IrType resultType, IrValue callee, IReadOnlyList<IrValue> args) : base(resultType) {
    this.AddOperand(callee);
    foreach (var a in args)
      this.AddOperand(a);
  }

  public IrValue Callee => this.GetOperand(0);
  public IEnumerable<IrValue> Args => this.Operands.Skip(1);
  public int ArgCount => this.Operands.Count - 1;
}

/// <summary>A function return: <c>ret value</c> or <c>ret void</c>.</summary>
public sealed class IrRet : IrInstruction {
  public IrRet(IrValue? value = null) : base(IrType.Void) {
    if (value is not null)
      this.AddOperand(value);
  }

  public bool HasValue => this.Operands.Count > 0;
  public IrValue? Value => this.HasValue ? this.GetOperand(0) : null;
  public override bool IsTerminator => true;
}

/// <summary>An unconditional branch: <c>br target</c>.</summary>
public sealed class IrBr(IrBasicBlock target) : IrInstruction(IrType.Void) {
  public IrBasicBlock Target { get; set; } = target;
  public override bool IsTerminator => true;
  public override IEnumerable<IrBasicBlock> Successors => [this.Target];
}

/// <summary>A conditional branch: <c>br cond, ifTrue, ifFalse</c>.</summary>
public sealed class IrCondBr : IrInstruction {
  public IrCondBr(IrValue condition, IrBasicBlock ifTrue, IrBasicBlock ifFalse) : base(IrType.Void) {
    this.AddOperand(condition);
    this.IfTrue = ifTrue;
    this.IfFalse = ifFalse;
  }

  public IrValue Condition => this.GetOperand(0);
  public IrBasicBlock IfTrue { get; set; }
  public IrBasicBlock IfFalse { get; set; }
  public override bool IsTerminator => true;
  public override IEnumerable<IrBasicBlock> Successors => [this.IfTrue, this.IfFalse];
}

/// <summary>An integer switch: a default target plus a list of (value, target) cases.</summary>
public sealed class IrSwitch : IrInstruction {

  private readonly List<(long Value, IrBasicBlock Target)> _cases = [];

  public IrSwitch(IrValue condition, IrBasicBlock defaultTarget) : base(IrType.Void) {
    this.AddOperand(condition);
    this.DefaultTarget = defaultTarget;
  }

  public IrValue Condition => this.GetOperand(0);
  public IrBasicBlock DefaultTarget { get; set; }
  public IReadOnlyList<(long Value, IrBasicBlock Target)> Cases => this._cases;

  public void AddCase(long value, IrBasicBlock target) => this._cases.Add((value, target));

  public override bool IsTerminator => true;
  public override IEnumerable<IrBasicBlock> Successors {
    get {
      yield return this.DefaultTarget;
      foreach (var (_, target) in this._cases)
        yield return target;
    }
  }
}

/// <summary>Marks an unreachable point (control must never arrive here).</summary>
public sealed class IrUnreachable() : IrInstruction(IrType.Void) {
  public override bool IsTerminator => true;
}
