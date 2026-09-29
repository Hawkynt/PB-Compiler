namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Computes a near address's byte offset at the width of the pointer it is added to. An address is
/// taken modulo 2^pointer-bits, and add, subtract, multiply, shift left and the bitwise operations all
/// commute with that truncation, so a subscript the front end widened to 32 bits - <c>(sext i - 1) * 2</c>
/// - means exactly its low word. On a 16-bit target that is the difference between one SHL and a
/// register pair carried through SHL/RCL for a high half the address never reads.
///
/// <para>
/// The rewritten offset is built beside the address from the narrowed operands; the wide expression is
/// left for DCE, or for whatever else still reads it. A widening of a word is the word itself, a
/// constant is its low bits, and any other wide leaf is truncated where it is used. A tree that has
/// nothing but a leaf is left alone - truncating a lone value saves nothing.
/// </para>
/// </summary>
public static class AddressOffsetNarrowing {

  /// <summary>Narrows the byte offsets of <paramref name="function"/>'s near addresses; how many.</summary>
  public static int Run(IrFunction function, int pointerBits = 16) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.IsDeclaration || function.HasInlineAsm)
      return 0;
    var narrow = new IrType(IrTypeKind.Int, pointerBits);
    var made = 0;
    foreach (var address in function.AllInstructions.OfType<IrGep>().ToList()) {
      if (address.Parent is null || address.ElementType is not null || address.Type.IsFarPointer
          || address.ByteOffset is not IrBinary { Type.IsInteger: true } offset || offset.Type.Bits <= pointerBits)
        continue;
      var built = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
      address.SetOperand(1, Narrowed(offset, address, narrow, built));
      ++made;
    }
    return made;
  }

  private static IrValue Narrowed(IrValue value, IrInstruction before, IrType narrow, Dictionary<IrValue, IrValue> built) {
    if (built.TryGetValue(value, out var done))
      return done;
    var block = before.Parent!;
    IrValue result = value switch {
      IrConstantInt constant => new IrConstantInt(narrow, constant.Value),
      IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt } widening when widening.Value.Type.Bits == narrow.Bits => widening.Value,
      IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt } widening when widening.Value.Type is { IsInteger: true } source && source.Bits < narrow.Bits
        => block.InsertBefore(new IrCast(widening.Op, widening.Value, narrow), before),
      IrBinary { Op: IrBinaryOp.Shl, Rhs: IrConstantInt { Value: var count } } when count >= narrow.Bits
        => new IrConstantInt(narrow, 0),
      IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary
        => block.InsertBefore(new IrBinary(binary.Op, Narrowed(binary.Lhs, before, narrow, built),
             Narrowed(binary.Rhs, before, narrow, built)), before),
      IrBinary { Op: IrBinaryOp.Shl, Rhs: IrConstantInt { Value: >= 0 } count } shift
        => block.InsertBefore(new IrBinary(IrBinaryOp.Shl, Narrowed(shift.Lhs, before, narrow, built),
             new IrConstantInt(narrow, count.Value)), before),
      _ => block.InsertBefore(new IrCast(IrCastOp.Trunc, value, narrow), before),
    };
    built[value] = result;
    return result;
  }
}
