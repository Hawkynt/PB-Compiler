namespace PowerBasic.Compiler.Ir;

/// <summary>
/// A cursor that appends instructions to a basic block. It keeps construction
/// concise for both the AST→IR lowering and the unit tests, and returns the freshly
/// created instruction (which is also its result value) from every emit method.
/// </summary>
public sealed class IrBuilder {

  public IrBuilder(IrBasicBlock? block = null) => this.Block = block;

  /// <summary>The block instructions are appended to.</summary>
  public IrBasicBlock? Block { get; set; }

  /// <summary>Repositions the builder at the end of <paramref name="block"/>.</summary>
  public void Position(IrBasicBlock block) => this.Block = block;

  private IrBasicBlock Target => this.Block ?? throw new InvalidOperationException("IrBuilder has no insertion block");

  private T Emit<T>(T instruction) where T : IrInstruction => this.Target.Append(instruction);

  // ---- constants -----------------------------------------------------------

  public static IrConstantInt ConstInt(IrType type, long value) => new(type, value);
  public static IrConstantInt ConstI32(long value) => new(IrType.I32, value);
  public static IrConstantInt ConstBool(bool value) => new(IrType.I1, value ? 1 : 0);
  public static IrConstantFloat ConstFloat(IrType type, double value) => new(type, value);

  // ---- arithmetic ----------------------------------------------------------

  public IrBinary Binary(IrBinaryOp op, IrValue lhs, IrValue rhs) => this.Emit(new IrBinary(op, lhs, rhs));
  public IrBinary Add(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Add, l, r);
  public IrBinary Sub(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Sub, l, r);
  public IrBinary Mul(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Mul, l, r);
  public IrBinary SDiv(IrValue l, IrValue r) => this.Binary(IrBinaryOp.SDiv, l, r);
  public IrBinary And(IrValue l, IrValue r) => this.Binary(IrBinaryOp.And, l, r);
  public IrBinary Or(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Or, l, r);
  public IrBinary Xor(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Xor, l, r);
  public IrBinary Shl(IrValue l, IrValue r) => this.Binary(IrBinaryOp.Shl, l, r);

  public IrCmp Cmp(IrCmpPred pred, IrValue lhs, IrValue rhs) => this.Emit(new IrCmp(pred, lhs, rhs));

  public IrSelect Select(IrValue condition, IrValue ifTrue, IrValue ifFalse) => this.Emit(new IrSelect(condition, ifTrue, ifFalse));

  public IrCast Cast(IrCastOp op, IrValue value, IrType to) => this.Emit(new IrCast(op, value, to));
  public IrCast Trunc(IrValue value, IrType to) => this.Cast(IrCastOp.Trunc, value, to);
  public IrCast ZExt(IrValue value, IrType to) => this.Cast(IrCastOp.ZExt, value, to);
  public IrCast SExt(IrValue value, IrType to) => this.Cast(IrCastOp.SExt, value, to);

  // ---- memory --------------------------------------------------------------

  public IrAlloca Alloca(IrType allocated) => this.Emit(new IrAlloca(allocated));
  public IrLoad Load(IrType type, IrValue pointer) => this.Emit(new IrLoad(type, pointer));
  public IrStore Store(IrValue value, IrValue pointer) => this.Emit(new IrStore(value, pointer));

  /// <summary>Appends an opaque inline-assembly barrier (see <see cref="IrInlineAsm"/>).</summary>
  public IrInlineAsm InlineAsm(IrInlineAsm node) => this.Emit(node);
  public IrGep Gep(IrValue basePtr, IrValue byteOffset) => this.Emit(new IrGep(basePtr, byteOffset));
  public IrGep Gep(IrValue basePtr, IrValue index, IrType elementType) => this.Emit(new IrGep(basePtr, index, elementType));

  // ---- ssa / calls ---------------------------------------------------------

  public IrPhi Phi(IrType type) => this.Target.AppendPhi(new IrPhi(type));
  public IrCall Call(IrType resultType, IrValue callee, params IrValue[] args) => this.Emit(new IrCall(resultType, callee, args));
  public IrCall Call(IrType resultType, IrValue callee, IReadOnlyList<IrValue> args) => this.Emit(new IrCall(resultType, callee, args));

  // ---- terminators ---------------------------------------------------------

  public IrRet Ret(IrValue? value = null) => this.Emit(new IrRet(value));
  public IrBr Br(IrBasicBlock target) => this.Emit(new IrBr(target));
  public IrCondBr CondBr(IrValue cond, IrBasicBlock ifTrue, IrBasicBlock ifFalse) => this.Emit(new IrCondBr(cond, ifTrue, ifFalse));
  public IrSwitch Switch(IrValue cond, IrBasicBlock defaultTarget) => this.Emit(new IrSwitch(cond, defaultTarget));
  public IrIndirectBr IndirectBr(IrValue address, IEnumerable<IrBasicBlock> targets)
    => this.Emit(new IrIndirectBr(address, targets));
  public IrUnreachable Unreachable() => this.Emit(new IrUnreachable());
}
