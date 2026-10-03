using PowerBasic.Compiler.Runtime.Portable;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The wide-integer operations too long to write out at every use: multiply, divide, compare, shift
/// and the decimal form of an <c>INT128</c> .. <c>UINT512</c>. Each is an ordinary IR function, defined
/// in the module the first time a program needs it, so every back end - x86-16, x86-32, x64, the
/// 6502, C and LLVM - compiles it exactly as it compiles the program's own procedures, and no runtime
/// has to carry it.
///
/// <para>
/// A value is a run of 16-bit words, low word first, as <see cref="IrLowering"/> lays it out; the
/// width is an argument, so one function serves all three. Every routine works through buffers of its
/// own, so a destination may be one of its operands. Arithmetic on words is done in 32 bits, where a
/// carry is bit 16 and a borrow the sign, which every target can do.
/// </para>
/// </summary>
internal static class WideIntegerHelpers {

  /// <summary>The widest value there is: <c>INT512</c>, 32 words.</summary>
  private const int MaxWords = 32;

  private static IrFunction Define(IrModule module, string name, IrType result, IrType[] parameters, Action<IrWriter, IReadOnlyList<IrArgument>> body) {
    if (module.FindFunction(name) is { } existing)
      return existing;
    // one body for every width and every call: inlined or specialized on a call's constant width, the
    // word loops would be unrolled into a copy per caller, and an INT256's sixteen-word loops nested in a
    // multiply unroll into more code than a 6502 has memory for
    var function = module.AddFunction(new IrFunction(name, result, parameters.Select((type, i) => new IrArgument(type, i))) { NoInline = true });
    function.MayGainCallers = true;
    var writer = new IrWriter(function, IrType.I16);
    body(writer, function.Parameters);
    return function;
  }

  // --- word access ----------------------------------------------------------------------------

  private static IrValue Word(IrWriter w, IrValue buffer, IrValue index) => w.B.Load(IrType.I16, w.B.Gep(buffer, index, IrType.I16));
  private static void SetWord(IrWriter w, IrValue buffer, IrValue index, IrValue value) => w.B.Store(value, w.B.Gep(buffer, index, IrType.I16));
  private static IrValue U32(IrWriter w, IrValue word) => w.B.ZExt(word, IrType.I32);
  private static IrValue Low(IrWriter w, IrValue u32) => w.B.Trunc(u32, IrType.I16);
  private static IrValue High(IrWriter w, IrValue u32) => w.B.Trunc(w.B.Binary(IrBinaryOp.LShr, u32, w.I32(16)), IrType.I16);
  private static IrValue W16(long value) => IrBuilder.ConstInt(IrType.I16, value);

  private static IrValue NewBuffer(IrWriter w, int count) => w.Buffer(count * 2);

  /// <summary>for (i = from; i &lt; to; ++i) body(i), over 16-bit counters.</summary>
  private static void For(IrWriter w, IrValue from, IrValue to, Action<IrValue> body) {
    var i = w.Variable(IrType.I16, from);
    w.While(() => w.Cmp(IrCmpPred.Slt, i.Get(), to), () => {
      body(i.Get());
      i.Set(w.B.Add(i.Get(), W16(1)));
    });
  }

  /// <summary>for (i = from; i &gt;= 0; --i) body(i).</summary>
  private static void Down(IrWriter w, IrValue from, Action<IrValue> body) {
    var i = w.Variable(IrType.I16, from);
    w.While(() => w.Cmp(IrCmpPred.Sge, i.Get(), W16(0)), () => {
      body(i.Get());
      i.Set(w.B.Sub(i.Get(), W16(1)));
    });
  }

  private static void Copy(IrWriter w, IrValue to, IrValue from, IrValue words)
    => For(w, W16(0), words, k => SetWord(w, to, k, Word(w, from, k)));

  private static void Clear(IrWriter w, IrValue buffer, IrValue words)
    => For(w, W16(0), words, k => SetWord(w, buffer, k, W16(0)));

  /// <summary>Whether the top bit of the value is set.</summary>
  private static IrValue Negative(IrWriter w, IrValue buffer, IrValue words)
    => w.Cmp(IrCmpPred.Slt, Word(w, buffer, w.B.Sub(words, W16(1))), W16(0));

  /// <summary>The two's complement of the value, in place.</summary>
  private static void Negate(IrWriter w, IrValue buffer, IrValue words) {
    var carry = w.Variable(IrType.I32, w.I32(1));
    For(w, W16(0), words, k => {
      var sum = w.B.Add(U32(w, w.B.Xor(Word(w, buffer, k), W16(-1))), carry.Get());
      SetWord(w, buffer, k, Low(w, sum));
      carry.Set(w.B.Binary(IrBinaryOp.LShr, sum, w.I32(16)));
    });
  }

