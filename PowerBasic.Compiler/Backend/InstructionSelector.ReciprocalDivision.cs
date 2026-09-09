using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Backend;

public sealed partial class InstructionSelector {

  /// <summary>
  /// O0056's target-neutral signed high multiply is spelled in IR as
  /// <c>trunc((sext i16 x * i32 magic) ashr 16)</c>. On x86-16 that exact single-use shape is one
  /// accumulator <c>IMUL r/m16</c>, whose high half is already in DX. Selecting it as such avoids
  /// turning the middle-end optimization into a 32-bit runtime multiply on the very target it is
  /// intended to accelerate.
  /// </summary>
  private bool TrySelectSignedMulHigh(IrBinary multiply) {
    if (this._target is not { Optimize: true, OptimizeSpeed: true }
        || SignedMulHighShape(multiply) is not { } shape)
      return false;
    if (!this.TryOperand(shape.Source, out var source))
      return false;

    // These users are the target-neutral spelling of "take DX". Once the multiply owns the machine
    // idiom they must not be selected separately. The preceding SExt has already been walked by the
    // selector; after this fold its machine pair is dead but semantically inert.
    this._consumed.Add(shape.HighShift);
    this._consumed.Add(shape.Truncate);

    var factor = new MOperand.Register(this.FreshVreg(IrType.I16));
    var immediate = new MOperand.Immediate(shape.Magic);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [factor, immediate], MovEffect(factor, immediate)));

    var ax = new MOperand.Register(MReg.Physical_(Reg.AX, MRegSize.Word));
    var dx = new MOperand.Register(MReg.Physical_(Reg.DX, MRegSize.Word));
    Reg[] pinned = [Reg.AX, Reg.DX];
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [ax, source], MovEffect(ax, source),
      condition: null, clobbers: pinned));
    this._current.Instructions.Add(new MInstr(MOpcode.Imul, [factor],
      new MInstrEffect(WrittenRegs: [], ReadRegs: [0], ReadsFlags: false, WritesFlags: true,
        ReadsMemory: false, WritesMemory: false),
      condition: null, clobbers: pinned));

    var destination = this.FreshVreg(IrType.I16);
    this._vregs[shape.Truncate] = destination;
    var destinationOperand = new MOperand.Register(destination);
    this._current.Instructions.Add(new MInstr(MOpcode.Mov, [destinationOperand, dx], MovEffect(destinationOperand, dx)));
    return true;
  }

  private static (IrValue Source, short Magic, IrBinary HighShift, IrCast Truncate)? SignedMulHighShape(IrBinary multiply) {
    if (multiply.Op != IrBinaryOp.Mul || multiply.Type is not { IsInteger: true, Signed: true, Bits: 32 }
        || multiply.Users.Count != 1)
      return null;

    IrCast extend;
    IrConstantInt constant;
    if (multiply.Lhs is IrCast left && multiply.Rhs is IrConstantInt right) {
      extend = left;
      constant = right;
    } else if (multiply.Rhs is IrCast rightExtend && multiply.Lhs is IrConstantInt leftConstant) {
      extend = rightExtend;
      constant = leftConstant;
    } else {
      return null;
    }

    if (extend.Op != IrCastOp.SExt || extend.Type.Bits != 32
        || extend.Value.Type is not { IsInteger: true, Signed: true, Bits: 16 }
        || extend.Users.Count != 1
        || constant.Type is not { IsInteger: true, Signed: true, Bits: 32 }
        || constant.Value is < short.MinValue or > short.MaxValue)
      return null;

    if (multiply.Users.Single() is not IrBinary {
          Op: IrBinaryOp.AShr,
          Rhs: IrConstantInt { Value: 16 },
          Users.Count: 1
        } highShift
        || !ReferenceEquals(highShift.Lhs, multiply)
        || highShift.Users.Single() is not IrCast {
          Op: IrCastOp.Trunc,
          Type: { IsInteger: true, Signed: true, Bits: 16 }
        } truncate
        || !ReferenceEquals(truncate.Value, highShift))
      return null;

    if (!ReferenceEquals(extend.Parent, multiply.Parent)
        || !ReferenceEquals(highShift.Parent, multiply.Parent)
        || !ReferenceEquals(truncate.Parent, multiply.Parent))
      return null;

    return (extend.Value, checked((short)constant.Value), highShift, truncate);
  }
}
