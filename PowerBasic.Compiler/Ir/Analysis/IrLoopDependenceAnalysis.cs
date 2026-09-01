using PowerBasic.Compiler.Ir.Passes;

namespace PowerBasic.Compiler.Ir.Analysis;

/// <summary>The observable memory-order relationship between two loop accesses.</summary>
public enum IrDependenceKind {
  /// <summary>A write supplies a later read (RAW / true dependence).</summary>
  Flow,
  /// <summary>A read must happen before a later write (WAR / anti-dependence).</summary>
  Anti,
  /// <summary>Two writes must retain their order (WAW / output dependence).</summary>
  Output,
}

/// <summary>
/// One component of a dependence direction vector. O0172 currently analyzes one loop level, but the
/// enum is deliberately the full three-way relation so nested-loop analysis can extend the result
/// without changing consumers.
/// </summary>
public enum IrDependenceDirection {
  Less,
  Equal,
  Greater,
}

/// <summary>A load or store inside the analyzed loop.</summary>
public sealed record IrLoopMemoryAccess(IrInstruction Instruction, IrValue Pointer, IrType AccessType, bool Writes);

/// <summary>
/// A proven dependence from <see cref="Source"/> to <see cref="Sink"/>. <see cref="Distance"/> is the
/// sink iteration minus the source iteration at the analyzed loop level; it is zero for a dependence
/// within one iteration and positive for every loop-carried dependence produced by the current
/// single-level analysis.
/// </summary>
public sealed record IrLoopDependence(
  IrLoopMemoryAccess Source,
  IrLoopMemoryAccess Sink,
  IrDependenceKind Kind,
  IrDependenceDirection Direction,
  long Distance);

/// <summary>The dependence facts for one recognized counted loop.</summary>
public sealed record IrLoopDependenceInfo(
  IrBasicBlock Header,
  IrPhi Counter,
  long Trips,
  IReadOnlyList<IrLoopMemoryAccess> Accesses,
  IReadOnlyList<IrLoopDependence> Dependences,
  bool IsComplete) {

  /// <summary>True when at least one proven dependence crosses an iteration boundary.</summary>
  public bool HasLoopCarriedDependence => this.Dependences.Any(d => d.Distance != 0);
}

/// <summary>
/// O0172 — target-independent loop memory-dependence analysis for counted loops.
///
/// <para>
/// The first implemented layer deliberately answers a narrower question exactly rather than a wider
/// one optimistically. It recognizes byte addresses that are affine in the loop's canonical counter,
/// <c>base + stride*iteration + constant</c>, after constant add/subtract/multiply/shift and safe
/// signed widening/truncation. Every intermediate arithmetic result must be provably wrap-free over
/// the whole iteration domain; otherwise that access remains unknown.
/// </para>
/// <para>
/// Equal-stride access pairs are solved exactly, including access width, so overlapping byte ranges
/// produce exact dependence distances. Unequal strides use the classical GCD test plus the bounded
/// interval test: either can prove that no integer solution exists; if both admit a solution, the
/// bounded Diophantine problem is deliberately left unknown for the later SIV/MIV layers. Distinct
/// underlying objects are dismissed first by <see cref="IrAliasAnalysis"/>.
/// </para>
/// <para>
/// <see cref="IrLoopDependenceInfo.IsComplete"/> is the safety boundary for consumers. Unknown pointer
/// provenance, loop-varying roots, calls with unmodelled memory effects, wrapping index arithmetic or
/// an unresolved unequal-stride equation set it false. A transform may use the proven dependences for
/// diagnostics or costing, but may only treat their absence as independence when the result is
/// complete.
/// </para>
/// </summary>
public static class IrLoopDependenceAnalysis {

  private const int _MAX_AFFINE_DEPTH = 16;

  private readonly record struct Affine(long Stride, long Constant);
  private readonly record struct AffineAddress(IrValue Root, long Stride, long Constant, int Bytes);

