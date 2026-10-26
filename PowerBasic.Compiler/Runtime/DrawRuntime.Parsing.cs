using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Runtime;

public static partial class DrawRuntime {
  private sealed partial class Definer {
    private void DefineNext() {
      var function = this.Declare("rt_draw_next", IrType.I16, IrType.Ptr, IrType.I16, IrType.Ptr);
      var w = this.Writer(function);
      var handle = function.Parameters[0];
      var length = function.Parameters[1];
      var position = function.Parameters[2];
      w.While(() => w.Cmp(IrCmpPred.Ult, w.B.Load(IrType.I16, position), length), () => {
        var index = w.B.Load(IrType.I16, position);
        var character = this.Call(w, "rt_str_char_at_borrow", IrType.I16, handle, index);
        w.B.Store(w.B.Add(index, w.Ix(1)), position);
        var whitespace = w.B.Or(w.Cmp(IrCmpPred.Eq, character, w.Ix(' ')),
          w.Cmp(IrCmpPred.Eq, character, w.Ix('\t')));
        w.If(w.Cmp(IrCmpPred.Eq, whitespace, IrBuilder.ConstBool(false)), () => w.Return(character));
      });
      w.B.Ret(w.Ix(-1));
    }

    private void DefineNumber() {
      var function = this.Declare("rt_draw_number", IrType.I16,
        IrType.Ptr, IrType.I16, IrType.Ptr, IrType.Ptr, IrType.Ptr);
      var w = this.Writer(function);
      var character = w.Variable(IrType.I16, this.Call(w, "rt_draw_next", IrType.I16,
        function.Parameters[0], function.Parameters[1], function.Parameters[2]));
      var negative = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      var signed = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      var present = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      var value = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.B.Or(w.Cmp(IrCmpPred.Eq, character.Get(), w.Ix('+')),
          w.Cmp(IrCmpPred.Eq, character.Get(), w.Ix('-'))), () => {
        negative.Set(w.B.Xor(negative.Get(), w.Cmp(IrCmpPred.Eq, character.Get(), w.Ix('-'))));
        signed.Set(IrBuilder.ConstBool(true));
        character.Set(this.Call(w, "rt_draw_next", IrType.I16,
          function.Parameters[0], function.Parameters[1], function.Parameters[2]));
      });
      w.If(w.Cmp(IrCmpPred.Eq, character.Get(), w.Ix('=')), () => {
        var referenced = this.Reference(w, function.Parameters[0], function.Parameters[1],
          function.Parameters[2], function.Parameters[4], isString: false);
        this.FinishNumber(w, w.B.SExt(referenced, IrType.I32), negative.Get(), IrBuilder.ConstBool(true), signed.Get());
      });
      w.While(() => w.Cmp(IrCmpPred.Ne, character.Get(), w.Ix(-1)), () => {
        // QB45 checks the accumulated value only after fetching a non-EOF character. An EOF right
        // after the fifth digit bypasses this limit; another digit/command/semicolon does not.
        w.If(w.Cmp(IrCmpPred.Ugt, value.Get(), w.I32(9999)), () => {
          w.B.Store(w.Ix(5), function.Parameters[4]);
          this.FinishNumber(w, value.Get(), negative.Get(), present.Get(), signed.Get());
        });
        var digit = w.B.And(w.Cmp(IrCmpPred.Uge, character.Get(), w.Ix('0')),
          w.Cmp(IrCmpPred.Ule, character.Get(), w.Ix('9')));
        w.If(w.Cmp(IrCmpPred.Eq, digit, IrBuilder.ConstBool(false)), () => {
          var cursor = w.B.Load(IrType.I16, function.Parameters[2]);
          w.B.Store(w.B.Sub(cursor, w.Ix(1)), function.Parameters[2]);
          this.FinishNumber(w, value.Get(), negative.Get(), present.Get(), signed.Get());
        });
        value.Set(w.B.Add(w.B.Mul(value.Get(), w.I32(10)),
          w.B.ZExt(w.B.Sub(character.Get(), w.Ix('0')), IrType.I32)));
        present.Set(IrBuilder.ConstBool(true));
        character.Set(this.Call(w, "rt_draw_next", IrType.I16,
          function.Parameters[0], function.Parameters[1], function.Parameters[2]));
      });
      this.FinishNumber(w, value.Get(), negative.Get(), present.Get(), signed.Get());
      w.B.Ret(w.Ix(0));
    }

    private void FinishNumber(IrWriter w, IrValue value, IrValue negative, IrValue present, IrValue signed) {
      var flags = w.B.Or(w.B.Select(present, w.Ix(1), w.Ix(0)), w.B.Select(signed, w.Ix(2), w.Ix(0)));
      w.B.Store(flags, w.Function.Parameters[3]);
      var magnitude = w.B.Trunc(value, IrType.I16);
      w.Return(w.B.Select(negative, w.B.Sub(w.Ix(0), magnitude), magnitude));
    }
  }
}
