using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// Steps an array element's address instead of rebuilding it from the counter - the address half of
/// induction-variable strength reduction. In a loop whose counter <c>i</c> advances by a constant,
/// <c>gep base, scale*i + offset</c> with a loop-invariant base advances by <c>scale*step</c>, so it
/// becomes a pointer carried through a header phi and moved by one add in the latch. What a person
/// writing <c>s = s + a(i)</c> by hand would produce: <c>ADD DI,[BX]</c> / <c>ADD BX,2</c>, with no
/// subscript arithmetic left in the loop.
///
/// <para>
/// It needs no trip count and no proof that anything does not overflow, because it works modulo the
/// pointer width - which is exactly how the machine forms an address. <see cref="AddressOffsetNarrowing"/>
/// runs first and restates every offset at that width, and there an affine function of the counter is
/// affine whatever the counter does: <c>a(i)</c> and the unrolled <c>a(i + 3)</c> alike.
/// </para>
/// <para>
/// The offset may also hold values defined outside the loop - a row's start in a two-dimensional
/// subscript, or the counter's own first value when it is not a constant - which are computed once
/// in the preheader. One pointer is carried per base, counter, scale and invariant part; two subscripts
/// that differ by a constant share it through a displacement the selector folds into the addressing
/// mode. Only near pointers are stepped: a far element's address is a segment and an offset together.
/// </para>
/// <para>
/// It runs in the native pipeline, after the loop vectoriser (which recognises the counter-indexed
/// form this removes). The C, LLVM and BASIC writers render the IR rather than select it, and a
/// pointer phi says nothing to them a subscript did not.
/// </para>
/// </summary>
public static class AddressInduction {

  private const int _MAX_AFFINE_DEPTH = 16;

  /// <summary>
  /// An offset as <c>Scale * counter + Offset + sum(Terms)</c> modulo the pointer width, each term a
  /// value defined outside the loop times a coefficient.
  /// </summary>
  private sealed record Linear(long Scale, long Offset, IReadOnlyList<(IrValue Value, long Coefficient)> Terms) {
    public static Linear Constant(long value) => new(0, value, []);
    public static Linear Counter() => new(1, 0, []);
    public static Linear Invariant(IrValue value) => new(0, 0, [(value, 1)]);

    public Linear Plus(Linear other, long sign) => new(
      unchecked(this.Scale + sign * other.Scale),
      unchecked(this.Offset + sign * other.Offset),
      [.. this.Terms, .. other.Terms.Select(term => (term.Value, unchecked(sign * term.Coefficient)))]);

    public Linear Times(long factor) => new(
      unchecked(this.Scale * factor),
      unchecked(this.Offset * factor),
      [.. this.Terms.Select(term => (term.Value, unchecked(term.Coefficient * factor)))]);

    public bool IsConstant => this.Scale == 0 && this.Terms.Count == 0;
  }

  /// <summary>A counter the latch advances by a constant: the header phi, its first value and its step.</summary>
  private sealed record Counter(IrPhi Phi, IrValue Start, long Step);

  /// <summary>A carried pointer: the phi, and the constant part of the offset its first value holds.</summary>
  private sealed record Carried(IrValue Base, Counter Counter, long Scale, IReadOnlyList<(IrValue, long)> Terms,
    IrPhi Pointer, long FirstOffset);

