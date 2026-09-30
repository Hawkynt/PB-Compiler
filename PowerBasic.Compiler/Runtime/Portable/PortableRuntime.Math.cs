using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

public static partial class PortableRuntime {

  private sealed partial class Definer {

    // A constant the reductions need beyond a double's 53 bits is split in two: the double nearest
    // it and what is left over, applied as (x - k*hi) - k*lo so the leftover is never folded away
    private const double PiOverTwo = 1.5707963267948966, PiOverTwoLow = 6.123233995736766e-17;
    private const double Ln2 = 0.6931471805599453, Ln2Low = 2.3190468138462996e-17;
    private const double Ln10 = 2.302585092994046, Ln10Low = -2.1707562233822494e-16;
    private const double Log2E = 1.4426950408889634;
    private const double TwoOverPi = 0.6366197723675814;
    private const double PiOverSix = 0.5235987755982989;
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt3 = 1.7320508075688772;
    private const double Tan15 = 0.2679491924311227;
    private const double TwoTo64 = 18446744073709551616.0;

    /// <summary>
    /// <c>llvm.sqrt.f64</c> and its kin, for a target with no floating-point hardware
    /// (<c>softMath</c>): each computes in EXTENDED from the argument widened and narrows its answer
    /// to the intrinsic's type. The domain errors are BASIC's error 5, as on the x87 targets.
    /// </summary>
    private Action<IrWriter>? MathRoutine(string name) {
      if (!softMath || name.Split('.') is not ["llvm", var fn, "f32" or "f64" or "f80"])
        return null;
      var body = fn switch {
        "sqrt" => this.SquareRoot,
        "sin" => this.Sine,
        "cos" => this.Cosine,
        "tan" => this.Tangent,
        "atan" => this.ArcTangent,
        "log" => this.NaturalLog,
        "log2" => this.Log2,
        "log10" => this.Log10,
        "exp" => this.ExpE,
        "exp2" => this.Exp2,
        "exp10" => this.Exp10,
        "pow" => this.RaiseToPower,
        _ => (IrFunction?)null,
      };
      if (body is null)
        return null;
      return w => {
        var arguments = w.Function.Parameters.Select(parameter => ToExtended(w, parameter)).ToArray<IrValue>();
        var result = w.B.Call(IrType.F80, body, arguments);
        w.B.Ret(w.Function.ReturnType == IrType.F80 ? result : w.B.Cast(IrCastOp.FPTrunc, result, w.Function.ReturnType));
      };
    }

    private static IrValue ToExtended(IrWriter w, IrValue value)
      => value.Type == IrType.F80 ? value : w.B.Cast(IrCastOp.FPExt, value, IrType.F80);

    private IrFunction MathFunction(string name, int arity, Action<IrWriter> body)
      => this.Internal($"rt.math.{name}", IrType.F80, Enumerable.Repeat(IrType.F80, arity).ToArray(), body);

    private static IrValue Ext(double value) => new IrConstantFloat(IrType.F80, value);
    private static IrValue Add(IrWriter w, IrValue a, IrValue b) => w.B.Binary(IrBinaryOp.FAdd, a, b);
    private static IrValue Sub(IrWriter w, IrValue a, IrValue b) => w.B.Binary(IrBinaryOp.FSub, a, b);
    private static IrValue Mul(IrWriter w, IrValue a, IrValue b) => w.B.Binary(IrBinaryOp.FMul, a, b);
    private static IrValue Div(IrWriter w, IrValue a, IrValue b) => w.B.Binary(IrBinaryOp.FDiv, a, b);
    private static IrValue Float(IrWriter w, IrValue integer) => w.B.Cast(IrCastOp.SIToFP, integer, IrType.F80);

    /// <summary>Raises <paramref name="code"/> and answers zero, should an ON ERROR handler resume.</summary>
    private void Fail(IrWriter w, int code) {
      w.B.Call(IrType.Void, this.ErrorFunction, w.I32(code));
      w.Return(Ext(0));
    }

    /// <summary>Repeats <paramref name="body"/> <paramref name="times"/> times.</summary>
    private static void Repeat(IrWriter w, int times, Action body) {
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), w.I32(times)), () => {
        body();
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
    }

    private IrFunction? _scale;

