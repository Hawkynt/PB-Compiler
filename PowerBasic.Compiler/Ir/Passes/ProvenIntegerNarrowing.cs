using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// 32-bit operations whose operands the range analysis PROVES fit sixteen bits, done in sixteen bits -
/// the direct emitter's O16 narrowing of LONG arithmetic, on SSA. On a 16-bit target a LONG divide is
/// a runtime call and a LONG compare a nine-instruction sequence over two register pairs; when both
/// operands are known to lie in the INTEGER range, one IDIV or one CMP answers the same question.
///
/// <para>
/// What is narrowed, and why each is exact:
/// </para>
/// <list type="bullet">
/// <item>a signed divide or remainder of two values in [-32768, 32767] - except when the dividend may
/// be -32768 while the divisor may be -1, the one quotient that does not fit.</item>
/// <item>an unsigned divide or remainder of two values in [0, 65535].</item>
/// <item>in both, only where the divisor's range excludes zero. The lowering guards a zero divisor with
/// an Error 11 branch, but under ON ERROR RESUME that raise returns and the divide still runs - and a
/// 16-bit IDIV by zero is a processor fault, where the 32-bit routine was not.</item>
/// <item>a compare of two values in [-32768, 32767], for every predicate: sign extension preserves
/// both the signed and the unsigned order of such values.</item>
/// </list>
/// <para>
/// The result is widened back where it was a 32-bit value, so every user is untouched. A MULTIPLY keeps
/// its 32-bit result - the product of two 16-bit values needs all of it - but its operands are
/// restated as widened words, which is the shape the x86-16 selector computes with one IMUL or MUL
/// into DX:AX instead of the three of rt_lmul. The product of two 16-bit values always fits 32 bits,
/// so the low 32 bits the IR's multiply keeps are the whole product.
/// </para>
/// <para>
/// It runs in the native pipeline of a 16-bit target only. On a 32-bit target the narrow form costs a
/// partial register and saves nothing, and the C, LLVM and BASIC writers render the IR rather than
/// select it, so they gain nothing from a narrower spelling of the same value.
/// </para>
/// </summary>
public static class ProvenIntegerNarrowing {

  /// <summary>Narrows what the ranges prove in <paramref name="function"/>; returns how many.</summary>
  public static int Run(IrFunction function, IrRangeAnalysis? ranges) {
    ArgumentNullException.ThrowIfNull(function);
    if (ranges is null || function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
      return 0;
    var made = 0;
    foreach (var instruction in function.AllInstructions.ToList()) {
      if (instruction.Parent is not { } block)
        continue;
      ValueRange Range(IrValue value) => ranges.RangeAt(value, block);
      switch (instruction) {
        case IrBinary { Type: { IsInteger: true, Bits: 32 }, Op: IrBinaryOp.SDiv or IrBinaryOp.SRem } signedDivide
            when FitsSigned(Range(signedDivide.Lhs)) && FitsSigned(Range(signedDivide.Rhs))
              && !Range(signedDivide.Rhs).Contains(0)
              && !(Range(signedDivide.Lhs).Contains(short.MinValue) && Range(signedDivide.Rhs).Contains(-1)):
          Replace(signedDivide, IrCastOp.SExt);
          ++made;
          break;
        case IrBinary { Type: { IsInteger: true, Bits: 32 }, Op: IrBinaryOp.UDiv or IrBinaryOp.URem } unsignedDivide
            when FitsUnsigned(Range(unsignedDivide.Lhs)) && FitsUnsigned(Range(unsignedDivide.Rhs))
              && !Range(unsignedDivide.Rhs).Contains(0):
          Replace(unsignedDivide, IrCastOp.ZExt);
          ++made;
          break;
        case IrBinary { Type: { IsInteger: true, Bits: 32 }, Op: IrBinaryOp.Mul } multiply
            when !IsWordFactor(multiply.Lhs) || !IsWordFactor(multiply.Rhs): {
          var widen = FitsSigned(Range(multiply.Lhs)) && FitsSigned(Range(multiply.Rhs)) ? IrCastOp.SExt
            : FitsUnsigned(Range(multiply.Lhs)) && FitsUnsigned(Range(multiply.Rhs)) ? IrCastOp.ZExt
            : (IrCastOp?)null;
          if (widen is not { } extension)
            break;
          multiply.SetOperand(0, AsWidenedWord(multiply, multiply.Lhs, extension));
          multiply.SetOperand(1, AsWidenedWord(multiply, multiply.Rhs, extension));
          ++made;
          break;
        }
        case IrCmp { Lhs.Type: { IsInteger: true, Bits: 32 } } compare
            when FitsSigned(Range(compare.Lhs)) && FitsSigned(Range(compare.Rhs)): {
          var left = block.InsertBefore(new IrCast(IrCastOp.Trunc, compare.Lhs, IrType.I16), compare);
          var right = block.InsertBefore(new IrCast(IrCastOp.Trunc, compare.Rhs, IrType.I16), compare);
          var narrow = block.InsertBefore(new IrCmp(compare.Pred, left, right) { IsSourceCondition = compare.IsSourceCondition }, compare);
          compare.ReplaceAllUsesWith(narrow);
          compare.EraseFromParent();
          ++made;
          break;
        }
      }
    }
    return made;
  }

  /// <summary>A word widened to 32 bits, the operand shape the x86-16 selector multiplies with one instruction.</summary>
  private static bool IsWidenedWord(IrValue value)
    => value is IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt, Value.Type: { IsInteger: true, Bits: 16 } };

  /// <summary>
  /// A factor already in a shape the selector takes as a word: a widened word, or a constant it can
  /// use directly. A CONSTANT is never rewritten - other patterns (the reciprocal divide above all)
  /// recognise it by being a constant, and hiding it behind a cast breaks them.
  /// </summary>
  private static bool IsWordFactor(IrValue value) => IsWidenedWord(value) || value is IrConstantInt;

  /// <summary><paramref name="value"/> as its low word widened back - the same number, given its range.</summary>
  private static IrValue AsWidenedWord(IrInstruction at, IrValue value, IrCastOp widen) {
    if (IsWordFactor(value))
      return value;
    var block = at.Parent!;
    var word = block.InsertBefore(new IrCast(IrCastOp.Trunc, value, IrType.I16), at);
    return block.InsertBefore(new IrCast(widen, word, value.Type), at);
  }

  private static bool FitsSigned(ValueRange range) => !range.IsEmpty && range.Lo >= short.MinValue && range.Hi <= short.MaxValue;

  private static bool FitsUnsigned(ValueRange range) => !range.IsEmpty && range.Lo >= 0 && range.Hi <= ushort.MaxValue;

  /// <summary>The same operation on the low words, widened back the way the operands were.</summary>
  private static void Replace(IrBinary wide, IrCastOp widen) {
    var block = wide.Parent!;
    var left = block.InsertBefore(new IrCast(IrCastOp.Trunc, wide.Lhs, IrType.I16), wide);
    var right = block.InsertBefore(new IrCast(IrCastOp.Trunc, wide.Rhs, IrType.I16), wide);
    var narrow = block.InsertBefore(new IrBinary(wide.Op, left, right), wide);
    var widened = block.InsertBefore(new IrCast(widen, narrow, wide.Type), wide);
    wide.ReplaceAllUsesWith(widened);
    wide.EraseFromParent();
  }
}
