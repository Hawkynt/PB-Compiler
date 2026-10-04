using PowerBasic.Compiler.Syntax;
using System.Numerics;
using PowerBasic.Compiler.Ir;

namespace PowerBasic.Compiler.Runtime.Portable;

/// <summary>
/// Numbers as text, in both directions. The formatters write BASIC's shape into a buffer - a sign slot
/// (a space or the minus), then the digits - and answer the length; <c>PRINT</c> adds the trailing
/// space and writes it, <c>STR$</c> makes a string of it. <c>VAL</c> reads a number the way BASIC
/// does, stopping at the first character that cannot continue it.
/// </summary>
public static partial class PortableRuntime {

  /// <summary>The powers <c>10^(2^i)</c> the float formatter and VAL scale by, for i = 0..12.</summary>
  private const int PowerCount = 13;

  /// <summary>Room for any formatted number: a sign, 18 digits, a point, an exponent.</summary>
  private const int NumberText = 48;

  private sealed partial class Definer {

    private IrFunction? _formatSigned, _formatUnsigned;
    private readonly Dictionary<int, IrFunction> _formatFloat = [];
    private IrGlobalVariable? _powers;

    private Action<IrWriter>? NumberRoutine(string name) => name switch {
      "rt_print_i8" or "rt_print_i16" or "rt_print_i32" => w => this.PrintNumber(w, this.Widened(w, signed: true), this.FormatSigned),
      // a QUAD prints as the DOS runtime prints it - through the DOUBLE formatter, so exact digits
      // only below 10^15 and 9.22337203685478E+18 above
      "rt_print_i64" => w => this.PrintNumber(w, w.B.Cast(IrCastOp.SIToFP, w.Function.Parameters[0], IrType.F80), this.FormatFloat(15)),
      "rt_print_u8" or "rt_print_u16" or "rt_print_u32" => w => this.PrintNumber(w, this.Widened(w, signed: false), this.FormatUnsigned),
      "rt_print_single" => w => this.PrintNumber(w, w.Function.Parameters[0], this.FormatFloat(7)),
      "rt_print_double" => w => this.PrintNumber(w, w.Function.Parameters[0], this.FormatFloat(15)),
      // an EXT prints as a DOUBLE does: the DOS runtime has one formatter for both
      "rt_print_ext" => w => this.PrintNumber(w, w.Function.Parameters[0], this.FormatFloat(15)),
      "rt_rnd" => w => w.B.Ret(w.B.Call(IrType.F64, this.Random)),
      "rt_rnd_range" => this.RandomRange,
      "rt_round_half_away" => this.RoundHalfAway,
      "rt_round_places" => this.RoundPlaces,
      "rt_str_from_i8" or "rt_str_from_i16" or "rt_str_from_i32" => w => this.NumberString(w, this.Widened(w, signed: true), this.FormatSigned),
      "rt_str_from_i64" => w => this.NumberString(w, w.B.Cast(IrCastOp.SIToFP, w.Function.Parameters[0], IrType.F80), this.FormatFloat(15)),
      "rt_str_from_u8" or "rt_str_from_u16" or "rt_str_from_u32" => w => this.NumberString(w, this.Widened(w, signed: false), this.FormatUnsigned),
      "rt_str_from_single" => w => this.NumberString(w, w.Function.Parameters[0], this.FormatFloat(7)),
      "rt_str_from_double" => w => this.NumberString(w, w.Function.Parameters[0], this.FormatFloat(15)),
      // STR$ of an EXT renders fifteen digits, as genuine PBC 3.50 does
      "rt_str_from_ext" => w => this.NumberString(w, w.Function.Parameters[0], this.FormatFloat(15)),
      "rt_str_hex" => w => this.Radix(w, w.Function.Parameters[0], w.Ix((1 << 8) | 4)),
      "rt_str_oct" => w => this.Radix(w, w.Function.Parameters[0], w.Ix((1 << 8) | 3)),
      "rt_str_bin" => w => this.Radix(w, w.Function.Parameters[0], w.Ix((1 << 8) | 1)),
      "rt_str_radix" => w => this.Radix(w, w.Function.Parameters[0], w.Function.Parameters[1]),
      "rt_str_val" => w => w.B.Ret(w.B.Call(IrType.F64, this.ValFunction, w.Function.Parameters[0])),
      _ => null,
    };