    /// <summary><c>x * 2^n</c>, exactly: powers of two in steps of 2^64, then of 2.</summary>
    private IrFunction Scale => this._scale ??= this.Internal("rt.math.scale", IrType.F80, [IrType.F80, IrType.I32], w => {
      var x = w.Variable(IrType.F80, w.Function.Parameters[0]);
      var n = w.Variable(IrType.I32, w.Function.Parameters[1]);
      foreach (var (limit, step, factor) in (ReadOnlySpan<(int, int, double)>)[(63, 64, TwoTo64), (0, 1, 2)]) {
        w.While(() => w.Cmp(IrCmpPred.Sgt, n.Get(), w.I32(limit)), () => {
          x.Set(Mul(w, x.Get(), Ext(factor)));
          n.Set(w.B.Sub(n.Get(), w.I32(step)));
        });
        w.While(() => w.Cmp(IrCmpPred.Slt, n.Get(), w.I32(-limit)), () => {
          x.Set(Mul(w, x.Get(), Ext(1 / factor)));
          n.Set(w.B.Add(n.Get(), w.I32(step)));
        });
      }
      w.B.Ret(x.Get());
    });

    /// <summary>
    /// Splits a positive <c>x</c> into <c>m * 2^k</c> with <c>m</c> in <c>[low, 2 * low)</c>, by powers of
    /// two, which are exact. Answers the variables holding <c>m</c> and <c>k</c>.
    /// </summary>
    private static (IrWriter.Local M, IrWriter.Local K) Normalize(IrWriter w, IrValue x, double low) {
      var m = w.Variable(IrType.F80, x);
      var k = w.Variable(IrType.I32, w.I32(0));
      foreach (var (factor, step) in (ReadOnlySpan<(double, int)>)[(TwoTo64, 64), (2, 1)]) {
        w.While(() => w.Cmp(IrCmpPred.Foge, m.Get(), Ext(low * 2 * (factor == 2 ? 1 : factor))), () => {
          m.Set(Mul(w, m.Get(), Ext(1 / factor)));
          k.Set(w.B.Add(k.Get(), w.I32(step)));
        });
        w.While(() => w.Cmp(IrCmpPred.Folt, m.Get(), Ext(low / (factor == 2 ? 1 : factor))), () => {
          m.Set(Mul(w, m.Get(), Ext(factor)));
          k.Set(w.B.Sub(k.Get(), w.I32(step)));
        });
      }
      return (m, k);
    }

    private IrFunction? _ln;

    /// <summary>
    /// <c>ln x</c> of a positive <c>x</c>: <c>x = m * 2^k</c> with <c>m</c> in <c>[√½, √2)</c>, then
    /// <c>ln m = 2 atanh s</c> for <c>s = (m-1)/(m+1)</c>, whose odd series has shrunk below
    /// EXTENDED's last bit after thirteen terms since <c>|s| &lt; 0.172</c>.
    /// </summary>
    private IrFunction Ln => this._ln ??= this.MathFunction("ln", 1, w => {
      var (m, k) = Normalize(w, w.Function.Parameters[0], Sqrt2 / 2);
      var s = Div(w, Sub(w, m.Get(), Ext(1)), Add(w, m.Get(), Ext(1)));
      var s2 = Mul(w, s, s);
      var term = w.Variable(IrType.F80, s);
      var sum = w.Variable(IrType.F80, s);
      var n = w.Variable(IrType.F80, Ext(1));
      Repeat(w, 13, () => {
        term.Set(Mul(w, term.Get(), s2));
        n.Set(Add(w, n.Get(), Ext(2)));
        sum.Set(Add(w, sum.Get(), Div(w, term.Get(), n.Get())));
      });
      var kf = Float(w, k.Get());
      w.B.Ret(Add(w, Add(w, Mul(w, kf, Ext(Ln2Low)), Mul(w, sum.Get(), Ext(2))), Mul(w, kf, Ext(Ln2))));
    });

    private IrFunction? _expE;

