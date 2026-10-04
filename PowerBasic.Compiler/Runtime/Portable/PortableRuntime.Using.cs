using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// PRINT USING's numeric field and USING$, as the DOS runtime's <c>rt_usefmt</c> renders them: the
/// value arrives scaled to an integer by ten to the field's decimals, and is written right-aligned in
/// the field's width with a minus sign, optional thousands commas, and at least one digit before the
/// point. USING$ is the same text captured instead of printed, which is what the DOS runtime's capture
/// mode is, and a runtime format holding one numeric field is read at run time.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    private IrFunction? _usingText;
    private IrGlobalVariable? _capturing, _captured;

    private IrGlobalVariable Capturing => this._capturing ??= this.Shared(new IrGlobalVariable("rt.capturing", this.Index));
    private IrGlobalVariable Captured => this._captured ??= this.Shared(new IrGlobalVariable("rt.captured", IrType.Ptr));

    /// <summary>Whether this module has a USING$, so console output has to look at the capture state at all.</summary>
    private bool CapturesOutput => module.FindFunction("rt_capture_begin") is not null || module.FindFunction("rt_using_dynamic") is not null;

    private Action<IrWriter>? UsingRoutine(string name) => name switch {
      "rt_using_field" => w => {
        var buffer = w.Buffer(NumberText + 16);
        var length = w.B.Call(w.Index, this.UsingText, w.Function.Parameters[0], w.ToIndex(w.Function.Parameters[1]), buffer);
        w.B.Call(IrType.Void, this.Out, buffer, length);
        w.B.Ret();
      },
      "rt_fusing_field" => w => {
        var buffer = w.Buffer(NumberText + 16);
        var length = w.B.Call(w.Index, this.UsingText, w.Function.Parameters[1], w.ToIndex(w.Function.Parameters[2]), buffer);
        w.B.Call(IrType.Void, this.FileOut, w.ToIndex(w.Function.Parameters[0]), buffer, length);
        w.B.Ret();
      },
      "rt_capture_begin" => w => {
        w.B.Call(IrType.Void, this.Release, w.B.Load(IrType.Ptr, this.Captured));
        w.B.Store(new IrNullPtr(), this.Captured);
        w.B.Store(w.Ix(1), this.Capturing);
        w.B.Ret();
      },
      "rt_capture_end" => w => {
        w.B.Store(w.Ix(0), this.Capturing);
        var text = w.B.Load(IrType.Ptr, this.Captured);
        w.B.Store(new IrNullPtr(), this.Captured);
        w.B.Ret(text);
      },
      "rt_using_dynamic" => this.UsingDynamic,
      _ => null,
    };

    /// <summary>Console output while a USING$ is being built: appended to the captured text instead of written.</summary>
    private void Capture(IrWriter w, IrValue buffer, IrValue length) {
      var piece = this.Make(w, buffer, length);
      var previous = w.B.Load(IrType.Ptr, this.Captured);
      w.B.Store(this.Concatenate(w, previous, piece), this.Captured);
      this.Consume(w, previous, piece);
    }

    /// <summary>
    /// <c>rt.usingText(scaled, spec, buffer)</c>, the length written: spec carries the width in its
    /// second byte, the decimals in the low seven bits of its first and thousands grouping in bit 7.
    /// </summary>
    private IrFunction UsingText => this._usingText ??= this.Internal("rt.usingText", this.Index, [IrType.I32, this.Index, IrType.Ptr], w => {
      var (scaled, spec, output) = (w.Function.Parameters[0], w.Function.Parameters[1], w.Function.Parameters[2]);
      var width = w.B.And(w.B.Binary(IrBinaryOp.LShr, spec, w.Ix(8)), w.Ix(0xFF));
      var decimals = w.B.And(spec, w.Ix(0x7F));
      var group = w.Cmp(IrCmpPred.Ne, w.B.And(spec, w.Ix(0x80)), w.Ix(0));
      var negative = w.Cmp(IrCmpPred.Slt, scaled, w.I32(0));
      // the magnitude in 64 bits, so the most negative LONG has one
      var wide = w.B.SExt(scaled, IrType.I64);
      var magnitude = w.Variable(IrType.I64, w.B.Select(negative, w.B.Sub(w.I64(0), wide), wide));
      // digits, least significant first, into the end of a scratch run
      var digits = w.Buffer(24);
      var count = w.Variable(w.Index, w.Ix(0));
      var more = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => more.Get(), () => {
        var next = w.B.Binary(IrBinaryOp.UDiv, magnitude.Get(), w.I64(10));
        var digit = w.B.Sub(magnitude.Get(), w.B.Mul(next, w.I64(10)));
        w.SetByte(digits, count.Get(), w.B.Add(w.B.Trunc(digit, IrType.I8), w.I8('0')));
        count.Set(w.B.Add(count.Get(), w.Ix(1)));
        magnitude.Set(next);
        more.Set(w.Cmp(IrCmpPred.Ne, next, w.I64(0)));
      });
      // at least one digit before the point: 0.05 is 5 scaled, and reads "0.05"
      w.While(() => w.Cmp(IrCmpPred.Sle, count.Get(), decimals), () => {
        w.SetByte(digits, count.Get(), w.I8('0'));
        count.Set(w.B.Add(count.Get(), w.Ix(1)));
      });
      var integerDigits = w.B.Sub(count.Get(), decimals);
      var commas = w.B.Select(group, w.B.Binary(IrBinaryOp.SDiv, w.B.Sub(integerDigits, w.Ix(1)), w.Ix(3)), w.Ix(0));
      var printed = w.B.Add(w.B.Add(count.Get(), commas),
        w.B.Add(w.B.Select(negative, w.Ix(1), w.Ix(0)), w.B.Select(w.Cmp(IrCmpPred.Sgt, decimals, w.Ix(0)), w.Ix(1), w.Ix(0))));
      var length = w.Variable(w.Index, w.Ix(0));
      void Emit(IrValue character) {
        w.SetByte(output, length.Get(), character);
        length.Set(w.B.Add(length.Get(), w.Ix(1)));
      }
      var pad = w.Variable(w.Index, w.B.Sub(width, printed));
      w.While(() => w.Cmp(IrCmpPred.Sgt, pad.Get(), w.Ix(0)), () => {
        Emit(w.I8(' '));
        pad.Set(w.B.Sub(pad.Get(), w.Ix(1)));
      });
      w.If(negative, () => Emit(w.I8('-')));
      // the integer digits, most significant first, a comma before each group of three after the first
      var at = w.Variable(w.Index, w.B.Sub(count.Get(), w.Ix(1)));
      var left = w.Variable(w.Index, integerDigits);
      w.While(() => w.Cmp(IrCmpPred.Sgt, left.Get(), w.Ix(0)), () => {
        Emit(w.ByteAt(digits, at.Get()));
        at.Set(w.B.Sub(at.Get(), w.Ix(1)));
        left.Set(w.B.Sub(left.Get(), w.Ix(1)));
        w.If(w.B.And(group, w.B.And(w.Cmp(IrCmpPred.Sgt, left.Get(), w.Ix(0)),
            w.Cmp(IrCmpPred.Eq, w.B.Binary(IrBinaryOp.SRem, left.Get(), w.Ix(3)), w.Ix(0)))), () => Emit(w.I8(',')));
      });
      w.If(w.Cmp(IrCmpPred.Sgt, decimals, w.Ix(0)), () => {
        Emit(w.I8('.'));
        w.While(() => w.Cmp(IrCmpPred.Sge, at.Get(), w.Ix(0)), () => {
          Emit(w.ByteAt(digits, at.Get()));
          at.Set(w.B.Sub(at.Get(), w.Ix(1)));
        });
      });
      w.B.Ret(length.Get());
    });

    /// <summary>
    /// <c>USING$(format$, value)</c> with a runtime format: its first numeric field is read the way
    /// the compile-time reader reads one, the value rendered into it, and the text before and after
    /// the field kept as it is. A format without a field is answered unchanged. Consumes the format.
    /// </summary>
    private void UsingDynamic(IrWriter w) {
      var (value, format) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var (bytes, length) = this.View(w, format, w.Ix(1), this.Length(w, format));
      IrValue Char(IrValue i) => w.ByteAt(bytes, w.B.Select(w.Cmp(IrCmpPred.Slt, i, length), i, w.Ix(0)));
      IrValue Is(IrValue i, char c) => w.B.And(w.Cmp(IrCmpPred.Slt, i, length), w.Cmp(IrCmpPred.Eq, Char(i), w.I8(c)));
      var start = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Slt, start.Get(), length), w.Cmp(IrCmpPred.Ne, Char(start.Get()), w.I8('#'))),
        () => start.Set(w.B.Add(start.Get(), w.Ix(1))));
      w.If(w.Cmp(IrCmpPred.Sge, start.Get(), length), () => w.Return(format));
      var i = w.Variable(w.Index, start.Get());
      var digits = w.Variable(w.Index, w.Ix(0));
      var commas = w.Variable(w.Index, w.Ix(0));
      var scanning = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => scanning.Get(), () => {
        w.If(Is(i.Get(), '#'), () => {
          digits.Set(w.B.Add(digits.Get(), w.Ix(1)));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        }, () => w.If(w.B.And(Is(i.Get(), ','), Is(w.B.Add(i.Get(), w.Ix(1)), '#')), () => {
          commas.Set(w.B.Add(commas.Get(), w.Ix(1)));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        }, () => scanning.Set(IrBuilder.ConstBool(false))));
      });
      var decimals = w.Variable(w.Index, w.Ix(0));
      w.If(Is(i.Get(), '.'), () => {
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
        w.While(() => Is(i.Get(), '#'), () => {
          decimals.Set(w.B.Add(decimals.Get(), w.Ix(1)));
          i.Set(w.B.Add(i.Get(), w.Ix(1)));
        });
      });
      var width = w.B.Add(w.B.Add(digits.Get(), commas.Get()),
        w.B.Select(w.Cmp(IrCmpPred.Sgt, decimals.Get(), w.Ix(0)), w.B.Add(decimals.Get(), w.Ix(1)), w.Ix(0)));
      var spec = w.B.Or(w.B.Or(w.B.Shl(width, w.Ix(8)), decimals.Get()),
        w.B.Select(w.Cmp(IrCmpPred.Sgt, commas.Get(), w.Ix(0)), w.Ix(0x80), w.Ix(0)));
      // scaled by ten to the decimals and rounded to nearest-even, as the compile-time field is
      var scale = w.Variable(IrType.F64, new IrConstantFloat(IrType.F64, 1));
      var k = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, k.Get(), decimals.Get()), () => {
        scale.Set(w.B.Binary(IrBinaryOp.FMul, scale.Get(), new IrConstantFloat(IrType.F64, 10)));
        k.Set(w.B.Add(k.Get(), w.Ix(1)));
      });
      var scaled = w.B.Cast(IrCastOp.FPToSIRound, w.B.Binary(IrBinaryOp.FMul, value, scale.Get()), IrType.I32);
      var field = w.Buffer(NumberText + 16);
      var fieldLength = w.B.Call(w.Index, this.UsingText, scaled, spec, field);
      var before = this.Make(w, bytes, start.Get());
      var middle = this.Make(w, field, fieldLength);
      var after = this.Make(w, w.B.Gep(bytes, i.Get()), w.B.Sub(length, i.Get()));
      var joined = this.Concatenate(w, before, middle);
      var result = this.Concatenate(w, joined, after);
      this.Consume(w, before, middle, after, joined, format);
      w.B.Ret(result);
    }
  }
}
