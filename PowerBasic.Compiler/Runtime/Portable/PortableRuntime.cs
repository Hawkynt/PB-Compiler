using System.Numerics;
using PowerBasic.Compiler.Ir;
using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// The runtime for native targets that are not DOS, written once as IR and compiled by each back end
/// like the program itself. It defines the <c>rt_*</c> functions a module declares - the same ABI
/// <c>runtime/pbc_rt.h</c> documents - on top of two primitives a back end emits for its operating
/// system: <c>sys_write(buffer, length)</c> to standard output and <c>sys_exit(code)</c>.
///
/// <para>
/// Only what the module calls is defined, so a program that never prints a float carries no float
/// printer. A routine it asks for that this runtime does not have stays a declaration, and the back
/// end declines it by name.
/// </para>
/// </summary>
public static class PortableRuntime {

  /// <summary>BASIC's print zone: the comma separator's tab stop.</summary>
  private const int ZoneWidth = 14;

  /// <summary>The powers <c>10^(2^i)</c> the float printer scales by, for i = 0..12.</summary>
  private const int PowerCount = 13;

  /// <summary>Defines, in <paramref name="module"/>, every runtime function it declares that this runtime implements.</summary>
  public static void Define(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    new Definer(module).Run();
  }

  private sealed class Definer(IrModule module) {

    private IrFunction? _write, _exit, _out, _printSigned, _printUnsigned;
    private IrGlobalVariable? _column, _powers;
    private readonly List<IrFunction> _defined = [];

    public void Run() {
      // rt_error is always there: a back end raises BASIC's errors through it
      Declare("rt_error", IrType.Void, IrType.I32);
      foreach (var function in module.Functions.Where(function => function.IsDeclaration).ToList())
        this.DefineIfKnown(function);
      foreach (var function in this._defined) {
        Mem2Reg.Run(function);
        SimplifyCfg.Run(function);
        Dce.Run(function);
      }
      var errors = IrVerifier.Verify(module);
      if (errors.Count != 0)
        throw new InvalidOperationException("the portable runtime built invalid IR: " + string.Join("; ", errors));
    }

    private IrFunction Declare(string name, IrType returnType, params IrType[] parameters)
      => module.FindFunction(name)
        ?? module.AddFunction(new IrFunction(name, returnType, parameters.Select((type, i) => new IrArgument(type, i))));

    private void DefineIfKnown(IrFunction function) {
      Action<IrWriter>? body = function.Name switch {
        "rt_print_i8" or "rt_print_i16" or "rt_print_i32" or "rt_print_i64" => w => this.PrintInteger(w, signed: true),
        "rt_print_u8" or "rt_print_u16" or "rt_print_u32" => w => this.PrintInteger(w, signed: false),
        "rt_print_str" => w => {
          w.B.Call(IrType.Void, this.Out, w.Function.Parameters[0], w.Function.Parameters[1]);
          w.B.Ret();
        },
        "rt_print_nl" => w => this.PrintByte(w, '\n'),
        "rt_print_comma" or "rt_print_zone" => this.PrintZone,
        "rt_print_tab" => this.PrintTab,
        "rt_print_spc" => this.PrintSpaces,
        "rt_print_single" => w => this.PrintFloat(w, 7),
        "rt_print_double" => w => this.PrintFloat(w, 15),
        "rt_print_ext" => w => this.PrintFloat(w, 18),
        "rt_error" => this.Error,
        "rt_end" => w => {
          w.B.Call(IrType.Void, this.Exit, w.B.SExt(w.Function.Parameters[0], IrType.I32));
          w.B.Ret();
        },
        "rt_inp" => w => w.B.Ret(IrBuilder.ConstInt(w.Function.ReturnType, 0)),
        "rt_outp" => w => w.B.Ret(),
        _ => null,
      };
      if (body is null)
        return;
      this.Build(function, body);
    }

    private void Build(IrFunction function, Action<IrWriter> body) {
      var writer = new IrWriter(function);
      body(writer);
      this._defined.Add(function);
    }

    /// <summary>A runtime-internal function, defined on first use.</summary>
    private IrFunction Internal(string name, IrType returnType, IrType[] parameters, Action<IrWriter> body) {
      if (module.FindFunction(name) is { } existing)
        return existing;
      var function = module.AddFunction(new IrFunction(name, returnType, parameters.Select((type, i) => new IrArgument(type, i))));
      this.Build(function, body);
      return function;
    }

