using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

public static partial class PortableRuntime {

  private sealed partial class Definer {

    /// <summary>The DOS runtime's first seed: a program that never says RANDOMIZE draws the same numbers there and here.</summary>
    private const uint InitialSeed = 0x12345678;

    private IrGlobalVariable? _seed;

    /// <summary>
    /// <c>rt_rndseed</c>, the LONG that <c>RANDOMIZE n</c> stores into and RND steps. The lowering
    /// declares it zero-initialised, as a cell the DOS runtime owns; here it is this runtime's, so it
    /// is replaced by one that starts where the DOS runtime's does.
    /// </summary>
    private IrGlobalVariable Seed {
      get {
        if (this._seed is not null)
          return this._seed;
        var existing = module.FindGlobal("rt_rndseed");
        if (existing?.Bytes is not null)
          return this._seed = existing;
        var seed = new IrGlobalVariable("rt_rndseed", IrType.I32) {
          Bytes = BitConverter.GetBytes(InitialSeed), IsZeroInitialized = false,
        };
        if (existing is not null) {
          module.RemoveGlobal(existing);
          existing.ReplaceAllUsesWith(seed);
        }
        return this._seed = this.Shared(seed);
      }
    }

    private IrFunction? _random;

    /// <summary>
    /// <c>rt.random()</c>: the DOS runtime's generator, exactly - <c>seed = seed * 1103515245 + 12345</c>
    /// in 32 bits, and the answer the high word's low fifteen bits over 32768.
    /// </summary>
    private IrFunction Random => this._random ??= this.Internal("rt.random", IrType.F64, [], w => {
      var seed = w.B.Add(w.B.Mul(w.B.Load(IrType.I32, this.Seed), w.I32(1103515245)), w.I32(12345));
      w.B.Store(seed, this.Seed);
      var high = w.B.And(w.B.Binary(IrBinaryOp.LShr, seed, w.I32(16)), w.I32(0x7FFF));
      w.B.Ret(w.B.Binary(IrBinaryOp.FDiv, w.B.Cast(IrCastOp.SIToFP, high, IrType.F64), new IrConstantFloat(IrType.F64, 32768)));
    });

    /// <summary><c>RND(a, z)</c>: <c>a + trunc(rnd * (z - a + 1))</c>, as the DOS runtime computes it.</summary>
    private void RandomRange(IrWriter w) {
      var (lower, upper) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var span = w.B.Cast(IrCastOp.SIToFP, w.B.Add(w.B.Sub(upper, lower), w.I32(1)), IrType.F64);
      var scaled = w.B.Binary(IrBinaryOp.FMul, span, w.B.Call(IrType.F64, this.Random));
      w.B.Ret(w.B.Add(lower, w.B.Cast(IrCastOp.FPToSI, scaled, IrType.I32)));
    }

    /// <summary>
    /// ROUND's half-away-from-zero: <c>trunc(|x| + 0.5)</c> with the sign put back. A magnitude past
    /// 2^63 is a whole number already, and is its own answer.
    /// </summary>
    private void RoundHalfAway(IrWriter w) {
      var type = w.Function.ReturnType;
      w.B.Ret(w.B.Call(type, this.RoundAway(type), Convert(w, w.Function.Parameters[0], type)));
    }

    /// <summary>
    /// <c>ROUND(x, places)</c>, as the DOS runtime's <c>rt_round</c> does it: a scale of ten to the
    /// places, built by multiplying, then half away from zero, then divided back.
    /// </summary>
    private void RoundPlaces(IrWriter w) {
      var type = w.Function.ReturnType;
      var (x, places) = (Convert(w, w.Function.Parameters[0], type), w.Function.Parameters[1]);
      var scale = w.Variable(type, new IrConstantFloat(type, 1));
      var count = w.Variable(places.Type, places);
      w.While(() => w.Cmp(IrCmpPred.Sgt, count.Get(), IrBuilder.ConstInt(places.Type, 0)), () => {
        scale.Set(w.B.Binary(IrBinaryOp.FMul, scale.Get(), new IrConstantFloat(type, 10)));
        count.Set(w.B.Sub(count.Get(), IrBuilder.ConstInt(places.Type, 1)));
      });
      var rounded = w.B.Call(type, this.RoundAway(type), w.B.Binary(IrBinaryOp.FMul, x, scale.Get()));
      w.B.Ret(w.B.Binary(IrBinaryOp.FDiv, rounded, scale.Get()));
    }

    private static IrValue Convert(IrWriter w, IrValue x, IrType type)
      => x.Type == type ? x : w.B.Cast(x.Type.Bits < type.Bits ? IrCastOp.FPExt : IrCastOp.FPTrunc, x, type);

    /// <summary><c>rt.roundAway.fN(x)</c>: the whole number nearest <c>x</c>, halves away from zero.</summary>
    private IrFunction RoundAway(IrType type) => this.Internal($"rt.roundAway.f{type.Bits}", type, [type], w => {
      var value = w.Function.Parameters[0];
      var negative = w.Cmp(IrCmpPred.Folt, value, new IrConstantFloat(type, 0));
      var magnitude = w.B.Select(negative, w.B.Binary(IrBinaryOp.FSub, new IrConstantFloat(type, 0), value), value);
      w.If(w.Cmp(IrCmpPred.Foge, magnitude, new IrConstantFloat(type, 9.2233720368547758e18)), () => w.Return(value));
      var whole = w.B.Cast(IrCastOp.SIToFP,
        w.B.Cast(IrCastOp.FPToSI, w.B.Binary(IrBinaryOp.FAdd, magnitude, new IrConstantFloat(type, 0.5)), IrType.I64), type);
      w.B.Ret(w.B.Select(negative, w.B.Binary(IrBinaryOp.FSub, new IrConstantFloat(type, 0), whole), whole));
    });
  }
}
