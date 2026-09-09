using PowerBasic.Compiler.Ir.Analysis;

namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// O0313 — recognizes an inclusive integer prefix scan and carries its previous result in SSA.
///
/// <para>
/// A memory scan normally reaches back to the preceding element on every iteration:
/// <c>load t[i-1]; next = op(previous, input); store next, t[i]</c>. O0172 proves that the store-to-load
/// edge is the loop's only carried memory dependence and that its distance is exactly one. Once that
/// proof exists, the preceding element is the value produced by the preceding iteration, so a loop
/// phi can carry it directly. The first iteration is seeded by one preheader load of the original
/// preceding element.
/// </para>
/// <para>
/// This first target-neutral layer deliberately does not create worker threads. O0311 owns hosted
/// loop versioning and the runtime threshold; this pass exposes the scan recurrence that such a
/// backend can later split into local scans plus slice offsets. It is still profitable sequentially:
/// one dependent memory load disappears from every iteration while every arithmetic operation and
/// every intermediate store remains in the original order.
/// </para>
/// <para>
/// Only integer addition, multiplication and bitwise AND/OR/XOR are admitted. They are associative
/// over PB's fixed-width integer values, including modular wrap. Floating point and subtraction are
/// left alone because reassociating them for a later parallel scan would change program semantics.
/// </para>
/// </summary>
public static class ParallelPrefixScan {

  private const int _MAX_ADDRESS_DEPTH = 16;

  /// <summary>Canonicalizes recognized prefix scans in <paramref name="fn"/>; returns the number rewritten.</summary>
  public static int Run(IrFunction fn) {
    ArgumentNullException.ThrowIfNull(fn);
    if (fn.HasErrorHandler || fn.HasInlineAsm)
      return 0;

    var rewritten = 0;
    foreach (var header in fn.Blocks.ToList())
      if (header.Parent is not null)
        rewritten += RewriteIn(fn, header);
    return rewritten;
  }

  private static int RewriteIn(IrFunction fn, IrBasicBlock header) {
    if (CountedLoop.Match(fn, header) is not { } loop
        || IrLoopDependenceAnalysis.Analyze(fn, header) is not { IsComplete: true } dependence)
      return 0;

    var carried = dependence.Dependences.Where(d => d.Distance != 0).ToList();
    if (carried.Count != 1)
      return 0;

    var edge = carried[0];
    if (edge.Kind != IrDependenceKind.Flow
        || edge.Direction != IrDependenceDirection.Less
        || edge.Distance != 1
        || edge.Source.Instruction is not IrStore store
        || edge.Sink.Instruction is not IrLoad previous)
      return 0;

    if (!ReferenceEquals(previous.Parent, loop.Latch)
        || store.Value is not IrBinary update
        || !ReferenceEquals(update.Parent, loop.Latch)
        || !ReferenceEquals(store.Parent, loop.Latch)
        || !previous.Type.SameStorage(update.Type)
        || !IsAssociativeInteger(update))
      return 0;

    var previousIsLeft = ReferenceEquals(update.Lhs, previous);
    var previousIsRight = ReferenceEquals(update.Rhs, previous);
    if (previousIsLeft == previousIsRight)
      return 0;

    if (previous.Users.Any(user => user.Parent is not { } parent || !loop.Region.Contains(parent)))
      return 0;

    // Moving the first previous-element load into the preheader is only valid when nothing else in
    // the same iteration writes or otherwise orders that memory access. The one distance-one edge is
    // the scan itself; any additional dependence touching the load makes its timing observable.
    if (dependence.Dependences.Any(other => !ReferenceEquals(other, edge)
        && (ReferenceEquals(other.Source.Instruction, previous)
          || ReferenceEquals(other.Sink.Instruction, previous))))
      return 0;

    if (loop.Counter.IncomingFrom(loop.Preheader) is not { } initial
        || !CanMaterializeInPreheader(previous.Pointer, loop, _MAX_ADDRESS_DEPTH))
      return 0;

    var cache = new Dictionary<IrValue, IrValue>(ReferenceEqualityComparer.Instance);
    var seedPointer = MaterializeInPreheader(previous.Pointer, loop, initial, cache);
    var terminator = loop.Preheader.Terminator!;
    var seed = loop.Preheader.InsertBefore(new IrLoad(previous.Type, seedPointer), terminator);
    var scan = loop.Header.AppendPhi(new IrPhi(previous.Type) {
      Name = previous.Name is { Length: > 0 } name ? name + ".scan" : null,
    });
    scan.AddIncoming(seed, loop.Preheader);
    scan.AddIncoming(update, loop.Latch);

    previous.ReplaceAllUsesWith(scan);
    previous.EraseFromParent();
    return 1;
  }

  private static bool IsAssociativeInteger(IrBinary update)
    => update.Type.IsInteger
      && update.Op is IrBinaryOp.Add or IrBinaryOp.Mul or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor;

  private static bool CanMaterializeInPreheader(IrValue value, CountedLoop loop, int depth) {
    if (ReferenceEquals(value, loop.Counter))
      return true;
    if (value is not IrInstruction instruction)
      return true;
    if (instruction.Parent is not { } parent)
      return false;
    if (!loop.Region.Contains(parent))
      return true;
    if (depth <= 0)
      return false;

    return instruction switch {
      IrGep gep => CanMaterializeInPreheader(gep.BasePtr, loop, depth - 1)
        && CanMaterializeInPreheader(gep.ByteOffset, loop, depth - 1),
      IrBinary binary when IsAffineAddressOp(binary.Op) =>
        CanMaterializeInPreheader(binary.Lhs, loop, depth - 1)
        && CanMaterializeInPreheader(binary.Rhs, loop, depth - 1),
      IrCast { Op: IrCastOp.SExt or IrCastOp.Trunc } cast =>
        CanMaterializeInPreheader(cast.Value, loop, depth - 1),
      _ => false,
    };
  }

  private static IrValue MaterializeInPreheader(IrValue value, CountedLoop loop, IrValue initial,
      Dictionary<IrValue, IrValue> cache) {
    if (ReferenceEquals(value, loop.Counter))
      return initial;
    if (value is not IrInstruction instruction || instruction.Parent is { } parent && !loop.Region.Contains(parent))
      return value;
    if (cache.TryGetValue(value, out var cached))
      return cached;

    IrInstruction clone = instruction switch {
      IrGep gep when gep.ElementType is { } elementType => new IrGep(
        MaterializeInPreheader(gep.BasePtr, loop, initial, cache),
        MaterializeInPreheader(gep.ByteOffset, loop, initial, cache), elementType),
      IrGep gep => new IrGep(
        MaterializeInPreheader(gep.BasePtr, loop, initial, cache),
        MaterializeInPreheader(gep.ByteOffset, loop, initial, cache)),
      IrBinary binary => new IrBinary(binary.Op,
        MaterializeInPreheader(binary.Lhs, loop, initial, cache),
        MaterializeInPreheader(binary.Rhs, loop, initial, cache)),
      IrCast cast => new IrCast(cast.Op,
        MaterializeInPreheader(cast.Value, loop, initial, cache), cast.Type),
      _ => throw new InvalidOperationException("prevalidated scan address contained an unsupported instruction"),
    };

    loop.Preheader.InsertBefore(clone, loop.Preheader.Terminator!);
    cache[value] = clone;
    return clone;
  }

  private static bool IsAffineAddressOp(IrBinaryOp op)
    => op is IrBinaryOp.Add or IrBinaryOp.Sub or IrBinaryOp.Mul or IrBinaryOp.Shl;
}