  /// <summary>
  /// Analyzes the counted loop headed by <paramref name="header"/>. Returns null when the header is not
  /// a loop shape understood by <see cref="CountedLoop"/>; otherwise returns conservative dependence
  /// information, with <see cref="IrLoopDependenceInfo.IsComplete"/> saying whether every relevant
  /// memory pair was decided.
  /// </summary>
  public static IrLoopDependenceInfo? Analyze(IrFunction fn, IrBasicBlock header) {
    ArgumentNullException.ThrowIfNull(fn);
    ArgumentNullException.ThrowIfNull(header);

    if (fn.HasErrorHandler || fn.HasInlineAsm || CountedLoop.Match(fn, header) is not { } loop)
      return null;

    var accesses = CollectAccesses(fn, loop);
    var complete = !HasUnknownMemoryCall(loop);
    if (!TryInduction(loop, out var start, out var step))
      return new(loop.Header, loop.Counter, loop.Trips, accesses, [], false);

    var addresses = new Dictionary<IrLoopMemoryAccess, AffineAddress?>(ReferenceEqualityComparer.Instance);
    AffineAddress? AddressOf(IrLoopMemoryAccess access) {
      if (addresses.TryGetValue(access, out var cached))
        return cached;
      AffineAddress? address = TryAddress(loop, access, start, step, out var matched) ? matched : null;
      addresses[access] = address;
      return address;
    }

    var found = new HashSet<IrLoopDependence>();
    for (var i = 0; i < accesses.Count; ++i)
      for (var j = i; j < accesses.Count; ++j) {
        var first = accesses[i];
        var second = accesses[j];
        if (!first.Writes && !second.Writes)
          continue;

        if (IrAliasAnalysis.Alias(first.Pointer, first.AccessType, second.Pointer, second.AccessType)
            == IrAliasResult.NoAlias)
          continue;

        if (AddressOf(first) is not { } a || AddressOf(second) is not { } b
            || !ReferenceEquals(a.Root, b.Root)) {
          complete = false;
          continue;
        }

        var distances = FindDistances(a, b, loop.Trips);
        if (distances is null) {
          complete = false;
          continue;
        }

        foreach (var distance in distances)
          AddDependence(found, first, second, distance, ref complete);
      }

    return new(loop.Header, loop.Counter, loop.Trips, accesses, found
      .OrderBy(d => d.Distance)
      .ThenBy(d => d.Kind)
      .ToList(), complete);
  }

  private static List<IrLoopMemoryAccess> CollectAccesses(IrFunction fn, CountedLoop loop) {
    var result = new List<IrLoopMemoryAccess>();
    foreach (var block in fn.Blocks)
      if (loop.Region.Contains(block))
        foreach (var instruction in block.Instructions)
          switch (instruction) {
            case IrLoad load:
              result.Add(new(load, load.Pointer, load.Type, false));
              break;
            case IrStore store:
              result.Add(new(store, store.Pointer, store.Value.Type, true));
              break;
          }
    return result;
  }

  private static bool HasUnknownMemoryCall(CountedLoop loop) {
    foreach (var block in loop.Region)
      foreach (var instruction in block.Instructions)
        if (instruction is IrCall call
            && (call.Callee is not IrFunction callee || !FunctionSummaries.IsPureExternal(callee.Name)))
          return true;
    return false;
  }

  private static bool TryInduction(CountedLoop loop, out long start, out long step) {
    start = step = 0;
    if (!loop.Counter.Type.IsInteger || !loop.Counter.Type.Signed)
      return false;
    if (loop.Counter.IncomingFrom(loop.Preheader) is not IrConstantInt initial
        || loop.Counter.IncomingFrom(loop.Latch) is not IrBinary { Op: IrBinaryOp.Add } next
        || !ReferenceEquals(next.Lhs, loop.Counter)
        || next.Rhs is not IrConstantInt increment)
      return false;

    start = Signed(initial);
    step = Signed(increment);
    if (step == 0)
      return false;

    var values = new Affine(step, start);
    return Fits(values, loop.Counter.Type, loop.Trips);
  }

  private static bool TryAddress(CountedLoop loop, IrLoopMemoryAccess access, long start, long step,
      out AffineAddress address) {
    address = default;
    if (IrAliasAnalysis.StorageBytes(access.AccessType) is not { } bytes || bytes <= 0)
      return false;

    var root = access.Pointer;
    var total = new Affine(0, 0);
    while (root is IrGep gep) {
      if (!TryAffine(loop, gep.ByteOffset, start, step, _MAX_AFFINE_DEPTH, out var displacement))
        return false;
      if (gep.ElementType is { } elementType) {
        if (IrAliasAnalysis.StorageBytes(elementType) is not { } elementBytes
            || !TryScale(displacement, elementBytes, out displacement))
          return false;
      }
      if (!TryAdd(total, displacement, out total))
        return false;
      root = gep.BasePtr;
    }

    // One SSA pointer defined outside the loop is one stable base value. An instruction defined in
    // the loop executes once per iteration and may therefore answer a different pointer each time.
    if (root is IrInstruction definition && definition.Parent is { } parent && loop.Region.Contains(parent))
      return false;

    address = new(root, total.Stride, total.Constant, bytes);
    return true;
  }

