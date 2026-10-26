using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Runtime;

public static partial class DrawRuntime {
  private sealed partial class Definer {
    private void DefineMove() {
      var function = this.Declare("rt_draw_move", IrType.Void, IrType.I16, IrType.I16, IrType.I16, IrType.I16);
      if (!function.IsDeclaration)
        return;
      var w = this.Writer(function);
      var oldX = this.Load(w, "rt_gx1");
      var oldY = this.Load(w, "rt_gy1");
      var x = w.Variable(IrType.I16, function.Parameters[1]);
      var y = w.Variable(IrType.I16, function.Parameters[2]);
      w.If(w.Cmp(IrCmpPred.Eq, function.Parameters[0], w.Ix(0)), () => {
        var scale = this.Load(w, "rt_draw_scale");
        // Zero is the startup unity fast path. Explicit S4 has a WORD product and is not equivalent.
        w.If(w.Cmp(IrCmpPred.Ne, scale, w.Ix(0)), () => {
          x.Set(w.B.SDiv(w.B.Mul(x.Get(), scale), w.Ix(4)));
          y.Set(w.B.SDiv(w.B.Mul(y.Get(), scale), w.Ix(4)));
        });
        this.EmitRotation(w, x, y);
        x.Set(w.B.Add(oldX, x.Get()));
        y.Set(w.B.Add(oldY, y.Get()));
      });
      this.Store(w, "rt_gx2", x.Get());
      this.Store(w, "rt_gy2", y.Get());
      this.Store(w, "rt_gstyle", w.Ix(-1));
      var encodedColor = this.Load(w, "rt_draw_color");
      this.Store(w, "rt_gcolor", w.B.Select(w.Cmp(IrCmpPred.Eq, encodedColor, w.Ix(0)),
        w.Ix(15), w.B.Sub(encodedColor, w.Ix(1))));
      var flags = function.Parameters[3];
      var noUpdate = w.Cmp(IrCmpPred.Ne, w.B.And(flags, w.Ix(2)), w.Ix(0));
      w.If(w.Cmp(IrCmpPred.Eq, w.B.And(flags, w.Ix(1)), w.Ix(0)), () => {
        this.Call(w, "rt_line", IrType.Void);
        w.If(noUpdate, () => { this.Store(w, "rt_gx1", oldX); this.Store(w, "rt_gy1", oldY); });
      }, () => w.If(w.Cmp(IrCmpPred.Eq, noUpdate, IrBuilder.ConstBool(false)), () => {
        this.Store(w, "rt_gx1", x.Get());
        this.Store(w, "rt_gy1", y.Get());
      }));
      w.B.Ret();
    }

    private void EmitRotation(IrWriter w, IrWriter.Local x, IrWriter.Local y) {
      var angle = this.Load(w, "rt_draw_angle");
      w.If(w.Cmp(IrCmpPred.Ne, angle, w.Ix(0)), () => {
        var cosine = w.Variable(IrType.F80, w.Extended(1));
        var sine = w.Variable(IrType.F80, w.Extended(0));
        w.If(w.Cmp(IrCmpPred.Eq, angle, w.Ix(90)), () => {
          cosine.Set(w.Extended(0)); sine.Set(w.Extended(1));
        });
        w.If(w.Cmp(IrCmpPred.Eq, angle, w.Ix(180)), () => cosine.Set(w.Extended(-1)));
        w.If(w.Cmp(IrCmpPred.Eq, angle, w.Ix(270)), () => {
          cosine.Set(w.Extended(0)); sine.Set(w.Extended(-1));
        });
        var quarter = w.B.Binary(IrBinaryOp.SRem, angle, w.Ix(90));
        w.If(w.Cmp(IrCmpPred.Ne, quarter, w.Ix(0)), () => {
          var pi = IrConstantFloat.FromFloat80Bits(0x4000, 0xC90FDAA22168C235UL);
          var factor = w.B.Binary(IrBinaryOp.FDiv, pi, w.Extended(180));
          var radians = w.B.Binary(IrBinaryOp.FMul, w.B.Cast(IrCastOp.SIToFP, angle, IrType.F80), factor);
          cosine.Set(this.Call(w, "llvm.cos.f80", IrType.F80, radians));
          sine.Set(this.Call(w, "llvm.sin.f80", IrType.F80, radians));
        });
        var ratio = this.PixelRatio(w);
        var dx = w.B.Cast(IrCastOp.SIToFP, x.Get(), IrType.F80);
        var dy = w.B.Cast(IrCastOp.SIToFP, y.Get(), IrType.F80);
        IrValue Multiply(IrValue a, IrValue b) => w.B.Binary(IrBinaryOp.FMul, a, b);
        var rotatedX = w.B.Binary(IrBinaryOp.FAdd, Multiply(dx, cosine.Get()),
          w.B.Binary(IrBinaryOp.FDiv, Multiply(dy, sine.Get()), ratio));
        var rotatedY = w.B.Binary(IrBinaryOp.FSub, Multiply(dy, cosine.Get()),
          Multiply(Multiply(dx, sine.Get()), ratio));
        x.Set(w.B.Cast(IrCastOp.FPToSIRound, rotatedX, IrType.I16));
        y.Set(w.B.Cast(IrCastOp.FPToSIRound, rotatedY, IrType.I16));
      });
    }

    private IrValue PixelRatio(IrWriter w) {
      var mode = this.Call(w, "rt_graphics_mode", IrType.I16);
      var ratio = w.Variable(IrType.F80, w.Extended(1));
      foreach (var (modes, numerator, denominator) in new[] {
        (new[] { 4, 5, 0x0D, 0x13 }, 5, 6), (new[] { 6, 0x0E }, 5, 12),
        (new[] { 0x0F, 0x10 }, 35, 48),
      }) {
        IrValue selected = IrBuilder.ConstBool(false);
        foreach (var value in modes)
          selected = w.B.Or(selected, w.Cmp(IrCmpPred.Eq, mode, w.Ix(value)));
        w.If(selected, () => ratio.Set(w.B.Binary(IrBinaryOp.FDiv, w.Extended(numerator), w.Extended(denominator))));
      }
      return ratio.Get();
    }
  }
}