  // --- the routines ---------------------------------------------------------------------------

  /// <summary><c>pb_wide_addsub(d, a, b, words, subtract)</c>: d = a + b, or a - b, carried word by word.</summary>
  public static IrFunction AddSub(IrModule module) => Define(module, "pb_wide_addsub", IrType.Void,
      [IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16], (w, p) => {
    var (d, a, b, words, subtract) = (p[0], p[1], p[2], p[3], p[4]);
    var t = NewBuffer(w, MaxWords);
    var subtracting = w.Cmp(IrCmpPred.Ne, subtract, W16(0));
    var carry = w.Variable(IrType.I32, w.I32(0));
    For(w, W16(0), words, k => {
      var x = U32(w, Word(w, a, k));
      var y = U32(w, Word(w, b, k));
      // a sum's carry is bit 16; a difference of zero-extended words borrows exactly when it is negative
      var sum = w.B.Add(w.B.Add(x, y), carry.Get());
      var difference = w.B.Sub(w.B.Sub(x, y), carry.Get());
      var combined = w.B.Select(subtracting, difference, sum);
      SetWord(w, t, k, Low(w, combined));
      carry.Set(w.B.Select(subtracting, w.B.Binary(IrBinaryOp.LShr, difference, w.I32(31)),
        w.B.Binary(IrBinaryOp.LShr, sum, w.I32(16))));
    });
    Copy(w, d, t, words);
    w.B.Ret();
  });

  /// <summary><c>pb_wide_bitop(d, a, b, words, op)</c>: op 0 is AND, 1 OR, 2 XOR, 3 NOT of a alone.</summary>
  public static IrFunction BitOp(IrModule module) => Define(module, "pb_wide_bitop", IrType.Void,
      [IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16], (w, p) => {
    var (d, a, b, words, op) = (p[0], p[1], p[2], p[3], p[4]);
    For(w, W16(0), words, k => {
      var x = Word(w, a, k);
      var y = Word(w, b, k);
      var result = w.B.Select(w.Cmp(IrCmpPred.Eq, op, W16(0)), w.B.And(x, y),
        w.B.Select(w.Cmp(IrCmpPred.Eq, op, W16(1)), w.B.Or(x, y),
          w.B.Select(w.Cmp(IrCmpPred.Eq, op, W16(2)), w.B.Xor(x, y), w.B.Xor(x, W16(-1)))));
      SetWord(w, d, k, result);
    });
    w.B.Ret();
  });

  /// <summary>
  /// <c>pb_wide_copy(d, a, to, from, signed)</c>: the words the two widths share, then the sign or zero
  /// fill above them - a wide assignment, which truncates and extends as a narrow one does.
  /// </summary>
  public static IrFunction CopyWords(IrModule module) => Define(module, "pb_wide_copy", IrType.Void,
      [IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16, IrType.I16], (w, p) => {
    var (d, a, to, from, signed) = (p[0], p[1], p[2], p[3], p[4]);
    var common = w.B.Select(w.Cmp(IrCmpPred.Slt, to, from), to, from);
    Copy(w, d, a, common);
    var fill = w.B.Select(w.B.And(w.Cmp(IrCmpPred.Ne, signed, W16(0)), Negative(w, a, from)), W16(-1), W16(0));
    For(w, common, to, k => SetWord(w, d, k, fill));
    w.B.Ret();
  });

  /// <summary><c>pb_wide_fill(d, from, to, word)</c>: the words from..to-1 set to one value - the sign fill of a constant or an extended native value.</summary>
  public static IrFunction Fill(IrModule module) => Define(module, "pb_wide_fill", IrType.Void,
      [IrType.Ptr, IrType.I16, IrType.I16, IrType.I16], (w, p) => {
    For(w, p[1], p[2], k => SetWord(w, p[0], k, p[3]));
    w.B.Ret();
  });

  /// <summary><c>pb_wide_neg(d, a, words)</c>: d = -a.</summary>
  public static IrFunction Neg(IrModule module) => Define(module, "pb_wide_neg", IrType.Void, [IrType.Ptr, IrType.Ptr, IrType.I16], (w, p) => {
    var t = NewBuffer(w, MaxWords);
    Copy(w, t, p[1], p[2]);
    Negate(w, t, p[2]);
    Copy(w, p[0], t, p[2]);
    w.B.Ret();
  });