  private static bool TryAffine(CountedLoop loop, IrValue value, long start, long step, int depth, out Affine affine) {
    affine = default;
    if (depth <= 0 || !value.Type.IsInteger || !value.Type.Signed)
      return false;

    if (ReferenceEquals(value, loop.Counter)) {
      affine = new(step, start);
      return Fits(affine, value.Type, loop.Trips);
    }

    if (value is IrConstantInt constant) {
      affine = new(0, Signed(constant));
      return Fits(affine, value.Type, loop.Trips);
    }

    if (value is IrCast cast) {
      if (!TryAffine(loop, cast.Value, start, step, depth - 1, out var source))
        return false;
      switch (cast.Op) {
        case IrCastOp.SExt when cast.Value.Type.Signed:
          affine = source;
          return Fits(affine, cast.Type, loop.Trips);
        case IrCastOp.Trunc when cast.Type.Signed && Fits(source, cast.Type, loop.Trips):
          affine = source;                         // the truncation provably changes no bit
          return true;
        default:
          return false;
      }
    }

    if (value is not IrBinary binary
        || !TryAffine(loop, binary.Lhs, start, step, depth - 1, out var left)
        || !TryAffine(loop, binary.Rhs, start, step, depth - 1, out var right))
      return false;

    switch (binary.Op) {
      case IrBinaryOp.Add:
        if (!TryAdd(left, right, out affine))
          return false;
        break;
      case IrBinaryOp.Sub:
        if (!TrySubtract(left, right, out affine))
          return false;
        break;
      case IrBinaryOp.Mul when left.Stride == 0:
        if (!TryScale(right, left.Constant, out affine))
          return false;
        break;
      case IrBinaryOp.Mul when right.Stride == 0:
        if (!TryScale(left, right.Constant, out affine))
          return false;
        break;
      case IrBinaryOp.Shl when right.Stride == 0 && right.Constant is >= 0 and <= 62:
        if (!TryScale(left, 1L << (int)right.Constant, out affine))
          return false;
        break;
      default:
        return false;                              // non-affine or interpretation-changing operation
    }

    return Fits(affine, binary.Type, loop.Trips);
  }

  /// <summary>
  /// Exact distance set for equal strides; null means an unequal-stride equation survived both cheap
  /// disproval tests and therefore needs a stronger bounded Diophantine solver.
  /// </summary>
  private static HashSet<long>? FindDistances(AffineAddress first, AffineAddress second, long trips) {
    if (first.Stride == second.Stride) {
      if (first.Stride == 0) {
        if (!RangesOverlap(first.Constant, first.Bytes, second.Constant, second.Bytes))
          return [];
        var constantDistances = new HashSet<long> { 0 };
        if (trips > 1) {
          constantDistances.Add(1);
          constantDistances.Add(-1);
        }
        return constantDistances;
      }

      var distances = new HashSet<long>();
      for (var firstByte = 0; firstByte < first.Bytes; ++firstByte)
        for (var secondByte = 0; secondByte < second.Bytes; ++secondByte) {
          if (!TryAdd(first.Constant, firstByte, out var firstAt)
              || !TryAdd(second.Constant, secondByte, out var secondAt)
              || !TrySubtract(firstAt, secondAt, out var delta))
            return null;
          if (delta % first.Stride != 0)
            continue;
          long distance;
          try {
            distance = checked(delta / first.Stride);
          } catch (OverflowException) {
            return null;
          }
          if (distance > -trips && distance < trips)
            distances.Add(distance);
        }
      return distances;
    }

    // GCD is a necessary condition for A*k - B*l = delta. The interval bound is the one-dimensional
    // Banerjee bound for k,l in [0,trips-1]. If either disproves every byte-pair equation, the accesses
    // are independent. If one equation survives, a later exact SIV/MIV layer must decide it.
    for (var firstByte = 0; firstByte < first.Bytes; ++firstByte)
      for (var secondByte = 0; secondByte < second.Bytes; ++secondByte) {
        if (!TryAdd(second.Constant, secondByte, out var secondAt)
            || !TryAdd(first.Constant, firstByte, out var firstAt)
            || !TrySubtract(secondAt, firstAt, out var rhs))
          return null;
        if (!CouldHaveIntegerSolution(first.Stride, second.Stride, rhs, trips))
          continue;
        return null;
      }
    return [];
  }