  /// <summary>Steps what can be stepped in <paramref name="function"/>; the number of addresses rewritten.</summary>
  public static int Run(IrFunction function, int pointerBits = 16) {
    ArgumentNullException.ThrowIfNull(function);
    if (function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm)
      return 0;
    AddressOffsetNarrowing.Run(function, pointerBits);
    if (IrDominators.Build(function) is not { } dominators)
      return 0;

    var rewritten = 0;
    var carried = new List<Carried>();
    // innermost first: an address inside a nested loop steps with the counter that changes fastest
    foreach (var loop in IrLoopAnalysis.Build(function, dominators).Loops.OrderByDescending(loop => loop.Depth)) {
      // the first address is pure arithmetic, so the block entering the loop may compute it even when
      // that block branches elsewhere too - it need not be a canonical preheader
      if (loop.UniqueEnteringBlock is not { Terminator: not null } preheader || loop.Latches is not [var latch])
        continue;
      var counters = Counters(loop, preheader, latch, pointerBits);
      if (counters.Count == 0)
        continue;
      foreach (var address in loop.Blocks.SelectMany(block => block.Instructions).OfType<IrGep>().ToList()) {
        if (address.Parent is null || address.ElementType is not null || address.Type.IsFarPointer
            || address.ByteOffset.Type.Bits != pointerBits || !IsInvariant(address.BasePtr, loop))
          continue;
        foreach (var counter in counters) {
          if (TryLinear(address.ByteOffset, counter.Phi, loop, _MAX_AFFINE_DEPTH) is not { } form
              || Wrap(form.Scale, pointerBits) == 0)
            continue;
          Rewrite(loop, preheader, latch, address, counter, form, carried, pointerBits);
          ++rewritten;
          break;
        }
      }
    }
    return rewritten;
  }