  /// <summary><c>pb_wide_mul(d, a, b, words)</c>: d = a * b, kept to the width - the low half of the product, which is the same for signed and unsigned.</summary>
  public static IrFunction Mul(IrModule module) => Define(module, "pb_wide_mul", IrType.Void, [IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I16], (w, p) => {
    var (d, a, b, words) = (p[0], p[1], p[2], p[3]);
    var r = NewBuffer(w, MaxWords);
    Clear(w, r, words);
    For(w, W16(0), words, i => {
      var ai = U32(w, Word(w, a, i));
      w.If(w.Cmp(IrCmpPred.Ne, ai, w.I32(0)), () => {
        var carry = w.Variable(IrType.I32, w.I32(0));
        // a word product plus a word plus a carry stays below 2^32
        For(w, W16(0), w.B.Sub(words, i), j => {
          var at = w.B.Add(i, j);
          var t = w.B.Add(w.B.Add(w.B.Mul(ai, U32(w, Word(w, b, j))), U32(w, Word(w, r, at))), carry.Get());
          SetWord(w, r, at, Low(w, t));
          carry.Set(w.B.Binary(IrBinaryOp.LShr, t, w.I32(16)));
        });
      });
    });
    Copy(w, d, r, words);
    w.B.Ret();
  });

  /// <summary>
  /// <c>pb_wide_cmp(a, b, words, signed)</c>: -1, 0 or 1 as a is below, equal to or above b. The top
  /// words decide first, compared with sign when the values have one; every word below unsigned.
  /// </summary>
  public static IrFunction Cmp(IrModule module) => Define(module, "pb_wide_cmp", IrType.I16, [IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16], (w, p) => {
    var (a, b, words, signed) = (p[0], p[1], p[2], p[3]);
    var top = w.B.Sub(words, W16(1));
    var x = Word(w, a, top);
    var y = Word(w, b, top);
    w.If(w.Cmp(IrCmpPred.Ne, x, y), () => {
      var below = w.B.Select(w.Cmp(IrCmpPred.Ne, signed, W16(0)), w.Cmp(IrCmpPred.Slt, x, y), w.Cmp(IrCmpPred.Ult, x, y));
      w.Return(w.B.Select(below, W16(-1), W16(1)));
    });
    Down(w, w.B.Sub(top, W16(1)), k => {
      var u = Word(w, a, k);
      var v = Word(w, b, k);
      w.If(w.Cmp(IrCmpPred.Ne, u, v), () => w.Return(w.B.Select(w.Cmp(IrCmpPred.Ult, u, v), W16(-1), W16(1))));
    });
    w.B.Ret(W16(0));
  });