    /// <summary>
    /// <c>e^x</c>: <c>x = k ln 2 + r</c> with <c>|r| &lt;= ½ ln 2</c> (Cody and Waite's two-part
    /// <c>ln 2</c>), Taylor's series for <c>e^r</c>, and <c>2^k</c> exactly. Far below the range it is
    /// zero; far above, error 6, overflow.
    /// </summary>
    private IrFunction ExpE => this._expE ??= this.MathFunction("exp", 1, w => {
      var x = w.Function.Parameters[0];
      w.If(w.Cmp(IrCmpPred.Folt, x, Ext(-11400)), () => w.Return(Ext(0)));
      w.If(w.Cmp(IrCmpPred.Fogt, x, Ext(11400)), () => this.Fail(w, 6));
      var k = w.B.Cast(IrCastOp.FPToSIRound, Mul(w, x, Ext(Log2E)), IrType.I32);
      var kf = Float(w, k);
      var r = Sub(w, Sub(w, x, Mul(w, kf, Ext(Ln2))), Mul(w, kf, Ext(Ln2Low)));
      var term = w.Variable(IrType.F80, Ext(1));
      var sum = w.Variable(IrType.F80, Ext(1));
      var n = w.Variable(IrType.F80, Ext(0));
      Repeat(w, 20, () => {
        n.Set(Add(w, n.Get(), Ext(1)));
        term.Set(Div(w, Mul(w, term.Get(), r), n.Get()));
        sum.Set(Add(w, sum.Get(), term.Get()));
      });
      w.B.Ret(w.B.Call(IrType.F80, this.Scale, sum.Get(), k));
    });

    /// <summary>Raises error 5 unless <c>x</c> is positive (or zero, when <paramref name="allowZero"/>).</summary>
    private void RequirePositive(IrWriter w, IrValue x, bool allowZero)
      => w.If(w.Cmp(allowZero ? IrCmpPred.Folt : IrCmpPred.Fole, x, Ext(0)), () => this.Fail(w, 5));

    private IrFunction NaturalLog => this.MathFunction("log", 1, w => {
      var x = w.Function.Parameters[0];
      this.RequirePositive(w, x, allowZero: false);
      w.B.Ret(w.B.Call(IrType.F80, this.Ln, x));
    });

    private IrFunction Log2 => this.MathFunction("log2", 1, w => {
      var x = w.Function.Parameters[0];
      this.RequirePositive(w, x, allowZero: false);
      w.B.Ret(Mul(w, w.B.Call(IrType.F80, this.Ln, x), Ext(Log2E)));
    });

    private IrFunction Log10 => this.MathFunction("log10", 1, w => {
      var x = w.Function.Parameters[0];
      this.RequirePositive(w, x, allowZero: false);
      w.B.Ret(Div(w, w.B.Call(IrType.F80, this.Ln, x), Ext(Ln10)));
    });

    /// <summary><c>2^y</c>: the whole part scales exactly, so a whole <c>y</c> gives an exact power.</summary>
    private IrFunction Exp2 => this.MathFunction("exp2", 1, w => {
      var y = w.Function.Parameters[0];
      w.If(w.Cmp(IrCmpPred.Folt, y, Ext(-16500)), () => w.Return(Ext(0)));
      w.If(w.Cmp(IrCmpPred.Fogt, y, Ext(16500)), () => this.Fail(w, 6));
      var k = w.B.Cast(IrCastOp.FPToSIRound, y, IrType.I32);
      var fraction = Mul(w, Sub(w, y, Float(w, k)), Ext(Ln2));
      w.B.Ret(w.B.Call(IrType.F80, this.Scale, w.B.Call(IrType.F80, this.ExpE, fraction), k));
    });

    private IrFunction Exp10 => this.MathFunction("exp10", 1, w => {
      var y = w.Function.Parameters[0];
      w.B.Ret(w.B.Call(IrType.F80, this.ExpE, Add(w, Mul(w, y, Ext(Ln10)), Mul(w, y, Ext(Ln10Low)))));
    });

    /// <summary>
    /// <c>√x</c>: <c>x = m * 4^k</c> with <c>m</c> in <c>[1, 4)</c>, seven Newton steps from
    /// <c>(m + 1) / 2</c> - enough to double a first guess's two good bits past EXTENDED's 64 - and
    /// <c>2^k</c> exactly.
    /// </summary>
    private IrFunction SquareRoot => this.MathFunction("sqrt", 1, w => {
      var x = w.Function.Parameters[0];
      this.RequirePositive(w, x, allowZero: true);
      w.If(w.Cmp(IrCmpPred.Foeq, x, Ext(0)), () => w.Return(Ext(0)));
      var (m, k) = Normalize(w, x, 1);
      // an odd power of two moves one factor of 2 into m, leaving m in [1, 4) and k even
      w.If(w.Cmp(IrCmpPred.Ne, w.B.And(k.Get(), w.I32(1)), w.I32(0)), () => {
        m.Set(Mul(w, m.Get(), Ext(2)));
        k.Set(w.B.Sub(k.Get(), w.I32(1)));
      });
      var guess = w.Variable(IrType.F80, Mul(w, Add(w, m.Get(), Ext(1)), Ext(0.5)));
      Repeat(w, 7, () => guess.Set(Mul(w, Add(w, guess.Get(), Div(w, m.Get(), guess.Get())), Ext(0.5))));
      w.B.Ret(w.B.Call(IrType.F80, this.Scale, guess.Get(), w.B.Binary(IrBinaryOp.AShr, k.Get(), w.I32(1))));
    });

