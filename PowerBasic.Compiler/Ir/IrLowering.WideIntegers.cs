using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// pb36's <c>INT128</c>/<c>INT256</c>/<c>INT512</c> and their unsigned spellings: fixed-size integers
/// emulated as a run of 16-bit words, low word first.
///
/// <para>
/// A wide integer is never a VALUE. It has no IR type - <see cref="IrTypeMapper"/> maps the scalars and
/// nothing else - so its storage is an <c>i16 x Words</c> alloca, standing where a <c>UdtType</c>'s
/// byte buffer would, and an expression is computed into words in memory: each operand into a frame
/// cell of the operation's type, the operation into the destination (or into a cell of its own type,
/// then fitted to the destination as an assignment fits it).
/// </para>
/// <para>
/// Every operation is a call to a loop in <see cref="WideIntegerHelpers"/>: add and subtract with the
/// carry taken in 32 bits, the bitwise operators, multiply, divide with remainder, compare, shift, copy
/// with extension and the decimal form. They are ordinary IR functions defined in the module, so x86-16,
/// x86-32, x64, the 6502, C and LLVM compile them like the program's own procedures and no runtime
/// carries them; one loop per operation, rather than one store per word, is what keeps a program that
/// uses an INT512 inside a 6502's memory. Wide integers are pb36-only, so no genuine compiler is an
/// oracle for them and the golden gate has nothing to say about the shape; <c>BigInteger</c> is the
/// oracle the tests hold every target to.
/// </para>
/// </summary>
public sealed partial class IrLowering {

  /// <summary>The address of word <paramref name="index"/> - the words run low to high from the base.</summary>
  private IrValue WideWordAddress(IrValue basePtr, int index) => this.OffsetWithin(basePtr, index * 2);

  /// <summary>
  /// Where a wide-typed expression's bytes live. Only a NAME has an answer: the emitter asks
  /// <c>EmitPlace</c>, which spans more shapes, but the binder produces a wide-typed expression in no
  /// other position yet - and declining is what keeps a construct nobody lowered from miscompiling.
  /// </summary>
  private IrValue WideAddress(Expression expression) {
    if (expression is NameExpr && this._model.VariableBindings.TryGetValue(expression, out var symbol)
        && symbol.Type is WideIntType)
      return this.SlotFor(symbol);
    throw new IrLoweringException("wide-integer value without a memory location");
  }

  /// <summary>A wide expression computed into a cell of its own type, for a reader that needs its words.</summary>
  private IrValue WideValue(Expression expression) {
    var type = (WideIntType)this._model.TypeOf(expression);
    var cell = this.WideTemp(type);
    this.LowerWideInto(expression, cell, type);
    return cell;
  }

  /// <summary>Whether this assignment is one the wide-integer lowering owns.</summary>
  private bool IsWideAssignment(AssignStmt a, out WideIntType target)
    => (target = (this.TargetTypeOf(a.Target) as WideIntType)!) is not null;