  private static bool CouldHaveIntegerSolution(long firstStride, long secondStride, long rhs, long trips) {
    if (!TryGcd(firstStride, secondStride, out var gcd) || gcd == 0)
      return true;
    if (rhs % gcd != 0)
      return false;

    try {
      var last = checked(trips - 1);
      var firstExtent = checked(firstStride * last);
      var secondCoefficient = checked(-secondStride);
      var secondExtent = checked(secondCoefficient * last);
      var minimum = checked(Math.Min(0, firstExtent) + Math.Min(0, secondExtent));
      var maximum = checked(Math.Max(0, firstExtent) + Math.Max(0, secondExtent));
      return rhs >= minimum && rhs <= maximum;
    } catch (OverflowException) {
      return true;
    }
  }

  private static void AddDependence(HashSet<IrLoopDependence> found,
      IrLoopMemoryAccess first, IrLoopMemoryAccess second, long distance, ref bool complete) {
    if (ReferenceEquals(first, second)) {
      if (distance == 0 || !first.Writes)
        return;
      found.Add(new(first, first, IrDependenceKind.Output, IrDependenceDirection.Less, Math.Abs(distance)));
      return;
    }

    if (distance == 0) {
      if (!ReferenceEquals(first.Instruction.Parent, second.Instruction.Parent)) {
        // Address equality is known, but path-sensitive statement order is not. Do not invent a
        // source/sink direction for two different blocks.
        complete = false;
        return;
      }
      found.Add(new(first, second, Kind(first, second), IrDependenceDirection.Equal, 0));
      return;
    }

    if (distance > 0)
      found.Add(new(first, second, Kind(first, second), IrDependenceDirection.Less, distance));
    else
      found.Add(new(second, first, Kind(second, first), IrDependenceDirection.Less, -distance));
  }

  private static IrDependenceKind Kind(IrLoopMemoryAccess source, IrLoopMemoryAccess sink) =>
    (source.Writes, sink.Writes) switch {
      (true, false) => IrDependenceKind.Flow,
      (false, true) => IrDependenceKind.Anti,
      (true, true) => IrDependenceKind.Output,
      _ => throw new InvalidOperationException("read/read pairs are not memory-order dependences"),
    };

  private static bool RangesOverlap(long firstStart, int firstBytes, long secondStart, int secondBytes) {
    try {
      var firstEnd = checked(firstStart + firstBytes);
      var secondEnd = checked(secondStart + secondBytes);
      return firstStart < secondEnd && secondStart < firstEnd;
    } catch (OverflowException) {
      return true;                                  // cannot prove disjoint
    }
  }

  private static bool Fits(Affine affine, IrType type, long trips) {
    if (!type.IsInteger || !type.Signed || trips <= 0)
      return false;
    if (!TryRange(affine, trips, out var lo, out var hi))
      return false;
    var allowed = ValueRange.OfType(type);
    return lo >= allowed.Lo && hi <= allowed.Hi;
  }

  private static bool TryRange(Affine affine, long trips, out long lo, out long hi) {
    lo = hi = 0;
    try {
      var last = checked(affine.Constant + checked(affine.Stride * checked(trips - 1)));
      lo = Math.Min(affine.Constant, last);
      hi = Math.Max(affine.Constant, last);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryAdd(Affine left, Affine right, out Affine result) {
    result = default;
    try {
      result = new(checked(left.Stride + right.Stride), checked(left.Constant + right.Constant));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TrySubtract(Affine left, Affine right, out Affine result) {
    result = default;
    try {
      result = new(checked(left.Stride - right.Stride), checked(left.Constant - right.Constant));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryScale(Affine value, long factor, out Affine result) {
    result = default;
    try {
      result = new(checked(value.Stride * factor), checked(value.Constant * factor));
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryAdd(long left, long right, out long result) {
    result = 0;
    try {
      result = checked(left + right);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TrySubtract(long left, long right, out long result) {
    result = 0;
    try {
      result = checked(left - right);
      return true;
    } catch (OverflowException) {
      return false;
    }
  }

  private static bool TryGcd(long left, long right, out long gcd) {
    gcd = 0;
    if (left == long.MinValue || right == long.MinValue)
      return false;
    var a = Math.Abs(left);
    var b = Math.Abs(right);
    while (b != 0)
      (a, b) = (b, a % b);
    gcd = a;
    return true;
  }

  private static long Signed(IrConstantInt constant) {
    if (constant.Type.Bits >= 64)
      return constant.Value;
    var bits = constant.Type.Bits;
    var pattern = constant.ZeroExtended;
    var sign = 1UL << (bits - 1);
    return unchecked((long)((pattern ^ sign) - sign));
  }
}