    private IrFunction Write => this._write ??= this.Declare("sys_write", IrType.Void, IrType.Ptr, IrType.I32);
    private IrFunction Exit => this._exit ??= this.Declare("sys_exit", IrType.Void, IrType.I32);

    /// <summary>The output column, counted from zero, that zones and TAB measure from.</summary>
    private IrGlobalVariable Column => this._column ??= module.AddGlobal(new IrGlobalVariable("rt.column", IrType.I32));

    /// <summary><c>rt.out(buffer, length)</c>: writes, and keeps the column.</summary>
    private IrFunction Out => this._out ??= this.Internal("rt.out", IrType.Void, [IrType.Ptr, IrType.I32], w => {
      var (buffer, length) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var i = w.Variable(IrType.I32, w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), length), () => {
        var column = w.B.Load(IrType.I32, this.Column);
        w.If(w.Cmp(IrCmpPred.Eq, w.ByteAt(buffer, i.Get()), w.I8('\n')),
          () => w.B.Store(w.I32(0), this.Column),
          () => w.B.Store(w.B.Add(column, w.I32(1)), this.Column));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      w.B.Call(IrType.Void, this.Write, buffer, length);
      w.B.Ret();
    });

    private void OutByte(IrWriter w, IrValue character) {
      var buffer = w.Buffer(1);
      w.B.Store(character, buffer);
      w.B.Call(IrType.Void, this.Out, buffer, w.I32(1));
    }

    private void PrintByte(IrWriter w, char character) {
      this.OutByte(w, w.I8(character));
      w.B.Ret();
    }

