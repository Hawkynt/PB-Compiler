using System.Numerics;
using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 optimizations (docs/PB36.md). Every transformation here must preserve
/// observable behavior exactly - the differential harness re-runs all pb35
/// batteries under <c>--dialect pb36</c> against genuine PBC 3.50, and the
/// default pb35 code paths stay bit-identical (the pb36 checks read
/// <see cref="OptimizePb36"/> and change nothing when it is false).
/// </summary>
public sealed partial class CodeGenerator {

  /// <summary>True when pb36 optimizations may alter the emitted code (never its observable behavior).</summary>
  private bool OptimizePb36 => model.Dialect >= Dialect.Pb36;

  private ConstantFolder? _pb36Folder;
  private ConstantFolder Pb36Folder => this._pb36Folder ??= new(model.Equates);

  /// <summary>
  /// Wraps a compile-time value to the silent-wrap storage semantics of
  /// <paramref name="type"/> - folded arithmetic must land on exactly the bits
  /// the runtime ALU would have produced (QUIRKS: PB wraps without $ERROR NUMERIC).
  /// </summary>
  public static long WrapToType(long value, ScalarType type) => type switch {
    { ByteSize: 1 } => (byte)value,
    { ByteSize: 2, Signed: true } => (short)value,
    { ByteSize: 2 } => (ushort)value,
    { ByteSize: 4, Signed: true } => (int)value,
    { ByteSize: 4 } => (uint)value,
    _ => value,
  };

  #region O1 - constant folding (integral, wrap-correct)

  /// <summary>
  /// pb36 O1: emits a constant integral expression as one folded literal load.
  /// Only pure integral expressions fold (the folder knows literals, equates
  /// and operators - never calls), and the result is wrapped to the bound
  /// type, so the bits match the unfolded runtime arithmetic exactly.
  /// </summary>
  private bool TryEmitFolded(Expression e) {
    if (!this.OptimizePb36)
      return false;
    if (model.TypeOf(e) is not ScalarType { IsFloat: false } type)
      return false;
    if (this.Pb36Folder.TryFold(e) is not { Integer: { } raw })
      return false;

    this.EmitIntegralConstant(WrapToType(raw, type), KindOf(type));
    return true;
  }

  /// <summary>
  /// Loads an integral constant into the evaluation registers. Under pb36 the
  /// zero idiom (O8) applies: <c>XOR r,r</c> instead of <c>MOV r,0</c> - safe
  /// here because expression results never carry live flags across statements.
  /// </summary>
  private void EmitIntegralConstant(long value, ValueKind kind) {
    var asm = this._asm;
    switch (kind) {
      case ValueKind.Int16:
        if (this.OptimizePb36 && (value & 0xFFFF) == 0)
          asm.Xor(Reg.AX, Reg.AX);
        else
          asm.Mov(Reg.AX, (int)value);
        break;

      case ValueKind.Int64:
        asm.Fild(Mem.Qword(this.QuadConstOf(value)));
        break;

      default: {
        var low = (int)(value & 0xFFFF);
        var high = (int)((value >> 16) & 0xFFFF);
        if (this.OptimizePb36 && low == 0)
          asm.Xor(Reg.AX, Reg.AX);
        else
          asm.Mov(Reg.AX, low);
        if (this.OptimizePb36 && high == 0)
          asm.Xor(Reg.DX, Reg.DX);
        else
          asm.Mov(Reg.DX, high);
        break;
      }
    }
  }

  #endregion

  #region O4 - multiply strength reduction

  /// <summary>
  /// pb36 O4: <c>x * 2^n</c> (and <c>* 0</c> / <c>* 1</c>) as 8086-safe shifts.
  /// The non-constant operand is still evaluated (it may call FUNCTIONs), and
  /// shifting matches the low bits of the product exactly, so wrap semantics
  /// are preserved. Constants come from the pure folder only.
  /// </summary>
  private bool TryEmitStrengthReducedMultiply(BinaryExpr b, PbType opType) {
    if (!this.OptimizePb36 || b.Op != BinaryOp.Multiply)
      return false;
    if (opType is not ScalarType { IsFloat: false, ByteSize: 2 or 4 } scalar)
      return false;

    Expression variable;
    long constant;
    if (this.Pb36Folder.TryFold(b.Right) is { Integer: { } right }) {
      variable = b.Left;
      constant = right;
    } else if (this.Pb36Folder.TryFold(b.Left) is { Integer: { } left }) {
      variable = b.Right;
      constant = left;
    } else
      return false;

    var maxShift = scalar.ByteSize == 4 ? 4 : 8; // beyond this the generic path is cheaper
    int shift;
    if (constant == 0)
      shift = -1;
    else if (constant > 0 && BitOperations.IsPow2((ulong)constant) && BitOperations.TrailingZeroCount((ulong)constant) <= maxShift)
      shift = BitOperations.TrailingZeroCount((ulong)constant);
    else
      return false;

    var asm = this._asm;
    this.EmitExpression(variable);
    this.Coerce(model.TypeOf(variable), opType, variable);

    if (shift < 0) { // * 0: operand evaluated for its effects, result zero
      asm.Xor(Reg.AX, Reg.AX);
      if (scalar.ByteSize == 4)
        asm.Xor(Reg.DX, Reg.DX);
      return true;
    }

    for (var i = 0; i < shift; ++i)
      if (scalar.ByteSize == 4) {
        asm.Shl(Reg.AX, 1);
        asm.Rcl(Reg.DX, 1);
      } else if (shift > 4 && i == 0) {
        asm.Mov(Reg.CL, (Imm)shift);
        asm.Shl(Reg.AX, Reg.CL);
        break;
      } else
        asm.Shl(Reg.AX, 1);

    return true;
  }

  #endregion
}