  /// <summary>
  /// <c>pb_wide_divmod(q, r, a, b, words, signed)</c>: the quotient truncated toward zero and the
  /// remainder with the dividend's sign, as BASIC's <c>\</c> and <c>MOD</c> give them; a zero divisor
  /// is error 11. Long division one bit at a time from the dividend's highest nonzero word.
  /// </summary>
  public static IrFunction DivMod(IrModule module) => Define(module, "pb_wide_divmod", IrType.Void,
      [IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16], (w, p) => {
    var (q, rem, a, b, words, signed) = (p[0], p[1], p[2], p[3], p[4], p[5]);
    var n = NewBuffer(w, MaxWords);
    var d = NewBuffer(w, MaxWords);
    var quotient = NewBuffer(w, MaxWords);
    var r = NewBuffer(w, MaxWords);
    Copy(w, n, a, words);
    Copy(w, d, b, words);
    var isSigned = w.Cmp(IrCmpPred.Ne, signed, W16(0));
    var negativeN = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
    var negativeD = w.Variable(IrType.I1, IrBuilder.ConstBool(false));
    w.If(isSigned, () => {
      negativeN.Set(Negative(w, n, words));
      negativeD.Set(Negative(w, d, words));
      w.If(negativeN.Get(), () => Negate(w, n, words));
      w.If(negativeD.Get(), () => Negate(w, d, words));
    });

    // a zero divisor is BASIC's error 11
    var any = w.Variable(IrType.I16, W16(0));
    For(w, W16(0), words, k => any.Set(w.B.Or(any.Get(), Word(w, d, k))));
    w.If(w.Cmp(IrCmpPred.Eq, any.Get(), W16(0)), () => {
      var error = module.FindFunction("rt_error") ?? module.AddFunction(new IrFunction("rt_error", IrType.Void, [new IrArgument(IrType.I32, 0)]));
      w.B.Call(IrType.Void, error, w.I32(11));
      w.B.Unreachable();
      w.B.Position(w.Block("after"));
    });

    Clear(w, quotient, words);
    Clear(w, r, words);
    // the dividend's highest nonzero word: the bits above it would only shift zeros into r
    var top = w.Variable(IrType.I16, w.B.Sub(words, W16(1)));
    w.While(() => w.B.And(w.Cmp(IrCmpPred.Sgt, top.Get(), W16(0)), w.Cmp(IrCmpPred.Eq, Word(w, n, top.Get()), W16(0))),
      () => top.Set(w.B.Sub(top.Get(), W16(1))));

    Down(w, w.B.Add(w.B.Mul(top.Get(), W16(16)), W16(15)), bit => {
      var wordIndex = w.B.Binary(IrBinaryOp.LShr, bit, W16(4));
      var shift = w.B.And(bit, W16(15));
      var incoming = w.B.And(w.B.Binary(IrBinaryOp.LShr, Word(w, n, wordIndex), shift), W16(1));
      // r = r << 1 | the dividend's bit
      var carry = w.Variable(IrType.I16, incoming);
      For(w, W16(0), words, k => {
        var word = Word(w, r, k);
        SetWord(w, r, k, w.B.Or(w.B.Shl(word, W16(1)), carry.Get()));
        carry.Set(w.B.Binary(IrBinaryOp.LShr, word, W16(15)));
      });
      // r >= d, compared from the top word down
      var order = w.Variable(IrType.I16, W16(0));
      Down(w, w.B.Sub(words, W16(1)), k => {
        w.If(w.Cmp(IrCmpPred.Eq, order.Get(), W16(0)), () => {
          var u = Word(w, r, k);
          var v = Word(w, d, k);
          order.Set(w.B.Select(w.Cmp(IrCmpPred.Ult, u, v), W16(-1), w.B.Select(w.Cmp(IrCmpPred.Ugt, u, v), W16(1), W16(0))));
        });
      });
      w.If(w.Cmp(IrCmpPred.Sge, order.Get(), W16(0)), () => {
        var borrow = w.Variable(IrType.I32, w.I32(0));
        For(w, W16(0), words, k => {
          var difference = w.B.Sub(w.B.Sub(U32(w, Word(w, r, k)), U32(w, Word(w, d, k))), borrow.Get());
          SetWord(w, r, k, Low(w, difference));
          borrow.Set(w.B.Binary(IrBinaryOp.LShr, difference, w.I32(31)));
        });
        SetWord(w, quotient, wordIndex, w.B.Or(Word(w, quotient, wordIndex), w.B.Shl(W16(1), shift)));
      });
    });

    w.If(w.B.Xor(negativeN.Get(), negativeD.Get()), () => Negate(w, quotient, words));
    w.If(negativeN.Get(), () => Negate(w, r, words));
    Copy(w, q, quotient, words);
    Copy(w, rem, r, words);
    w.B.Ret();
  });

  /// <summary>
  /// <c>pb_wide_shift(d, a, count, words, kind)</c>: kind 0 shifts left, 1 right with the sign, 2 right
  /// with zeros. A count at or past the width leaves only the fill.
  /// </summary>
  public static IrFunction Shift(IrModule module) => Define(module, "pb_wide_shift", IrType.Void,
      [IrType.Ptr, IrType.Ptr, IrType.I16, IrType.I16, IrType.I16], (w, p) => {
    var (d, a, count, words, kind) = (p[0], p[1], p[2], p[3], p[4]);
    var t = NewBuffer(w, MaxWords);
    var fill = w.B.Select(w.B.And(w.Cmp(IrCmpPred.Eq, kind, W16(1)), Negative(w, a, words)), W16(-1), W16(0));
    var bits = w.B.Mul(words, W16(16));
    var amount = w.B.Select(w.Cmp(IrCmpPred.Ugt, count, bits), bits, count);
    var wordShift = w.B.Binary(IrBinaryOp.LShr, amount, W16(4));
    var bitShift = U32(w, w.B.And(amount, W16(15)));
    // the source word at k - offset, or the fill outside the value
    IrValue SourceWord(IrValue index) {
      var inside = w.B.And(w.Cmp(IrCmpPred.Sge, index, W16(0)), w.Cmp(IrCmpPred.Slt, index, words));
      var safe = w.B.Select(inside, index, W16(0));
      return w.B.Select(inside, Word(w, a, safe), fill);
    }
    For(w, W16(0), words, k => {
      w.If(w.Cmp(IrCmpPred.Eq, kind, W16(0)), () => {
        // left: the word wordShift below, with the high bits of the one below that
        var near = SourceWord(w.B.Sub(k, wordShift));
        var far = SourceWord(w.B.Sub(w.B.Sub(k, wordShift), W16(1)));
        var pair = w.B.Or(w.B.Shl(U32(w, near), w.I32(16)), U32(w, far));
        SetWord(w, t, k, High(w, w.B.Shl(pair, bitShift)));
      }, () => {
        var near = SourceWord(w.B.Add(k, wordShift));
        var far = SourceWord(w.B.Add(w.B.Add(k, wordShift), W16(1)));
        var pair = w.B.Or(w.B.Shl(U32(w, far), w.I32(16)), U32(w, near));
        SetWord(w, t, k, Low(w, w.B.Binary(IrBinaryOp.LShr, pair, bitShift)));
      });
    });
    Copy(w, d, t, words);
    w.B.Ret();
  });