    private IrValue Widened(IrWriter w, bool signed) {
      var value = w.Function.Parameters[0];
      return value.Type.Bits == 64 ? value : signed ? w.B.SExt(value, IrType.I64) : w.B.ZExt(value, IrType.I64);
    }

    /// <summary>PRINT: the formatted number, then its trailing space.</summary>
    private void PrintNumber(IrWriter w, IrValue value, IrFunction format) {
      var buffer = w.Buffer(NumberText);
      var length = w.B.Call(w.Index, format, value, buffer);
      w.SetByte(buffer, length, w.I8(' '));
      w.B.Call(IrType.Void, this.Out, buffer, w.B.Add(length, w.Ix(1)));
      w.B.Ret();
    }

    /// <summary>STR$: the formatted number, sign slot and all, as a string.</summary>
    private void NumberString(IrWriter w, IrValue value, IrFunction format) {
      var buffer = w.Buffer(NumberText);
      var length = w.B.Call(w.Index, format, value, buffer);
      w.B.Ret(this.Make(w, buffer, length));
    }

    private IrFunction FormatSigned => this._formatSigned ??= this.Internal("rt.formatSigned", this.Index, [IrType.I64, IrType.Ptr],
      w => this.FormatInteger(w, signed: true));

    private IrFunction FormatUnsigned => this._formatUnsigned ??= this.Internal("rt.formatUnsigned", this.Index, [IrType.I64, IrType.Ptr],
      w => this.FormatInteger(w, signed: false));

    /// <summary>A sign slot and the digits, built backwards and copied to the front of the buffer.</summary>
    private void FormatInteger(IrWriter w, bool signed) {
      var (value, output) = (w.Function.Parameters[0], w.Function.Parameters[1]);
      var digits = w.Buffer(24);
      var at = w.Variable(w.Index, w.Ix(24));
      var negative = signed ? w.Cmp(IrCmpPred.Slt, value, w.I64(0)) : IrBuilder.ConstBool(false);
      var magnitude = w.Variable(IrType.I64, signed ? w.B.Select(negative, w.B.Sub(w.I64(0), value), value) : value);
      var loop = w.Block("digit");
      var done = w.Block("digits");
      w.B.Br(loop);
      w.B.Position(loop);
      at.Set(w.B.Sub(at.Get(), w.Ix(1)));
      var digit = w.B.Trunc(w.B.Binary(IrBinaryOp.URem, magnitude.Get(), w.I64(10)), IrType.I8);
      w.SetByte(digits, at.Get(), w.B.Add(digit, w.I8('0')));
      magnitude.Set(w.B.Binary(IrBinaryOp.UDiv, magnitude.Get(), w.I64(10)));
      w.B.CondBr(w.Cmp(IrCmpPred.Ne, magnitude.Get(), w.I64(0)), loop, done);
      w.B.Position(done);
      at.Set(w.B.Sub(at.Get(), w.Ix(1)));
      w.SetByte(digits, at.Get(), w.B.Select(negative, w.I8('-'), w.I8(' ')));
      var length = w.B.Sub(w.Ix(24), at.Get());
      w.B.Call(IrType.Void, this.CopyBytes, output, w.B.Gep(digits, at.Get()), length);
      w.B.Ret(length);
    }