    private IrFunction? _sineCosine;

    /// <summary>
    /// <c>sin x</c> for a <c>shift</c> of 0 and <c>cos x</c> for 1: <c>x = q π/2 + r</c> with
    /// <c>|r| &lt;= π/4</c> (a two-part <c>π/2</c>), and quadrant <c>q + shift</c> picks
    /// <c>±sin r</c> or <c>±cos r</c>, each a Taylor series of twelve terms. <c>q</c> stays a float,
    /// rounded to a whole number by adding and taking away <c>1.5 * 2^63</c>, so an argument reduces
    /// as far as the x87's FSIN does; past <c>2^62</c> there is no fraction of a turn left, and
    /// that is error 5.
    /// </summary>
    private IrFunction SineCosine => this._sineCosine ??= this.Internal("rt.math.sincos", IrType.F80, [IrType.F80, IrType.I32], w => {
      var (x, shift) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      w.If(w.Cmp(IrCmpPred.Fogt, x, Ext(4.6e18)), () => this.Fail(w, 5));
      w.If(w.Cmp(IrCmpPred.Folt, x, Ext(-4.6e18)), () => this.Fail(w, 5));
      var qf = RoundToWhole(w, Mul(w, x, Ext(TwoOverPi)));
      var quarter = Mul(w, qf, Ext(0.25));
      var q = w.B.Cast(IrCastOp.FPToSIRound, Mul(w, Sub(w, quarter, RoundToWhole(w, quarter)), Ext(4)), IrType.I32);
      var r = Sub(w, Sub(w, x, Mul(w, qf, Ext(PiOverTwo))), Mul(w, qf, Ext(PiOverTwoLow)));
      var r2 = Mul(w, r, r);
      var quadrant = w.B.And(w.B.Add(q, shift), w.I32(3));
      var cosine = w.Cmp(IrCmpPred.Ne, w.B.And(quadrant, w.I32(1)), w.I32(0));
      var term = w.Variable(IrType.F80, w.B.Select(cosine, Ext(1), r));
      var sum = w.Variable(IrType.F80, term.Get());
      var n = w.Variable(IrType.F80, w.B.Select(cosine, Ext(0), Ext(1)));
      Repeat(w, 12, () => {
        var next = Add(w, n.Get(), Ext(1));
        var after = Add(w, n.Get(), Ext(2));
        term.Set(Sub(w, Ext(0), Div(w, Mul(w, term.Get(), r2), Mul(w, next, after))));
        n.Set(after);
        sum.Set(Add(w, sum.Get(), term.Get()));
      });
      w.If(w.Cmp(IrCmpPred.Ne, w.B.And(quadrant, w.I32(2)), w.I32(0)), () => w.Return(Sub(w, Ext(0), sum.Get())));
      w.B.Ret(sum.Get());
    });

    /// <summary>
    /// The whole number nearest <c>x</c> (<c>|x| &lt; 2^62</c>): EXTENDED's 64-bit significand has
    /// no bits below the units once <c>1.5 * 2^63</c> is added, so the sum rounds to nearest-even.
    /// </summary>
    private static IrValue RoundToWhole(IrWriter w, IrValue x) {
      const double shifter = 1.5 * 9223372036854775808.0;
      return Sub(w, Add(w, x, Ext(shifter)), Ext(shifter));
    }

    private IrFunction Sine => this.MathFunction("sin", 1, w => w.B.Ret(w.B.Call(IrType.F80, this.SineCosine, w.Function.Parameters[0], w.I32(0))));
    private IrFunction Cosine => this.MathFunction("cos", 1, w => w.B.Ret(w.B.Call(IrType.F80, this.SineCosine, w.Function.Parameters[0], w.I32(1))));

    private IrFunction Tangent => this.MathFunction("tan", 1, w => {
      var x = w.Function.Parameters[0];
      w.B.Ret(Div(w, w.B.Call(IrType.F80, this.SineCosine, x, w.I32(0)), w.B.Call(IrType.F80, this.SineCosine, x, w.I32(1))));
    });