  /// <summary>The header phis of pointer width the single latch advances by a constant.</summary>
  private static List<Counter> Counters(IrLoopAnalysis.Loop loop, IrBasicBlock preheader, IrBasicBlock latch, int pointerBits) {
    var counters = new List<Counter>();
    foreach (var phi in loop.Header.Instructions.OfType<IrPhi>()) {
      if (phi.Type is not { IsInteger: true } type || type.Bits != pointerBits
          || phi.IncomingFrom(preheader) is not { } start
          || phi.IncomingFrom(latch) is not IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub, Rhs: IrConstantInt step } next
          || !ReferenceEquals(next.Lhs, phi))
        continue;
      counters.Add(new(phi, start, next.Op == IrBinaryOp.Add ? step.Value : unchecked(-step.Value)));
    }
    return counters;
  }

  /// <summary>Defined outside the loop, so the same value on every iteration.</summary>
  private static bool IsInvariant(IrValue value, IrLoopAnalysis.Loop loop)
    => value is not IrInstruction { Parent: { } block } || !loop.Contains(block);

  private static Linear? TryLinear(IrValue value, IrPhi counter, IrLoopAnalysis.Loop loop, int depth) {
    if (depth <= 0 || !value.Type.IsInteger)
      return null;
    if (ReferenceEquals(value, counter))
      return Linear.Counter();
    if (value is IrConstantInt constant)
      return Linear.Constant(constant.Value);
    if (IsInvariant(value, loop))
      return Linear.Invariant(value);
    if (value is not IrBinary binary
        || TryLinear(binary.Lhs, counter, loop, depth - 1) is not { } left
        || TryLinear(binary.Rhs, counter, loop, depth - 1) is not { } right)
      return null;
    return binary.Op switch {
      IrBinaryOp.Add => left.Plus(right, 1),
      IrBinaryOp.Sub => left.Plus(right, -1),
      IrBinaryOp.Mul when right.IsConstant => left.Times(right.Offset),
      IrBinaryOp.Mul when left.IsConstant => right.Times(left.Offset),
      IrBinaryOp.Shl when right.IsConstant && right.Offset is >= 0 and < 63 => left.Times(1L << (int)right.Offset),
      _ => null,
    };
  }

  private static void Rewrite(IrLoopAnalysis.Loop loop, IrBasicBlock preheader, IrBasicBlock latch, IrGep address,
      Counter counter, Linear form, List<Carried> carried, int pointerBits) {
    var type = address.ByteOffset.Type;
    var scale = Wrap(form.Scale, pointerBits);
    var pointer = carried.FirstOrDefault(entry => ReferenceEquals(entry.Base, address.BasePtr)
      && ReferenceEquals(entry.Counter, counter) && entry.Scale == scale && SameTerms(entry.Terms, form.Terms));
    if (pointer is null) {
      var phi = loop.Header.AppendPhi(new IrPhi(address.Type) {
        Name = address.Name is null ? null : $"{address.Name}.iv",
      });
      var first = HandedOver(carried, address.BasePtr, counter.Start, scale, form, preheader, type)
        ?? FirstAddress(preheader, address.BasePtr, counter.Start, scale, form, type);
      var step = Wrap(unchecked(scale * counter.Step), pointerBits);
      var next = latch.InsertBefore(new IrGep(phi, new IrConstantInt(type, step)), latch.Terminator!);
      phi.AddIncoming(first, preheader);
      phi.AddIncoming(next, latch);
      pointer = new(address.BasePtr, counter, scale, form.Terms, phi, form.Offset);
      carried.Add(pointer);
    }

    var displacement = Wrap(form.Offset - pointer.FirstOffset, pointerBits);
    IrValue replacement = displacement == 0
      ? pointer.Pointer
      : address.Parent!.InsertBefore(new IrGep(pointer.Pointer, new IrConstantInt(type, displacement)), address);
    address.ReplaceAllUsesWith(replacement);
    address.EraseFromParent();
  }

  /// <summary>
  /// The first address taken from a loop that ran before: when this counter starts where another loop's
  /// counter stands, that loop's pointer over the same array already holds <c>base + scale*start</c> -
  /// a remainder loop and the unrolled loop after it are the case - and only the constant part of the
  /// offset can differ. The other pointer's phi is visible here because the counter it moves in step
  /// with is, which also makes the two agree at this point.
  /// </summary>
  private static IrValue? HandedOver(List<Carried> carried, IrValue basePtr, IrValue start, long scale, Linear form,
      IrBasicBlock preheader, IrType type) {
    if (start is not IrPhi previous
        || carried.FirstOrDefault(entry => ReferenceEquals(entry.Counter.Phi, previous)
             && ReferenceEquals(entry.Base, basePtr) && entry.Scale == scale && SameTerms(entry.Terms, form.Terms)) is not { } source)
      return null;
    var displacement = form.Offset - source.FirstOffset;
    return displacement == 0
      ? source.Pointer
      : preheader.InsertBefore(new IrGep(source.Pointer, new IrConstantInt(type, displacement)), preheader.Terminator!);
  }

  /// <summary>
  /// <c>base + scale*start + offset + sum(terms)</c>, built at the end of the preheader - folded to one
  /// displacement when the start is a constant and nothing else is added.
  /// </summary>
  private static IrValue FirstAddress(IrBasicBlock preheader, IrValue basePtr, IrValue start, long scale, Linear form, IrType type) {
    var at = preheader.Terminator!;
    IrValue? sum = null;
    var constant = form.Offset;
    void Add(IrValue value) => sum = sum is null ? value : preheader.InsertBefore(new IrBinary(IrBinaryOp.Add, sum, value), at);
    IrValue Scaled(IrValue value, long coefficient) => coefficient == 1
      ? value
      : preheader.InsertBefore(new IrBinary(IrBinaryOp.Mul, value, new IrConstantInt(type, coefficient)), at);

    if (start is IrConstantInt first)
      constant = unchecked(constant + scale * first.Value);
    else
      Add(Scaled(start, scale));
    foreach (var (value, coefficient) in form.Terms)
      if (coefficient != 0)
        Add(Scaled(value, coefficient));
    if (sum is null)
      return constant == 0 ? basePtr : preheader.InsertBefore(new IrGep(basePtr, new IrConstantInt(type, constant)), at);
    if (constant != 0)
      Add(new IrConstantInt(type, constant));
    return preheader.InsertBefore(new IrGep(basePtr, sum), at);
  }

  private static bool SameTerms(IReadOnlyList<(IrValue Value, long Coefficient)> left, IReadOnlyList<(IrValue Value, long Coefficient)> right) {
    if (left.Count != right.Count)
      return false;
    for (var i = 0; i < left.Count; ++i)
      if (!ReferenceEquals(left[i].Value, right[i].Value) || left[i].Coefficient != right[i].Coefficient)
        return false;
    return true;
  }

  /// <summary><paramref name="value"/> modulo 2^<paramref name="bits"/>, read as signed.</summary>
  private static long Wrap(long value, int bits) {
    if (bits >= 64)
      return value;
    var shift = 64 - bits;
    return (value << shift) >> shift;
  }
}
