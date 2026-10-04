using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// PowerBASIC's internal variables (<c>pbvFixDigits</c>, <c>pbvScrnCols</c>, ...) and the FIX scale
/// they govern. The lowering names each internal variable by the DOS runtime's own cell; here those
/// cells start with the values the DOS runtime gives them, so <c>PRINT pbvFixDigits</c> says 2 on every
/// target, and a FIX value is scaled by ten to that count exactly as <c>rt_fixdn</c>/<c>rt_fixup</c> scale it.
/// </summary>
public static partial class PortableRuntime {

  private sealed partial class Definer {

    /// <summary>Gives every internal-variable cell the module declares the DOS runtime's starting value.</summary>
    private void InitialiseInternalVariables() {
      foreach (var variable in DosRuntime.InternalVariables.Values) {
        if (module.FindGlobal(variable.Label) is not { Bytes: null } cell || variable.Initial == 0)
          continue;
        var size = Math.Max(1, cell.ValueType.Bits / 8);
        var initialised = new IrGlobalVariable(variable.Label, cell.ValueType) {
          Bytes = BitConverter.GetBytes((long)variable.Initial).AsSpan(0, size).ToArray(), IsZeroInitialized = false,
        };
        module.RemoveGlobal(cell);
        cell.ReplaceAllUsesWith(initialised);
        module.AddGlobal(initialised);
      }
    }

    private Action<IrWriter>? InternalRoutine(string name) => name switch {
      // FIX: a value is held scaled by ten to pbvFixDigits; reading divides it back, storing
      // multiplies and rounds to the nearest whole number
      "rt_fix_down" => w => w.B.Ret(w.B.Binary(IrBinaryOp.FDiv, w.Function.Parameters[0], this.FixScaleOf(w))),
      "rt_fix_up" => w => {
        var scaled = w.B.Binary(IrBinaryOp.FMul, w.Function.Parameters[0], this.FixScaleOf(w));
        w.B.Ret(w.B.Cast(IrCastOp.SIToFP, w.B.Cast(IrCastOp.FPToSIRound, scaled, IrType.I64), IrType.F80));
      },
      _ => null,
    };

    /// <summary>Ten to the FIX digit count, as an EXT.</summary>
    private IrValue FixScaleOf(IrWriter w) {
      var cell = module.FindGlobal("rt_pbv_fixdigits")
        ?? module.AddGlobal(new IrGlobalVariable("rt_pbv_fixdigits", IrType.I8) { Bytes = [2], IsZeroInitialized = false });
      var digits = w.B.ZExt(w.B.Load(cell.ValueType, cell), IrType.I32);
      var scale = w.Variable(IrType.F80, w.Extended(1));
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), digits), () => {
        scale.Set(w.B.Binary(IrBinaryOp.FMul, scale.Get(), w.Extended(10)));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      return scale.Get();
    }
  }
}