    /// <summary>
    /// <c>atan x</c>: the odd symmetry, <c>atan a = π/2 - atan(1/a)</c> past 1, and
    /// <c>atan a = π/6 + atan((a√3 - 1)/(a + √3))</c> past <c>tan 15°</c> leave an argument below
    /// 0.268, whose Taylor series is below EXTENDED's last bit after eighteen terms.
    /// </summary>
    private IrFunction ArcTangent => this.MathFunction("atan", 1, w => {
      var x = w.Function.Parameters[0];
      var negative = w.Cmp(IrCmpPred.Folt, x, Ext(0));
      var a = w.Variable(IrType.F80, w.B.Select(negative, Sub(w, Ext(0), x), x));
      var inverted = w.Cmp(IrCmpPred.Fogt, a.Get(), Ext(1));
      w.If(inverted, () => a.Set(Div(w, Ext(1), a.Get())));
      var shifted = w.Cmp(IrCmpPred.Fogt, a.Get(), Ext(Tan15));
      w.If(shifted, () => a.Set(Div(w, Sub(w, Mul(w, a.Get(), Ext(Sqrt3)), Ext(1)), Add(w, a.Get(), Ext(Sqrt3)))));
      var a2 = Mul(w, a.Get(), a.Get());
      var term = w.Variable(IrType.F80, a.Get());
      var sum = w.Variable(IrType.F80, a.Get());
      var n = w.Variable(IrType.F80, Ext(1));
      Repeat(w, 18, () => {
        term.Set(Sub(w, Ext(0), Mul(w, term.Get(), a2)));
        n.Set(Add(w, n.Get(), Ext(2)));
        sum.Set(Add(w, sum.Get(), Div(w, term.Get(), n.Get())));
      });
      var angle = w.Variable(IrType.F80, sum.Get());
      w.If(shifted, () => angle.Set(Add(w, angle.Get(), Ext(PiOverSix))));
      w.If(inverted, () => angle.Set(Sub(w, Ext(PiOverTwo), angle.Get())));
      w.If(negative, () => angle.Set(Sub(w, Ext(0), angle.Get())));
      w.B.Ret(angle.Get());
    });

    /// <summary>
    /// <c>x ^ y</c>. A zero base gives 1 for a zero exponent and 0 otherwise, as on the x87 targets.
    /// A whole exponent - the common case - multiplies by repeated squaring, so a power that fits is
    /// exact and a negative base needs nothing special; any other exponent is <c>e^(y ln x)</c> and
    /// needs a positive base (else error 5).
    /// </summary>
    private IrFunction RaiseToPower => this.MathFunction("pow", 2, w => {
      var (x, y) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      w.If(w.Cmp(IrCmpPred.Foeq, x, Ext(0)),
        () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Foeq, y, Ext(0)), Ext(1), Ext(0))));
      var small = w.B.And(w.Cmp(IrCmpPred.Foge, y, Ext(-2147483647)), w.Cmp(IrCmpPred.Fole, y, Ext(2147483647)));
      w.If(small, () => {
        var whole = w.B.Cast(IrCastOp.FPToSI, y, IrType.I32);
        w.If(w.Cmp(IrCmpPred.Foeq, Float(w, whole), y), () => {
          var n = w.Variable(IrType.I32, w.B.Select(w.Cmp(IrCmpPred.Slt, whole, w.I32(0)), w.B.Sub(w.I32(0), whole), whole));
          var factor = w.Variable(IrType.F80, x);
          var product = w.Variable(IrType.F80, Ext(1));
          w.While(() => w.Cmp(IrCmpPred.Ne, n.Get(), w.I32(0)), () => {
            w.If(w.Cmp(IrCmpPred.Ne, w.B.And(n.Get(), w.I32(1)), w.I32(0)), () => product.Set(Mul(w, product.Get(), factor.Get())));
            n.Set(w.B.Binary(IrBinaryOp.LShr, n.Get(), w.I32(1)));
            w.If(w.Cmp(IrCmpPred.Ne, n.Get(), w.I32(0)), () => factor.Set(Mul(w, factor.Get(), factor.Get())));
          });
          w.If(w.Cmp(IrCmpPred.Slt, whole, w.I32(0)), () => w.Return(Div(w, Ext(1), product.Get())));
          w.Return(product.Get());
        });
      });
      this.RequirePositive(w, x, allowZero: false);
      w.B.Ret(w.B.Call(IrType.F80, this.ExpE, Mul(w, y, w.B.Call(IrType.F80, this.Ln, x))));
    });
  }
}