    private void PrintZone(IrWriter w) {
      // at least one space, then on to the next multiple of fourteen
      var loop = w.Block("zone");
      var done = w.Block("zoned");
      w.B.Br(loop);
      w.B.Position(loop);
      this.OutByte(w, w.I8(' '));
      var column = w.B.Load(IrType.I32, this.Column);
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, w.B.Binary(IrBinaryOp.SRem, column, w.I32(ZoneWidth)), w.I32(0)), loop, done);
      w.B.Position(done);
      w.B.Ret();
    }

    private void PrintTab(IrWriter w) {
      var target = w.Variable(IrType.I32, w.Function.Parameters[0]);
      w.If(w.Cmp(IrCmpPred.Slt, target.Get(), w.I32(1)), () => target.Set(w.I32(1)));
      w.While(() => w.Cmp(IrCmpPred.Sgt, w.B.Load(IrType.I32, this.Column), w.B.Sub(target.Get(), w.I32(1))),
        () => this.OutByte(w, w.I8('\n')));
      w.While(() => w.Cmp(IrCmpPred.Slt, w.B.Load(IrType.I32, this.Column), w.B.Sub(target.Get(), w.I32(1))),
        () => this.OutByte(w, w.I8(' ')));
      w.B.Ret();
    }

    private void PrintSpaces(IrWriter w) {
      var count = w.Variable(IrType.I32, w.Function.Parameters[0]);
      w.While(() => w.Cmp(IrCmpPred.Sgt, count.Get(), w.I32(0)), () => {
        this.OutByte(w, w.I8(' '));
        count.Set(w.B.Sub(count.Get(), w.I32(1)));
      });
      w.B.Ret();
    }

    private void Error(IrWriter w) {
      // "Error n", a new line, and the program is over
      var text = w.Buffer(5);
      for (var i = 0; i < 5; ++i)
        w.SetByte(text, w.I32(i), w.I8("Error"[i]));
      w.B.Call(IrType.Void, this.Out, text, w.I32(5));
      w.B.Call(IrType.Void, this.PrintSigned, w.B.SExt(w.Function.Parameters[0], IrType.I64));
      this.OutByte(w, w.I8('\n'));
      w.B.Call(IrType.Void, this.Exit, w.I32(3));
      w.B.Ret();
    }

    // --- integers ------------------------------------------------------------------------------

    private void PrintInteger(IrWriter w, bool signed) {
      var value = w.Function.Parameters[0];
      IrValue wide = value.Type.Bits == 64 ? value : signed ? w.B.SExt(value, IrType.I64) : w.B.ZExt(value, IrType.I64);
      w.B.Call(IrType.Void, signed ? this.PrintSigned : this.PrintUnsigned, wide);
      w.B.Ret();
    }

    private IrFunction PrintSigned => this._printSigned ??= this.Internal("rt.printSigned", IrType.Void, [IrType.I64],
      w => this.PrintNumber(w, signed: true));

    private IrFunction PrintUnsigned => this._printUnsigned ??= this.Internal("rt.printUnsigned", IrType.Void, [IrType.I64],
      w => this.PrintNumber(w, signed: false));

    /// <summary>BASIC's number: a sign slot, the digits, a trailing space - built backwards in a buffer.</summary>
    private void PrintNumber(IrWriter w, bool signed) {
      var value = w.Function.Parameters[0];
      var buffer = w.Buffer(24);
      var at = w.Variable(IrType.I32, w.I32(23));
      w.SetByte(buffer, at.Get(), w.I8(' '));
      var negative = signed ? w.Cmp(IrCmpPred.Slt, value, w.I64(0)) : IrBuilder.ConstBool(false);
      var magnitude = w.Variable(IrType.I64, signed ? w.B.Select(negative, w.B.Sub(w.I64(0), value), value) : value);
      var loop = w.Block("digit");
      var done = w.Block("digits");
      w.B.Br(loop);
      w.B.Position(loop);
      at.Set(w.B.Sub(at.Get(), w.I32(1)));
      var digit = w.B.Trunc(w.B.Binary(IrBinaryOp.URem, magnitude.Get(), w.I64(10)), IrType.I8);
      w.SetByte(buffer, at.Get(), w.B.Add(digit, w.I8('0')));
      magnitude.Set(w.B.Binary(IrBinaryOp.UDiv, magnitude.Get(), w.I64(10)));
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, magnitude.Get(), w.I64(0)), loop, done);
      w.B.Position(done);
      at.Set(w.B.Sub(at.Get(), w.I32(1)));
      w.SetByte(buffer, at.Get(), w.B.Select(negative, w.I8('-'), w.I8(' ')));
      w.B.Call(IrType.Void, this.Out, w.B.Gep(buffer, at.Get()), w.B.Sub(w.I32(24), at.Get()));
      w.B.Ret();
    }

    // --- floats --------------------------------------------------------------------------------

    /// <summary><c>10^(2^i)</c> as correctly rounded 80-bit extended values, ten bytes each.</summary>
    private IrGlobalVariable Powers => this._powers ??= module.AddGlobal(new IrGlobalVariable("rt.powersOfTen", IrType.I8) {
      Count = PowerCount * 10,
      Bytes = PowersOfTen(),
      IsZeroInitialized = false,
    });

    private static byte[] PowersOfTen() {
      var bytes = new byte[PowerCount * 10];
      for (var i = 0; i < PowerCount; ++i) {
        var value = Extended(BigInteger.Pow(10, 1 << i));
        BitConverter.GetBytes(value.Significand).CopyTo(bytes, i * 10);
        BitConverter.GetBytes(value.SignExponent).CopyTo(bytes, i * 10 + 8);
      }
      return bytes;
    }

    /// <summary>A positive integer as an 80-bit extended value, rounded to nearest-even.</summary>
    internal static IrFloat80 Extended(BigInteger value) {
      var exponent = (int)value.GetBitLength() - 1;
      BigInteger significand;
      if (exponent <= 63) {
        significand = value << (63 - exponent);
      } else {
        var shift = exponent - 63;
        significand = value >> shift;
        var remainder = value - (significand << shift);
        var half = BigInteger.One << (shift - 1);
        if (remainder > half || (remainder == half && !significand.IsEven))
          significand += 1;
        if (significand.GetBitLength() > 64) {
          significand >>= 1;
          ++exponent;
        }
      }
      return new IrFloat80((ushort)(exponent + 16383), (ulong)significand);
    }

    private IrValue Power(IrWriter w, IrValue index)
      => w.B.Load(IrType.F80, w.B.Gep(this.Powers, w.B.Mul(index, w.I32(10))));

    /// <summary>
    /// BASIC's float: at most <paramref name="digits"/> significant digits, trailing zeros dropped,
    /// no zero before the point of a fraction, and E notation outside 1E-5 .. 10^digits - the
    /// <c>%G</c> shape. The value is scaled by powers of ten into a <paramref name="digits"/>-digit
    /// integer, rounded to nearest-even, and the digits are laid out around the decimal exponent.
    /// </summary>
    private void PrintFloat(IrWriter w, int digits) {
      var value = w.Function.Parameters[0];
      var zero = w.Extended(0);
      var output = w.Buffer(48);
      var length = w.Variable(IrType.I32, w.I32(0));
      void Emit(IrValue character) {
        w.SetByte(output, length.Get(), character);
        length.Set(w.B.Add(length.Get(), w.I32(1)));
      }

      w.If(w.Cmp(IrCmpPred.Foeq, value, zero), () => {
        foreach (var character in " 0 ")
          Emit(w.I8(character));
        w.B.Call(IrType.Void, this.Out, output, length.Get());
        w.Return();
      });

      var negative = w.Cmp(IrCmpPred.Folt, value, zero);
      var magnitude = w.B.Select(negative, w.B.Binary(IrBinaryOp.FSub, zero, value), value);

      // the decimal exponent: divide or multiply down into [1, 10), greatest power first
      var reduced = w.Variable(IrType.F80, magnitude);
      var exponent = w.Variable(IrType.I32, w.I32(0));
      var i = w.Variable(IrType.I32, w.I32(PowerCount - 1));
      w.If(w.Cmp(IrCmpPred.Foge, magnitude, w.Extended(1)),
        () => w.While(() => w.Cmp(IrCmpPred.Sge, i.Get(), w.I32(0)), () => {
          var power = this.Power(w, i.Get());
          w.If(w.Cmp(IrCmpPred.Foge, reduced.Get(), power), () => {
            reduced.Set(w.B.Binary(IrBinaryOp.FDiv, reduced.Get(), power));
            exponent.Set(w.B.Add(exponent.Get(), w.B.Shl(w.I32(1), i.Get())));
          });
          i.Set(w.B.Sub(i.Get(), w.I32(1)));
        }),
        () => w.While(() => w.Cmp(IrCmpPred.Sge, i.Get(), w.I32(0)), () => {
          var scaled = w.B.Binary(IrBinaryOp.FMul, reduced.Get(), this.Power(w, i.Get()));
          w.If(w.Cmp(IrCmpPred.Folt, scaled, w.Extended(10)), () => {
            reduced.Set(scaled);
            exponent.Set(w.B.Sub(exponent.Get(), w.B.Shl(w.I32(1), i.Get())));
          });
          i.Set(w.B.Sub(i.Get(), w.I32(1)));
        }));

      // scale the value itself to `digits` digits, correcting the exponent if rounding says so
      var mantissa = w.Variable(IrType.I64, w.I64(0));
      var scale = w.Block("scale");
      var scaled = w.Block("scaled");
      w.B.Br(scale);
      w.B.Position(scale);
      var k = w.B.Sub(w.I32(digits - 1), exponent.Get());
      var up = w.Cmp(IrCmpPred.Sgt, k, w.I32(0));
      var remaining = w.Variable(IrType.I32, w.B.Select(up, k, w.B.Sub(w.I32(0), k)));
      var product = w.Variable(IrType.F80, magnitude);
      i.Set(w.I32(0));
      w.While(() => w.Cmp(IrCmpPred.Ne, remaining.Get(), w.I32(0)), () => {
        w.If(w.Cmp(IrCmpPred.Ne, w.B.And(remaining.Get(), w.I32(1)), w.I32(0)), () => {
          var power = this.Power(w, i.Get());
          product.Set(w.B.Select(up, w.B.Binary(IrBinaryOp.FMul, product.Get(), power),
            w.B.Binary(IrBinaryOp.FDiv, product.Get(), power)));
        });
        remaining.Set(w.B.Binary(IrBinaryOp.LShr, remaining.Get(), w.I32(1)));
        i.Set(w.B.Add(i.Get(), w.I32(1)));
      });
      mantissa.Set(w.B.Cast(IrCastOp.FPToSIRound, product.Get(), IrType.I64));
      w.If(w.Cmp(IrCmpPred.Sge, mantissa.Get(), w.I64(Pow10(digits))), () => {
        exponent.Set(w.B.Add(exponent.Get(), w.I32(1)));
        w.B.Br(scale);
        w.B.Position(w.Block("after"));
      });
      w.If(w.Cmp(IrCmpPred.Slt, mantissa.Get(), w.I64(Pow10(digits - 1))), () => {
        exponent.Set(w.B.Sub(exponent.Get(), w.I32(1)));
        w.B.Br(scale);
        w.B.Position(w.Block("after"));
      });
      w.B.Br(scaled);
      w.B.Position(scaled);

      // the digits, most significant first; trailing zeros are not significant
      var digitText = w.Buffer(digits);
      var j = w.Variable(IrType.I32, w.I32(digits - 1));
      w.While(() => w.Cmp(IrCmpPred.Sge, j.Get(), w.I32(0)), () => {
        var digit = w.B.Trunc(w.B.Binary(IrBinaryOp.URem, mantissa.Get(), w.I64(10)), IrType.I8);
        w.SetByte(digitText, j.Get(), w.B.Add(digit, w.I8('0')));
        mantissa.Set(w.B.Binary(IrBinaryOp.UDiv, mantissa.Get(), w.I64(10)));
        j.Set(w.B.Sub(j.Get(), w.I32(1)));
      });
      var significant = w.Variable(IrType.I32, w.I32(digits));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, significant.Get(), w.I32(1)),
          w.Cmp(IrCmpPred.Eq, w.ByteAt(digitText, w.B.Sub(significant.Get(), w.I32(1))), w.I8('0'))),
        () => significant.Set(w.B.Sub(significant.Get(), w.I32(1))));

      void CopyDigits(IrValue from) {
        var at = w.Variable(IrType.I32, from);
        w.While(() => w.Cmp(IrCmpPred.Slt, at.Get(), significant.Get()), () => {
          Emit(w.ByteAt(digitText, at.Get()));
          at.Set(w.B.Add(at.Get(), w.I32(1)));
        });
      }

      Emit(w.B.Select(negative, w.I8('-'), w.I8(' ')));
      var e = exponent.Get();
      var scientific = w.B.Or(w.Cmp(IrCmpPred.Slt, e, w.I32(-4)), w.Cmp(IrCmpPred.Sge, e, w.I32(digits)));
      w.If(scientific, () => {
        Emit(w.ByteAt(digitText, w.I32(0)));
        w.If(w.Cmp(IrCmpPred.Sgt, significant.Get(), w.I32(1)), () => {
          Emit(w.I8('.'));
          CopyDigits(w.I32(1));
        });
        Emit(w.I8('E'));
        var power = exponent.Get();
        Emit(w.B.Select(w.Cmp(IrCmpPred.Slt, power, w.I32(0)), w.I8('-'), w.I8('+')));
        var absolute = w.Variable(IrType.I32, w.B.Select(w.Cmp(IrCmpPred.Slt, power, w.I32(0)), w.B.Sub(w.I32(0), power), power));
        foreach (var place in new[] { 1000, 100 }) {
          var p = place;
          w.If(w.Cmp(IrCmpPred.Sge, absolute.Get(), w.I32(p)),
            () => Emit(w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.URem, w.B.Binary(IrBinaryOp.UDiv, absolute.Get(), w.I32(p)), w.I32(10)), IrType.I8), w.I8('0'))));
        }
        Emit(w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.URem, w.B.Binary(IrBinaryOp.UDiv, absolute.Get(), w.I32(10)), w.I32(10)), IrType.I8), w.I8('0')));
        Emit(w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.URem, absolute.Get(), w.I32(10)), IrType.I8), w.I8('0')));
      }, () => w.If(w.Cmp(IrCmpPred.Sge, exponent.Get(), w.I32(0)), () => {
        // the integer part, padded with zeros past the significant digits, then any fraction
        var at = w.Variable(IrType.I32, w.I32(0));
        w.While(() => w.Cmp(IrCmpPred.Sle, at.Get(), exponent.Get()), () => {
          Emit(w.B.Select(w.Cmp(IrCmpPred.Slt, at.Get(), significant.Get()), w.ByteAt(digitText, at.Get()), w.I8('0')));
          at.Set(w.B.Add(at.Get(), w.I32(1)));
        });
        var fraction = w.B.Add(exponent.Get(), w.I32(1));
        w.If(w.Cmp(IrCmpPred.Sgt, significant.Get(), fraction), () => {
          Emit(w.I8('.'));
          CopyDigits(w.B.Add(exponent.Get(), w.I32(1)));
        });
      }, () => {
        // a pure fraction: no leading zero, the point, then zeros up to the first digit
        Emit(w.I8('.'));
        var zeros = w.Variable(IrType.I32, w.B.Sub(w.I32(-1), exponent.Get()));
        w.While(() => w.Cmp(IrCmpPred.Sgt, zeros.Get(), w.I32(0)), () => {
          Emit(w.I8('0'));
          zeros.Set(w.B.Sub(zeros.Get(), w.I32(1)));
        });
        CopyDigits(w.I32(0));
      }));
      Emit(w.I8(' '));
      w.B.Call(IrType.Void, this.Out, output, length.Get());
      w.B.Ret();
    }

    private static long Pow10(int n) {
      var value = 1L;
      for (var i = 0; i < n; ++i)
        value *= 10;
      return value;
    }
  }
}
