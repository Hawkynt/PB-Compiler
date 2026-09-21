using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// pb36's <c>INT128</c>/<c>INT256</c>/<c>INT512</c> and their unsigned spellings: fixed-size integers
/// emulated as a run of 16-bit words, low word first.
///
/// <para>
/// A wide integer is never a VALUE on either path. It has no IR type - <see cref="IrTypeMapper"/> maps
/// the scalars and nothing else - and the direct emitter does not give it one either: its storage is a
/// blob and every operation is word-by-word memory traffic, which is why <c>EmitWideStore</c> takes an
/// <see cref="AssignStmt"/> rather than producing a register. So the lowering matches, with a
/// <c>i16 x Words</c> alloca standing where a <c>UdtType</c>'s byte buffer would.
/// </para>
/// <para>
/// The one thing that does not carry over is the carry. The emitter walks the words with <c>ADC</c> and
/// <c>SBB</c>, reading a flag the IR has no way to name; here each word is added in THIRTY-TWO bits,
/// where the carry is simply bit 16 of the sum and the borrow is the sign of the difference. That is
/// more instructions and the same arithmetic - and the selector lowers a 32-bit add to the <c>ADD</c>
/// and <c>ADC</c> pair anyway, so most of the difference is paid back. Nothing asserts the byte shape:
/// wide integers are pb36-only, so no genuine compiler is an oracle for them and the golden gate has
/// nothing to say about the choice.
/// </para>
/// <para>
/// The supported set is exactly the emitter's, because that is what the binder admits: extend, copy,
/// truncate, and <c>a + b</c> / <c>a - b</c> between two equally wide operands. Multiply and compare
/// still diagnose at bind time rather than reaching either back end.
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

  /// <summary>Whether this assignment is one the wide-integer lowering owns.</summary>
  private bool IsWideAssignment(AssignStmt a, out WideIntType target)
    => (target = (this.TargetTypeOf(a.Target) as WideIntType)!) is not null;

  /// <summary>The declared type of an assignment target, or null when it is not a plain name.</summary>
  private PbType? TargetTypeOf(Expression target)
    => target is NameExpr && this._model.VariableBindings.TryGetValue(target, out var symbol) ? symbol.Type : null;

  /// <summary>
  /// <c>wide = …</c>, in the four forms the binder admits: a compile-time constant, a sum or difference
  /// of two wide values, another wide value, and a native integer.
  /// </summary>
  private void LowerWideAssign(AssignStmt a, WideIntType wt) {
    var destination = this.WideAddress(a.Target);

    // a compile-time integer constant writes its own words and a sign fill above them, which covers
    // any literal or equate up to 64 bits whatever type the binder gave the expression
    if (this._folder.TryFold(a.Value) is { Integer: { } constant }) {
      var fill = (short)(constant < 0 && wt.Signed ? -1 : 0);
      for (var k = 0; k < wt.Words; ++k) {
        var word = k < 4 ? (short)(ushort)(constant >> (16 * k)) : fill;
        this._b.Store(new IrConstantInt(IrType.I16, word), this.WideWordAddress(destination, k));
      }
      return;
    }

    if (this._model.TypeOf(a.Value) is WideIntType source) {
      if (a.Value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } sum
          && this._model.TypeOf(sum.Left) is WideIntType && this._model.TypeOf(sum.Right) is WideIntType) {
        this.LowerWideAddOrSubtract(wt, sum, destination);
        return;
      }
      if (a.Value is BinaryExpr or UnaryExpr)
        throw new IrLoweringException("this wide-integer operation");
      this.CopyWide(destination, this.WideAddress(a.Value), wt, source);
      return;
    }

    if (this._model.TypeOf(a.Value) is ScalarType { IsFloat: false, ByteSize: <= 4 } narrow) {
      this.ExtendIntoWide(destination, wt, narrow, this.LowerExpr(a.Value));
      return;
    }

    throw new IrLoweringException("wide-integer assignment from this value");
  }

  /// <summary>
  /// The carry chain. Each word is combined in thirty-two bits with the carry the word below produced,
  /// so bit 16 of a sum IS the carry out and the sign of a difference IS the borrow - no flag has to
  /// survive between two IR instructions, which is what an <c>ADC</c> chain would ask of one.
  /// </summary>
  private void LowerWideAddOrSubtract(WideIntType wt, BinaryExpr sum, IrValue destination) {
    var left = this.WideAddress(sum.Left);
    var right = this.WideAddress(sum.Right);
    var adding = sum.Op == BinaryOp.Add;
    IrValue carry = new IrConstantInt(IrType.I32, 0);

    for (var k = 0; k < wt.Words; ++k) {
      var l = this._b.ZExt(this._b.Load(IrType.I16, this.WideWordAddress(left, k)), IrType.I32);
      var r = this._b.ZExt(this._b.Load(IrType.I16, this.WideWordAddress(right, k)), IrType.I32);
      var combined = adding
        ? this._b.Add(this._b.Add(l, r), carry)
        : this._b.Sub(this._b.Sub(l, r), carry);
      this._b.Store(this._b.Trunc(combined, IrType.I16), this.WideWordAddress(destination, k));
      if (k + 1 == wt.Words)
        break;                                        // the top word's carry out has nowhere to go
      // adding: the carry is bit 16 of a sum of two 16-bit values and a carry, so it is 0 or 1 outright.
      // subtracting: the operands were zero-extended, so a borrow is exactly a negative difference, and
      // the sign bit of a 32-bit value shifted down to bit 0 is that 0 or 1.
      carry = this._b.Binary(IrBinaryOp.LShr, combined, new IrConstantInt(IrType.I32, adding ? 16 : 31));
      if (adding)
        carry = this._b.And(carry, new IrConstantInt(IrType.I32, 1));
    }
  }

  /// <summary>
  /// <c>wide = wide</c>: the words the two have in common, then a fill above them. A SIGNED source fills
  /// from the sign of its own top word; an unsigned one fills with zeros, and a narrower destination
  /// simply stops - a wide assignment truncates exactly as a narrow one does.
  /// </summary>
  private void CopyWide(IrValue destination, IrValue source, WideIntType wt, WideIntType sourceType) {
    var common = Math.Min(wt.Words, sourceType.Words);
    for (var k = 0; k < common; ++k)
      this._b.Store(this._b.Load(IrType.I16, this.WideWordAddress(source, k)), this.WideWordAddress(destination, k));
    if (wt.Words <= common)
      return;

    IrValue fill = new IrConstantInt(IrType.I16, 0);
    if (sourceType.Signed)
      // the sign of the source's top word, spread over a whole word: an arithmetic shift down by 15
      // leaves -1 for a negative value and 0 for the rest
      fill = this._b.Binary(IrBinaryOp.AShr,
        this._b.Load(IrType.I16, this.WideWordAddress(source, sourceType.Words - 1)),
        new IrConstantInt(IrType.I16, 15));
    for (var k = common; k < wt.Words; ++k)
      this._b.Store(fill, this.WideWordAddress(destination, k));
  }

  /// <summary>
  /// <c>wide = narrowExpr</c>: the native value's own words, then the extension. <c>WORD</c> and the
  /// other unsigned spellings fill with zeros where <c>INTEGER</c> and <c>LONG</c> fill with their sign,
  /// which is the whole difference between the two and is read off the SOURCE type rather than the
  /// destination's.
  /// </summary>
  private void ExtendIntoWide(IrValue destination, WideIntType wt, ScalarType narrow, IrValue value) {
    var words = narrow.ByteSize <= 2 ? 1 : 2;
    if (words == 1) {
      this._b.Store(this.NarrowToWord(value, narrow), this.WideWordAddress(destination, 0));
    } else {
      var asLong = value.Type.Bits == 32
        ? value
        : this._b.Cast(narrow.Signed ? IrCastOp.SExt : IrCastOp.ZExt, value, IrType.I32);
      this._b.Store(this._b.Trunc(asLong, IrType.I16), this.WideWordAddress(destination, 0));
      this._b.Store(this._b.Trunc(this._b.Binary(IrBinaryOp.LShr, asLong, new IrConstantInt(IrType.I32, 16)), IrType.I16),
        this.WideWordAddress(destination, 1));
    }
    if (wt.Words <= words)
      return;

    IrValue fill = new IrConstantInt(IrType.I16, 0);
    if (narrow.Signed)
      fill = this._b.Binary(IrBinaryOp.AShr,
        this._b.Load(IrType.I16, this.WideWordAddress(destination, words - 1)), new IrConstantInt(IrType.I16, 15));
    for (var k = words; k < wt.Words; ++k)
      this._b.Store(fill, this.WideWordAddress(destination, k));
  }

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
    var source = this.WideAddress(wideValue);
    var low = this._b.Load(IrType.I16, this.WideWordAddress(source, 0));
    if (narrow.ByteSize <= 2)
      return narrow.ByteSize == 1 ? this._b.Trunc(low, IrType.I8) : low;

    var high = this._b.Load(IrType.I16, this.WideWordAddress(source, 1));
    var combined = this._b.Or(
      this._b.ZExt(low, IrType.I32),
      this._b.Binary(IrBinaryOp.Shl, this._b.ZExt(high, IrType.I32), new IrConstantInt(IrType.I32, 16)));
    return narrow.ByteSize == 4 ? combined : this._b.SExt(combined, IrType.Integer(narrow.ByteSize * 8, narrow.Signed));
  }
}
