using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Runtime;

public static partial class DrawRuntime {
  private sealed partial class Definer {
    private IrValue RawByte(IrWriter w, IrValue handle, IrValue length, IrValue position, IrValue error) {
      var result = w.Variable(IrType.I16, w.Ix(0));
      var index = w.B.Load(IrType.I16, position);
      w.If(w.Cmp(IrCmpPred.Ult, index, length), () => {
        result.Set(this.Call(w, "rt_str_char_at_borrow", IrType.I16, handle, index));
        w.B.Store(w.B.Add(index, w.Ix(1)), position);
      }, () => w.B.Store(w.Ix(5), error));
      return result.Get();
    }

    private IrValue Reference(IrWriter w, IrValue handle, IrValue length, IrValue position,
        IrValue error, bool isString) {
      var tag = this.RawByte(w, handle, length, position, error);
      var low = this.RawByte(w, handle, length, position, error);
      var high = this.RawByte(w, handle, length, position, error);
      var offset = w.B.Or(low, w.B.Shl(high, w.Ix(8)));
      var pointer = w.B.Cast(IrCastOp.IntToPtr, offset, IrType.Ptr);
      var resultType = isString ? IrType.Ptr : IrType.I16;
      var result = w.Variable(resultType, isString ? new IrNullPtr() : w.Ix(0));
      w.If(w.Cmp(IrCmpPred.Eq, w.B.Load(IrType.I16, error), w.Ix(0)), () => {
        if (isString) {
          w.If(w.Cmp(IrCmpPred.Eq, tag, w.Ix(3)),
            () => result.Set(w.B.Load(IrType.Ptr, pointer)), () => w.B.Store(w.Ix(5), error));
          return;
        }
        // DOUBLE references use legacy scratch-state conversions in QB45. Do not approximate
        // that protocol as a WORD load; unsupported reference tags take the explicit error path.
        var integer = w.Cmp(IrCmpPred.Eq, tag, w.Ix(2));
        w.If(integer, () => result.Set(w.B.Load(IrType.I16, pointer)), () => {
          w.If(w.Cmp(IrCmpPred.Eq, tag, w.Ix(4)),
            () => result.Set(w.B.Cast(IrCastOp.FPToSIRound, w.B.Load(IrType.F32, pointer), IrType.I16)),
            () => w.B.Store(w.Ix(5), error));
        });
      });
      return result.Get();
    }
  }
}