  /// <summary>
  /// <c>pb_wide_str(a, words, signed)</c>: the value as <c>STR$</c> writes a number - a minus sign or a
  /// space, then the digits. The digits come four at a time, dividing by 10000 from the top word down.
  /// </summary>
  public static IrFunction Str(IrModule module) => Define(module, "pb_wide_str", IrType.Ptr, [IrType.Ptr, IrType.I16, IrType.I16], (w, p) => {
    var (a, words, signed) = (p[0], p[1], p[2]);
    var t = NewBuffer(w, MaxWords);
    Copy(w, t, a, words);
    var negative = w.B.And(w.Cmp(IrCmpPred.Ne, signed, W16(0)), Negative(w, t, words));
    w.If(negative, () => Negate(w, t, words));
    // 155 digits are enough for 2^512, and one more for the sign
    const int digits = 160;
    var text = w.Buffer(digits);
    var at = w.Variable(IrType.I16, W16(digits));
    var more = w.Variable(IrType.I1, IrBuilder.ConstBool(true));
    w.While(() => more.Get(), () => {
      var remainder = w.Variable(IrType.I32, w.I32(0));
      Down(w, w.B.Sub(words, W16(1)), k => {
        var current = w.B.Or(w.B.Shl(remainder.Get(), w.I32(16)), U32(w, Word(w, t, k)));
        var quotient = w.B.Binary(IrBinaryOp.UDiv, current, w.I32(10000));
        SetWord(w, t, k, Low(w, quotient));
        // the remainder from the quotient: one divide where a URem would ask for a second
        remainder.Set(w.B.Sub(current, w.B.Mul(quotient, w.I32(10000))));
      });
      var any = w.Variable(IrType.I16, W16(0));
      For(w, W16(0), words, k => any.Set(w.B.Or(any.Get(), Word(w, t, k))));
      more.Set(w.Cmp(IrCmpPred.Ne, any.Get(), W16(0)));
      // four digits, or as many as the last group has when nothing is left above it
      var group = w.Variable(IrType.I32, remainder.Get());
      var written = w.Variable(IrType.I16, W16(0));
      w.While(() => w.B.Or(w.Cmp(IrCmpPred.Ne, group.Get(), w.I32(0)),
          w.B.And(more.Get(), w.Cmp(IrCmpPred.Slt, written.Get(), W16(4)))), () => {
        at.Set(w.B.Sub(at.Get(), W16(1)));
        var tenth = w.B.Binary(IrBinaryOp.UDiv, group.Get(), w.I32(10));
        w.SetByte(text, at.Get(), w.B.Trunc(w.B.Add(w.B.Sub(group.Get(), w.B.Mul(tenth, w.I32(10))), w.I32('0')), IrType.I8));
        group.Set(tenth);
        written.Set(w.B.Add(written.Get(), W16(1)));
      });
    });
    // zero has no digit yet
    w.If(w.Cmp(IrCmpPred.Eq, at.Get(), W16(digits)), () => {
      at.Set(w.B.Sub(at.Get(), W16(1)));
      w.SetByte(text, at.Get(), w.I8('0'));
    });
    at.Set(w.B.Sub(at.Get(), W16(1)));
    w.SetByte(text, at.Get(), w.B.Select(negative, w.I8('-'), w.I8(' ')));
    var fromFixed = module.FindFunction("rt_str_from_fixed")
      ?? module.AddFunction(new IrFunction("rt_str_from_fixed", IrType.Ptr, [new IrArgument(IrType.Ptr, 0), new IrArgument(IrType.I32, 1)]));
    var length = w.B.SExt(w.B.Sub(W16(digits), at.Get()), IrType.I32);
    w.B.Ret(w.B.Call(IrType.Ptr, fromFixed, w.B.Gep(text, at.Get()), length));
  });
}