  /// <summary>The declared type of an assignment target, or null when it is not a plain name.</summary>
  private PbType? TargetTypeOf(Expression target)
    => target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var symbol) ? symbol.Type : null;

  /// <summary><c>wide = …</c>: the value computed into the target's words.</summary>
  private void LowerWideAssign(AssignStmt a, WideIntType wt) => this.LowerWideInto(a.Value, this.WideAddress(a.Target), wt);

  /// <summary>A frame cell for an intermediate wide value.</summary>
  private IrValue WideTemp(WideIntType wt)
    => this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I16) { Count = wt.Words, Name = "wide.t" });

  /// <summary>
  /// Any integer expression into the words at <paramref name="destination"/>, as a
  /// <paramref name="wt"/>: a constant writes its words, a native integer is extended, a wide variable
  /// copied, and an operation computed at its own type and then fitted the way an assignment fits it.
  /// </summary>
  private void LowerWideInto(Expression expression, IrValue destination, WideIntType wt) {
    // a compile-time integer constant writes its own words and a sign fill above them, which covers
    // any literal or equate up to 64 bits whatever type the binder gave the expression
    if (this._folder.TryFold(expression) is { Integer: { } constant }) {
      var fill = (short)(constant < 0 && wt.Signed ? -1 : 0);
      var written = Math.Min(4, wt.Words);
      for (var k = 0; k < written; ++k) {
        var word = k < 4 ? (short)(ushort)(constant >> (16 * k)) : fill;
        this._b.Store(new IrConstantInt(IrType.I16, word), this.WideWordAddress(destination, k));
      }
      if (written < wt.Words)
        this.FillWords(destination, written, wt.Words, new IrConstantInt(IrType.I16, fill));
      return;
    }

    switch (this._model.TypeOf(expression)) {
      case ScalarType { IsFloat: false, ByteSize: <= 8 } narrow:
        this.ExtendIntoWide(destination, wt, narrow, this.LowerExpr(expression));
        return;
      case WideIntType source when expression is NameExpr:
        this.CopyWide(destination, this.WideAddress(expression), wt, source);
        return;
      case WideIntType source: {
        var result = source == wt ? destination : this.WideTemp(source);
        this.ComputeWide(expression, result, source);
        if (!ReferenceEquals(result, destination))
          this.CopyWide(destination, result, wt, source);
        return;
      }
      default:
        throw new IrLoweringException("wide-integer assignment from this value");
    }
  }

  /// <summary>An operation whose type is <paramref name="type"/>, computed into <paramref name="result"/>.</summary>
  private void ComputeWide(Expression expression, IrValue result, WideIntType type) {
    var words = new IrConstantInt(IrType.I16, type.Words);
    switch (expression) {
      case UnaryExpr { Op: UnaryOp.Negate } negation:
        this.LowerWideInto(negation.Operand, result, type);
        this._b.Call(IrType.Void, WideIntegerHelpers.Neg(this._module!), result, result, words);
        return;
      case UnaryExpr { Op: UnaryOp.Not } inversion:
        this.LowerWideInto(inversion.Operand, result, type);
        this._b.Call(IrType.Void, WideIntegerHelpers.BitOp(this._module!), result, result, result, words, new IrConstantInt(IrType.I16, 3));
        return;
      case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical } shift: {
        var value = this.WideTemp(type);
        this.LowerWideInto(shift.Left, value, type);
        var count = this.Coerce(this.LowerExpr(shift.Right), this._model.TypeOf(shift.Right), PbType.Integer);
        // >> fills with the sign, which an unsigned value does not have
        var kind = shift.Op switch { BinaryOp.ShiftLeft => 0, BinaryOp.ShiftRightArith when type.Signed => 1, _ => 2 };
        this._b.Call(IrType.Void, WideIntegerHelpers.Shift(this._module!), result, value, count, words, new IrConstantInt(IrType.I16, kind));
        return;
      }
      case BinaryExpr binary: {
        var left = this.WideTemp(type);
        var right = this.WideTemp(type);
        this.LowerWideInto(binary.Left, left, type);
        this.LowerWideInto(binary.Right, right, type);
        switch (binary.Op) {
          case BinaryOp.Add or BinaryOp.Subtract:
            this._b.Call(IrType.Void, WideIntegerHelpers.AddSub(this._module!), result, left, right, words,
              new IrConstantInt(IrType.I16, binary.Op == BinaryOp.Subtract ? 1 : 0));
            return;
          case BinaryOp.And or BinaryOp.Or or BinaryOp.Xor:
            this._b.Call(IrType.Void, WideIntegerHelpers.BitOp(this._module!), result, left, right, words,
              new IrConstantInt(IrType.I16, binary.Op switch { BinaryOp.And => 0, BinaryOp.Or => 1, _ => 2 }));
            return;
          case BinaryOp.Multiply:
            this._b.Call(IrType.Void, WideIntegerHelpers.Mul(this._module!), result, left, right, words);
            return;
          case BinaryOp.IntegerDivide or BinaryOp.Modulo: {
            var other = this.WideTemp(type);
            var (quotient, remainder) = binary.Op == BinaryOp.IntegerDivide ? (result, other) : (other, result);
            this._b.Call(IrType.Void, WideIntegerHelpers.DivMod(this._module!), quotient, remainder, left, right, words,
              new IrConstantInt(IrType.I16, type.Signed ? 1 : 0));
            return;
          }
        }
        break;
      }
    }
    throw new IrLoweringException("this wide-integer expression");
  }

  /// <summary>
  /// A comparison with a wide integer on either side: both computed at the wider type, ordered by
  /// <c>pb_wide_cmp</c>, and answered as BASIC's -1 or 0.
  /// </summary>
  private IrValue LowerWideComparison(BinaryExpr expr, PbType resultPb) {
    var (leftPb, rightPb) = (this._model.TypeOf(expr.Left), this._model.TypeOf(expr.Right));
    var type = (leftPb, rightPb) switch {
      (WideIntType l, WideIntType r) => r.ByteSize > l.ByteSize ? r : l,
      (WideIntType l, _) => l,
      (_, WideIntType r) => r,
      _ => throw new IrLoweringException("a wide comparison without a wide operand"),
    };
    var left = this.WideTemp(type);
    var right = this.WideTemp(type);
    this.LowerWideInto(expr.Left, left, type);
    this.LowerWideInto(expr.Right, right, type);
    var order = this._b.Call(IrType.I16, WideIntegerHelpers.Cmp(this._module!), left, right,
      new IrConstantInt(IrType.I16, type.Words), new IrConstantInt(IrType.I16, type.Signed ? 1 : 0));
    var pred = expr.Op switch {
      BinaryOp.Equal => IrCmpPred.Eq,
      BinaryOp.NotEqual => IrCmpPred.Ne,
      BinaryOp.Less => IrCmpPred.Slt,
      BinaryOp.LessEqual => IrCmpPred.Sle,
      BinaryOp.Greater => IrCmpPred.Sgt,
      _ => IrCmpPred.Sge,
    };
    return this._b.SExt(this._b.Cmp(pred, order, new IrConstantInt(IrType.I16, 0)), MapType(resultPb));
  }

  /// <summary><c>STR$</c> of a wide value, and what <c>PRINT</c> writes before its trailing space.</summary>
  private IrValue LowerWideStr(Expression expression, WideIntType type) {
    var value = this.WideTemp(type);
    this.LowerWideInto(expression, value, type);
    return this._b.Call(IrType.Ptr, WideIntegerHelpers.Str(this._module!), value,
      new IrConstantInt(IrType.I16, type.Words), new IrConstantInt(IrType.I16, type.Signed ? 1 : 0));
  }

  /// <summary>
  /// <c>wide = wide</c>: the words the two have in common, then a fill above them. A SIGNED source fills
  /// from the sign of its own top word; an unsigned one fills with zeros, and a narrower destination
  /// simply stops - a wide assignment truncates exactly as a narrow one does.
  /// </summary>
  private void CopyWide(IrValue destination, IrValue source, WideIntType wt, WideIntType sourceType)
    => this._b.Call(IrType.Void, WideIntegerHelpers.CopyWords(this._module!), destination, source,
      new IrConstantInt(IrType.I16, wt.Words), new IrConstantInt(IrType.I16, sourceType.Words),
      new IrConstantInt(IrType.I16, sourceType.Signed ? 1 : 0));

  /// <summary>
  /// <c>wide = narrowExpr</c>: the native value's own words, then the extension. <c>WORD</c> and the
  /// other unsigned spellings fill with zeros where <c>INTEGER</c>, <c>LONG</c> and <c>QUAD</c> fill
  /// with their sign, which is the whole difference between the two and is read off the SOURCE type
  /// rather than the destination's. A <c>QUAD</c> brings four words.
  /// </summary>
  private void ExtendIntoWide(IrValue destination, WideIntType wt, ScalarType narrow, IrValue value) {
    var words = narrow.ByteSize switch { <= 2 => 1, <= 4 => 2, _ => 4 };
    if (words == 1) {
      this._b.Store(this.NarrowToWord(value, narrow), this.WideWordAddress(destination, 0));
    } else if (words == 2) {
      var asLong = value.Type.Bits == 32
        ? value
        : this._b.Cast(narrow.Signed ? IrCastOp.SExt : IrCastOp.ZExt, value, IrType.I32);
      this._b.Store(this._b.Trunc(asLong, IrType.I16), this.WideWordAddress(destination, 0));
      this._b.Store(this._b.Trunc(this._b.Binary(IrBinaryOp.LShr, asLong, new IrConstantInt(IrType.I32, 16)), IrType.I16),
        this.WideWordAddress(destination, 1));
    } else {
      // a QUAD's words are moved through memory: a 64-bit value is a cell on a 16-bit target, not
      // something it can shift, and the four words are where the cell already keeps them
      var cell = this.QuadCell();
      this._b.Store(value, cell);
      for (var k = 0; k < words && k < wt.Words; ++k)
        this._b.Store(this._b.Load(IrType.I16, this.OffsetWithin(cell, k * 2)), this.WideWordAddress(destination, k));
    }
    if (wt.Words <= words)
      return;

    IrValue fill = new IrConstantInt(IrType.I16, 0);
    if (narrow.Signed)
      fill = this._b.Binary(IrBinaryOp.AShr,
        this._b.Load(IrType.I16, this.WideWordAddress(destination, words - 1)), new IrConstantInt(IrType.I16, 15));
    this.FillWords(destination, words, wt.Words, fill);
  }

  /// <summary>
  /// The words from..to-1 set to <paramref name="fill"/>. This and every other run over the words is a
  /// loop in <see cref="WideIntegerHelpers"/> rather than one store per word, which keeps a program
  /// that uses an INT512 small enough for a machine with 64 KB.
  /// </summary>
  private void FillWords(IrValue destination, int from, int to, IrValue fill)
    => this._b.Call(IrType.Void, WideIntegerHelpers.Fill(this._module!), destination,
      new IrConstantInt(IrType.I16, from), new IrConstantInt(IrType.I16, to), fill);

  /// <summary>An eight-byte frame cell a QUAD's words pass through.</summary>
  private IrAlloca QuadCell()
    => this._entry.InsertAt(this._entryAllocaCount++, new IrAlloca(IrType.I64) { Name = "wide.quad" });

  /// <summary>The low word of a native value, whatever width the expression arrived in.</summary>
  private IrValue NarrowToWord(IrValue value, ScalarType narrow) {
    if (value.Type.Bits == 16)
      return value;
    return value.Type.Bits > 16
      ? this._b.Trunc(value, IrType.I16)
      : this._b.Cast(narrow.Signed ? IrCastOp.SExt : IrCastOp.ZExt, value, IrType.I16);
  }

  /// <summary>
  /// <c>narrow = wide</c>: the low word or words, and nothing above them. The destination's own type
  /// decides how much is kept, so a <c>LONG</c> takes two words and an <c>INTEGER</c> one - which is
  /// truncation and never a range check, exactly as the emitter has it.
  /// </summary>
  private IrValue LowerWideTruncation(Expression wideValue, ScalarType narrow) {
    var source = wideValue is NameExpr ? this.WideAddress(wideValue) : this.WideValue(wideValue);
    var low = this._b.Load(IrType.I16, this.WideWordAddress(source, 0));
    if (narrow.ByteSize <= 2)
      return narrow.ByteSize == 1 ? this._b.Trunc(low, IrType.I8) : low;

    if (narrow.ByteSize == 8) {
      // a QUAD takes four words, assembled in a cell for the reason ExtendIntoWide gives
      var cell = this.QuadCell();
      for (var k = 0; k < 4; ++k)
        this._b.Store(this._b.Load(IrType.I16, this.WideWordAddress(source, k)), this.OffsetWithin(cell, k * 2));
      return this._b.Load(IrType.Integer(64, narrow.Signed), cell);
    }

    var high = this._b.Load(IrType.I16, this.WideWordAddress(source, 1));
    return this._b.Or(
      this._b.ZExt(low, IrType.I32),
      this._b.Binary(IrBinaryOp.Shl, this._b.ZExt(high, IrType.I32), new IrConstantInt(IrType.I32, 16)));
  }
}