    /// <summary>
    /// HEX$, OCT$, BIN$: <paramref name="packed"/> is <c>(minimum digits &lt;&lt; 8) | bits per digit</c>.
    /// The minimum zero-pads and never truncates, and a value in the 16-bit range that arrived
    /// sign-extended renders at sixteen bits - HEX$(-1) is FFFF - as the DOS runtime does.
    /// </summary>
    private void Radix(IrWriter w, IrValue value, IrValue packed) {
      // the value's own arithmetic is 32-bit, whatever width the declaration gave it; only the digit
      // count is the runtime's index
      static IrValue C(long constant) => IrBuilder.ConstI32(constant);
      IrValue Wide(IrValue x) => x.Type.Bits == 32 ? x : x.Type.Bits < 32 ? w.B.SExt(x, IrType.I32) : w.B.Trunc(x, IrType.I32);
      value = Wide(value);
      packed = Wide(packed);
      var bits = w.B.And(packed, C(0xFF));
      var least = w.ToIndex(w.B.And(w.B.Binary(IrBinaryOp.LShr, packed, C(8)), C(0xFF)));
      var remaining = w.Variable(IrType.I32, value);
      w.If(w.B.And(w.Cmp(IrCmpPred.Eq, w.B.Binary(IrBinaryOp.LShr, value, C(16)), C(0xFFFF)),
          w.Cmp(IrCmpPred.Ne, w.B.And(value, C(0x8000)), C(0))),
        () => remaining.Set(w.B.And(value, C(0xFFFF))));
      var mask = w.B.Sub(w.B.Shl(C(1), bits), C(1));
      var reversed = w.Buffer(NumberText);
      var count = w.Variable(w.Index, w.Ix(0));
      var loop = w.Block("radix");
      var done = w.Block("radixed");
      w.B.Br(loop);
      w.B.Position(loop);
      var digit = w.B.Trunc(w.B.And(remaining.Get(), mask), IrType.I8);
      w.SetByte(reversed, count.Get(), w.B.Select(w.Cmp(IrCmpPred.Ult, digit, w.I8(10)),
        w.B.Add(digit, w.I8('0')), w.B.Add(digit, w.I8('A' - 10))));
      count.Set(w.B.Add(count.Get(), w.Ix(1)));
      remaining.Set(w.B.Binary(IrBinaryOp.LShr, remaining.Get(), bits));
      w.B.CondBr(w.B.Or(w.Cmp(IrCmpPred.Ne, remaining.Get(), C(0)), w.Cmp(IrCmpPred.Slt, count.Get(), least)), loop, done);
      w.B.Position(done);
      var text = w.Buffer(NumberText);
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), count.Get()), () => {
        w.SetByte(text, i.Get(), w.ByteAt(reversed, w.B.Sub(w.B.Sub(count.Get(), w.Ix(1)), i.Get())));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      w.B.Ret(this.Make(w, text, count.Get()));
    }

    /// <summary>
    /// VAL: blanks are skipped, before the number and between its digits; then a sign, and either an
    /// <c>&amp;H</c>/<c>&amp;O</c>/<c>&amp;B</c> integer (a 16-bit pattern reads as signed: &amp;HFFFF
    /// is -1) or digits, a point, more digits and an E or D exponent. The first character that cannot
    /// continue the number ends it; no number at all is 0. The digits are gathered into an integer and
    /// scaled once by a power of ten, so VAL("0.1") is the double nearest a tenth.
    /// </summary>
    private void Val(IrWriter w) {
      var handle = w.Function.Parameters[0];
      var (bytes, length) = this.View(w, handle, w.Ix(1), this.Length(w, handle));
      var at = w.Variable(w.Index, w.Ix(0));
      IrValue More() => w.Cmp(IrCmpPred.Slt, at.Get(), length);
      IrValue Current() => w.ByteAt(bytes, at.Get());
      void Next() => at.Set(w.B.Add(at.Get(), w.Ix(1)));
      void SkipBlanks() => w.While(() => w.B.And(More(), w.Cmp(IrCmpPred.Eq, w.B.Select(More(), Current(), w.I8(0)), w.I8(' '))), Next);
      IrValue Peek() => w.B.Select(More(), Current(), w.I8(0));
      IrValue Upper(IrValue character) => w.B.And(character, w.I8(unchecked((sbyte)0xDF)));
      IrValue IsDigit(IrValue character) => w.B.And(w.Cmp(IrCmpPred.Uge, character, w.I8('0')), w.Cmp(IrCmpPred.Ule, character, w.I8('9')));

      SkipBlanks();
      var negative = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      w.If(w.Cmp(IrCmpPred.Eq, Peek(), w.I8('-')), () => { negative.Set(IrBuilder.ConstBool(true)); Next(); },
        () => w.If(w.Cmp(IrCmpPred.Eq, Peek(), w.I8('+')), Next));
      SkipBlanks();

      // &H, &O, &B: an integer in that base
      w.If(w.Cmp(IrCmpPred.Eq, Peek(), w.I8('&')), () => {
        Next();
        var letter = Upper(Peek());
        var shift = w.Variable(IrType.I64, w.I64(3));
        w.If(w.Cmp(IrCmpPred.Eq, letter, w.I8('H')), () => { shift.Set(w.I64(4)); Next(); },
          () => w.If(w.Cmp(IrCmpPred.Eq, letter, w.I8('B')), () => { shift.Set(w.I64(1)); Next(); },
            () => w.If(w.Cmp(IrCmpPred.Eq, letter, w.I8('O')), Next)));
        var total = w.Variable(IrType.I64, w.I64(0));
        var scanning = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
        w.While(() => w.B.And(scanning.Get(), More()), () => {
          // only a letter is folded to upper case: clearing bit 5 of a digit turns '1' into a control code
          var raw = Current();
          var character = w.B.Select(IsDigit(raw), raw, Upper(raw));
          var digit = w.Variable(IrType.I64, w.I64(99));
          w.If(IsDigit(character), () => digit.Set(w.B.ZExt(w.B.Sub(character, w.I8('0')), IrType.I64)),
            () => w.If(w.B.And(w.Cmp(IrCmpPred.Uge, character, w.I8('A')), w.Cmp(IrCmpPred.Ule, character, w.I8('F'))),
              () => digit.Set(w.B.ZExt(w.B.Add(w.B.Sub(character, w.I8('A')), w.I8(10)), IrType.I64))));
          w.If(w.Cmp(IrCmpPred.Sge, digit.Get(), w.B.Shl(w.I64(1), shift.Get())),
            () => scanning.Set(IrBuilder.ConstBool(false)),
            () => {
              total.Set(w.B.Or(w.B.Shl(total.Get(), shift.Get()), digit.Get()));
              Next();
            });
        });
        w.If(w.B.And(w.Cmp(IrCmpPred.Sgt, total.Get(), w.I64(0x7FFF)), w.Cmp(IrCmpPred.Sle, total.Get(), w.I64(0xFFFF))),
          () => total.Set(w.B.Sub(total.Get(), w.I64(0x10000))));
        var result = w.B.Cast(IrCastOp.SIToFP, total.Get(), IrType.F64);
        this.Consume(w, handle);
        w.Return(w.B.Select(negative.Get(), w.B.Binary(IrBinaryOp.FSub, new IrConstantFloat(IrType.F64, 0), result), result));
      });

      // decimal: at most 18 digits are kept; later ones only move the point
      var mantissa = w.Variable(IrType.I64, w.I64(0));
      var kept = w.Variable(w.Index, w.Ix(0));
      var exponent = w.Variable(w.Index, w.Ix(0));
      var seenPoint = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
      var scanning = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
      w.While(() => w.B.And(scanning.Get(), More()), () => {
        var character = Current();
        w.If(IsDigit(character), () => {
          w.If(w.Cmp(IrCmpPred.Slt, kept.Get(), w.Ix(18)), () => {
            mantissa.Set(w.B.Add(w.B.Mul(mantissa.Get(), w.I64(10)), w.B.ZExt(w.B.Sub(character, w.I8('0')), IrType.I64)));
            w.If(w.Cmp(IrCmpPred.Ne, mantissa.Get(), w.I64(0)), () => kept.Set(w.B.Add(kept.Get(), w.Ix(1))));
            w.If(seenPoint.Get(), () => exponent.Set(w.B.Sub(exponent.Get(), w.Ix(1))));
          }, () => w.If(w.B.Xor(seenPoint.Get(), IrBuilder.ConstBool(true)), () => exponent.Set(w.B.Add(exponent.Get(), w.Ix(1)))));
          Next();
        }, () => w.If(w.B.And(w.Cmp(IrCmpPred.Eq, character, w.I8('.')), w.B.Xor(seenPoint.Get(), IrBuilder.ConstBool(true))),
          () => { seenPoint.Set(IrBuilder.ConstBool(true)); Next(); },
          () => w.If(w.Cmp(IrCmpPred.Eq, character, w.I8(' ')), Next, () => scanning.Set(IrBuilder.ConstBool(false)))));
      });
      // an exponent: E or D, a sign, digits
      w.If(w.B.Or(w.Cmp(IrCmpPred.Eq, Upper(Peek()), w.I8('E')), w.Cmp(IrCmpPred.Eq, Upper(Peek()), w.I8('D'))), () => {
        Next();
        var exponentNegative = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
        w.If(w.Cmp(IrCmpPred.Eq, Peek(), w.I8('-')), () => { exponentNegative.Set(IrBuilder.ConstBool(true)); Next(); },
          () => w.If(w.Cmp(IrCmpPred.Eq, Peek(), w.I8('+')), Next));
        var written = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.B.And(More(), IsDigit(Peek())), () => {
          w.If(w.Cmp(IrCmpPred.Slt, written.Get(), w.Ix(10000)),
            () => written.Set(w.B.Add(w.B.Mul(written.Get(), w.Ix(10)), w.B.ZExt(w.B.Sub(Current(), w.I8('0')), w.Index))));
          Next();
        });
        exponent.Set(w.B.Add(exponent.Get(), w.B.Select(exponentNegative.Get(), w.B.Sub(w.Ix(0), written.Get()), written.Get())));
      });
      var scaled = w.Variable(IrType.F80, w.B.Cast(IrCastOp.SIToFP, mantissa.Get(), IrType.F80));
      var up = w.Cmp(IrCmpPred.Sgt, exponent.Get(), w.Ix(0));
      var remaining = w.Variable(w.Index, w.B.Select(up, exponent.Get(), w.B.Sub(w.Ix(0), exponent.Get())));
      var i = w.Variable(w.Index, w.Ix(0));
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Ne, remaining.Get(), w.Ix(0)), w.Cmp(IrCmpPred.Slt, i.Get(), w.Ix(PowerCount))), () => {
        w.If(w.Cmp(IrCmpPred.Ne, w.B.And(remaining.Get(), w.Ix(1)), w.Ix(0)), () => {
          var power = this.Power(w, i.Get());
          scaled.Set(w.B.Select(up, w.B.Binary(IrBinaryOp.FMul, scaled.Get(), power), w.B.Binary(IrBinaryOp.FDiv, scaled.Get(), power)));
        });
        remaining.Set(w.B.Binary(IrBinaryOp.LShr, remaining.Get(), w.Ix(1)));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      var magnitude = w.B.Cast(IrCastOp.FPTrunc, scaled.Get(), IrType.F64);
      this.ReturnConsuming(w, w.B.Select(negative.Get(), w.B.Binary(IrBinaryOp.FSub, new IrConstantFloat(IrType.F64, 0), magnitude), magnitude), handle);
    }

    // --- floats --------------------------------------------------------------------------------

    /// <summary><c>10^(2^i)</c> as correctly rounded 80-bit extended values, ten bytes each.</summary>
    private IrGlobalVariable Powers => this._powers ??= this.Shared(new IrGlobalVariable("rt.powersOfTen", IrType.I8) {
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
      => w.B.Load(IrType.F80, w.B.Gep(this.Powers, w.B.Mul(index, w.Ix(10))));

    /// <summary>
    /// BASIC's float: at most <paramref name="digits"/> significant digits, trailing zeros dropped,
    /// no zero before the point of a fraction, and E notation outside 1E-5 .. 10^digits - the
    /// <c>%G</c> shape. The value is scaled by powers of ten into a <paramref name="digits"/>-digit
    /// integer, rounded to nearest-even, and the digits are laid out around the decimal exponent.
    /// </summary>
    /// <summary>
    /// <c>rt.formatFloat{digits}(value, buffer)</c>, the length written: a thin entry onto the one
    /// shared formatter, which takes the digit count as an argument so a program printing SINGLEs
    /// and DOUBLEs carries it once.
    /// </summary>
    private IrFunction FormatFloat(int digits) {
      digits = this.SignificantDigits(digits);
      if (!this._formatFloat.TryGetValue(digits, out var function)) {
        function = this.Internal($"rt.formatFloat{digits}", this.Index, [IrType.F80, IrType.Ptr],
          w => w.B.Ret(w.B.Call(w.Index, this.FloatFormatter, w.Function.Parameters[0], w.Function.Parameters[1], w.Ix(digits))));
        this._formatFloat[digits] = function;
      }
      return function;
    }

    /// <summary>
    /// The significant digits the source dialect's runtime prints for a SINGLE (7) or a DOUBLE (15):
    /// Turbo Basic prints sixteen for both, and the Microsoft compilers before PDS 7.0 sixteen for a
    /// DOUBLE - the same table the DOS runtime's formatter is built from.
    /// </summary>
    private int SignificantDigits(int digits) {
      var dialect = module.EffectiveDialect;
      if (dialect.IsTurboBasic())
        return 16;
      if (digits == 15 && dialect.Family() == Syntax.DialectFamily.Microsoft && dialect < Syntax.Dialect.Pds70)
        return 16;
      return digits;
    }

    private IrFunction? _floatFormatter;

    private IrFunction FloatFormatter => this._floatFormatter ??= this.Internal("rt.formatFloat", this.Index,
      [IrType.F80, IrType.Ptr, this.Index], this.FloatText);

    private void FloatText(IrWriter w) {
      var digits = w.Function.Parameters[2];
      // 10^(digits - 1) and 10^digits: the range a scaled mantissa must land in
      var lower = w.Variable(IrType.I64, w.I64(1));
      var count = w.Variable(w.Index, w.Ix(1));
      w.While(() => w.Cmp(IrCmpPred.Slt, count.Get(), digits), () => {
        lower.Set(w.B.Mul(lower.Get(), w.I64(10)));
        count.Set(w.B.Add(count.Get(), w.Ix(1)));
      });
      var upper = w.B.Mul(lower.Get(), w.I64(10));
      var value = w.Function.Parameters[0];
      var zero = w.Extended(0);
      var output = w.Function.Parameters[1];
      var length = w.Variable(w.Index, w.Ix(0));
      void Emit(IrValue character) {
        w.SetByte(output, length.Get(), character);
        length.Set(w.B.Add(length.Get(), w.Ix(1)));
      }

      w.If(w.Cmp(IrCmpPred.Foeq, value, zero), () => {
        foreach (var character in " 0")
          Emit(w.I8(character));
        w.Return(length.Get());
      });

      var negative = w.Cmp(IrCmpPred.Folt, value, zero);
      var magnitude = w.B.Select(negative, w.B.Binary(IrBinaryOp.FSub, zero, value), value);

      // the decimal exponent: divide or multiply down into [1, 10), greatest power first
      var reduced = w.Variable(IrType.F80, magnitude);
      var exponent = w.Variable(w.Index, w.Ix(0));
      var i = w.Variable(w.Index, w.Ix(PowerCount - 1));
      w.If(w.Cmp(IrCmpPred.Foge, magnitude, w.Extended(1)),
        () => w.While(() => w.Cmp(IrCmpPred.Sge, i.Get(), w.Ix(0)), () => {
          var power = this.Power(w, i.Get());
          w.If(w.Cmp(IrCmpPred.Foge, reduced.Get(), power), () => {
            reduced.Set(w.B.Binary(IrBinaryOp.FDiv, reduced.Get(), power));
            exponent.Set(w.B.Add(exponent.Get(), w.B.Shl(w.Ix(1), i.Get())));
          });
          i.Set(w.B.Sub(i.Get(), w.Ix(1)));
        }),
        () => w.While(() => w.Cmp(IrCmpPred.Sge, i.Get(), w.Ix(0)), () => {
          var scaled = w.B.Binary(IrBinaryOp.FMul, reduced.Get(), this.Power(w, i.Get()));
          w.If(w.Cmp(IrCmpPred.Folt, scaled, w.Extended(10)), () => {
            reduced.Set(scaled);
            exponent.Set(w.B.Sub(exponent.Get(), w.B.Shl(w.Ix(1), i.Get())));
          });
          i.Set(w.B.Sub(i.Get(), w.Ix(1)));
        }));

      // scale the value itself to `digits` digits, correcting the exponent if rounding says so
      var mantissa = w.Variable(IrType.I64, w.I64(0));
      var scale = w.Block("scale");
      var scaled = w.Block("scaled");
      w.B.Br(scale);
      w.B.Position(scale);
      var k = w.B.Sub(w.B.Sub(digits, w.Ix(1)), exponent.Get());
      var up = w.Cmp(IrCmpPred.Sgt, k, w.Ix(0));
      var remaining = w.Variable(w.Index, w.B.Select(up, k, w.B.Sub(w.Ix(0), k)));
      var product = w.Variable(IrType.F80, magnitude);
      i.Set(w.Ix(0));
      w.While(() => w.Cmp(IrCmpPred.Ne, remaining.Get(), w.Ix(0)), () => {
        w.If(w.Cmp(IrCmpPred.Ne, w.B.And(remaining.Get(), w.Ix(1)), w.Ix(0)), () => {
          var power = this.Power(w, i.Get());
          product.Set(w.B.Select(up, w.B.Binary(IrBinaryOp.FMul, product.Get(), power),
            w.B.Binary(IrBinaryOp.FDiv, product.Get(), power)));
        });
        remaining.Set(w.B.Binary(IrBinaryOp.LShr, remaining.Get(), w.Ix(1)));
        i.Set(w.B.Add(i.Get(), w.Ix(1)));
      });
      mantissa.Set(w.B.Cast(IrCastOp.FPToSIRound, product.Get(), IrType.I64));
      w.If(w.Cmp(IrCmpPred.Sge, mantissa.Get(), upper), () => {
        exponent.Set(w.B.Add(exponent.Get(), w.Ix(1)));
        w.B.Br(scale);
        w.B.Position(w.Block("after"));
      });
      w.If(w.Cmp(IrCmpPred.Slt, mantissa.Get(), lower.Get()), () => {
        exponent.Set(w.B.Sub(exponent.Get(), w.Ix(1)));
        w.B.Br(scale);
        w.B.Position(w.Block("after"));
      });
      w.B.Br(scaled);
      w.B.Position(scaled);

      // the digits, most significant first; trailing zeros are not significant
      var digitText = w.Buffer(18);
      var j = w.Variable(w.Index, w.B.Sub(digits, w.Ix(1)));
      w.While(() => w.Cmp(IrCmpPred.Sge, j.Get(), w.Ix(0)), () => {
        var digit = w.B.Trunc(w.B.Binary(IrBinaryOp.URem, mantissa.Get(), w.I64(10)), IrType.I8);
        w.SetByte(digitText, j.Get(), w.B.Add(digit, w.I8('0')));
        mantissa.Set(w.B.Binary(IrBinaryOp.UDiv, mantissa.Get(), w.I64(10)));
        j.Set(w.B.Sub(j.Get(), w.Ix(1)));
      });
      var significant = w.Variable(w.Index, digits);
      w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, significant.Get(), w.Ix(1)),
          w.Cmp(IrCmpPred.Eq, w.ByteAt(digitText, w.B.Sub(significant.Get(), w.Ix(1))), w.I8('0'))),
        () => significant.Set(w.B.Sub(significant.Get(), w.Ix(1))));

      void CopyDigits(IrValue from) {
        var at = w.Variable(w.Index, from);
        w.While(() => w.Cmp(IrCmpPred.Slt, at.Get(), significant.Get()), () => {
          Emit(w.ByteAt(digitText, at.Get()));
          at.Set(w.B.Add(at.Get(), w.Ix(1)));
        });
      }

      Emit(w.B.Select(negative, w.I8('-'), w.I8(' ')));
      var e = exponent.Get();
      // the layout is the source dialect's, as the DOS runtime lays it out: a number with more integer
      // digits than are significant is written with an exponent; a fraction keeps its zeros down to
      // 1E-7 under PowerBASIC, down to its own digit count under QuickBASIC's DOUBLE, and not below
      // .1 under Turbo Basic; and only the Microsoft family pads the exponent (to two digits, three
      // under Turbo Basic) and marks a DOUBLE's with D
      var dialect = module.EffectiveDialect;
      var microsoft = dialect.Family() == Syntax.DialectFamily.Microsoft && !dialect.IsTurboBasic();
      IrValue smallest = dialect.IsTurboBasic() ? w.Ix(-1)
        : microsoft ? w.B.Select(w.Cmp(IrCmpPred.Eq, digits, w.Ix(7)), w.Ix(-7), w.B.Sub(w.Ix(-1), digits))
        : w.Ix(-7);
      var scientific = w.B.Or(w.Cmp(IrCmpPred.Slt, e, smallest), w.Cmp(IrCmpPred.Sge, e, digits));
      w.If(scientific, () => {
        Emit(w.ByteAt(digitText, w.Ix(0)));
        w.If(w.Cmp(IrCmpPred.Sgt, significant.Get(), w.Ix(1)), () => {
          Emit(w.I8('.'));
          CopyDigits(w.Ix(1));
        });
        Emit(microsoft ? w.B.Select(w.Cmp(IrCmpPred.Eq, digits, w.Ix(7)), w.I8('E'), w.I8('D')) : w.I8('E'));
        var power = exponent.Get();
        Emit(w.B.Select(w.Cmp(IrCmpPred.Slt, power, w.Ix(0)), w.I8('-'), w.I8('+')));
        var absolute = w.Variable(w.Index, w.B.Select(w.Cmp(IrCmpPred.Slt, power, w.Ix(0)), w.B.Sub(w.Ix(0), power), power));
        var pad = dialect.IsTurboBasic() ? 3 : microsoft ? 2 : 1;
        foreach (var place in new[] { 1000, 100, 10 }) {
          var p = place;
          // a digit is written when the exponent reaches it, or when the padding asks for it
          var padded = p < (int)Math.Pow(10, pad);
          IrValue wanted = padded ? IrBuilder.ConstBool(true) : w.Cmp(IrCmpPred.Sge, absolute.Get(), w.Ix(p));
          w.If(wanted,
            () => Emit(w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.URem, w.B.Binary(IrBinaryOp.UDiv, absolute.Get(), w.Ix(p)), w.Ix(10)), IrType.I8), w.I8('0'))));
        }
        Emit(w.B.Add(w.B.Trunc(w.B.Binary(IrBinaryOp.URem, absolute.Get(), w.Ix(10)), IrType.I8), w.I8('0')));
      }, () => w.If(w.Cmp(IrCmpPred.Sge, exponent.Get(), w.Ix(0)), () => {
        // the integer part, padded with zeros past the significant digits, then any fraction
        var at = w.Variable(w.Index, w.Ix(0));
        w.While(() => w.Cmp(IrCmpPred.Sle, at.Get(), exponent.Get()), () => {
          Emit(w.B.Select(w.Cmp(IrCmpPred.Slt, at.Get(), significant.Get()), w.ByteAt(digitText, at.Get()), w.I8('0')));
          at.Set(w.B.Add(at.Get(), w.Ix(1)));
        });
        var fraction = w.B.Add(exponent.Get(), w.Ix(1));
        w.If(w.Cmp(IrCmpPred.Sgt, significant.Get(), fraction), () => {
          Emit(w.I8('.'));
          CopyDigits(w.B.Add(exponent.Get(), w.Ix(1)));
        });
      }, () => {
        // a pure fraction: no leading zero, the point, then zeros up to the first digit
        Emit(w.I8('.'));
        var zeros = w.Variable(w.Index, w.B.Sub(w.Ix(-1), exponent.Get()));
        w.While(() => w.Cmp(IrCmpPred.Sgt, zeros.Get(), w.Ix(0)), () => {
          Emit(w.I8('0'));
          zeros.Set(w.B.Sub(zeros.Get(), w.Ix(1)));
        });
        CopyDigits(w.Ix(0));
      }));
      w.B.Ret(length.Get());
    }

  }
}
