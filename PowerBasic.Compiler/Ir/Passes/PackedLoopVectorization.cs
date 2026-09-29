namespace PowerBasic.Compiler.Ir.Passes;

/// <summary>
/// pb36 R4 auto-vectorisation: a counted loop <c>FOR i = lo TO hi : c(i) = a(i) OP b(i) : NEXT</c> over
/// 16-bit elements becomes one call to a packed kernel (<c>rt_packed16_add</c> and its siblings), which
/// the DOS runtime emits with the widest SIMD the target declares - AVX-512, AVX2, SSE2 or MMX - and a
/// scalar tail. OP is + - AND OR XOR or *, each of which the packed unit computes per 16-bit lane with
/// the same wrap-around as the scalar instruction, so the result is identical to the loop's.
///
/// <para>
/// This is the direct emitter's vectoriser moved to where the loop is visible: a counted loop in SSA,
/// after inlining and propagation have exposed it, rather than a FOR statement in the syntax tree. The
/// recognised shape is deliberately narrow, and every uncertainty keeps the loop:
/// </para>
/// <list type="bullet">
/// <item>the header holds only the counter's phi and its compare against a constant limit, entered from
/// a single preheader with a constant start, and the body is one block that branches back;</item>
/// <item>the body stores exactly once, loads at most twice, calls nothing, and computes nothing else
/// but the element offsets - so an overflow or bounds check, which is a branch, keeps the loop;</item>
/// <item>every element address is a byte offset off an alloca or a global - near memory - that steps by
/// exactly two bytes per iteration.</item>
/// </list>
/// <para>
/// A destination that is also a source is fine: every lane is read before it is written, at the same
/// index, just as in the loop. The pass only runs where the native back end has asked for it with a
/// vector width, because the kernels are a fact about the DOS runtime and nothing else.
/// </para>
/// <para>
/// O0308: under <c>$ERROR OVERFLOW</c> an add or subtract carries a signed-overflow test whose failing
/// side raises Error 6, which the packed unit cannot do per lane. Such a loop is kept whole and put
/// behind a checked kernel (<c>rt_packed16_add_checked</c>): it scans the elements once, read-only,
/// for any overflow, and only if there is none computes them packed and answers 0. Otherwise it
/// answers 1 having written nothing, and the original loop runs and raises at the element it always
/// did. The scan is a second pass over the arrays, so it is only paid where the vectors win it back:
/// at least 32 elements and two whole vectors.
/// </para>
/// </summary>
public static class PackedLoopVectorization {

  /// <summary>Loops shorter than this stay scalar, as they did in the direct emitter.</summary>
  private const int MinimumTrip = 8;

  /// <summary>Rewrites qualifying loops of <paramref name="function"/>; returns how many.</summary>
  public static int Run(IrFunction function, int vectorBytes) {
    ArgumentNullException.ThrowIfNull(function);
    if (vectorBytes < 8 || function.IsDeclaration || function.HasErrorHandler || function.HasInlineAsm
        || function.Module is not { } module)
      return 0;
    var made = 0;
    foreach (var header in function.Blocks.ToList())
      if (ReferenceEquals(header.Parent, function) && TryRewrite(module, function, header, vectorBytes / 2))
        ++made;
    return made;
  }

  /// <summary>The preflight scan is a second walk over the arrays; below this many elements it does not pay.</summary>
  private const int MinimumCheckedTrip = 32;

  private static bool TryRewrite(IrModule module, IrFunction function, IrBasicBlock header, int lanes) {
    if (MatchChecked(function, header) is { } checkedLoop)
      return checkedLoop.Trip >= Math.Max(MinimumCheckedTrip, 2 * lanes) && RewriteChecked(module, checkedLoop);
    if (Match(function, header) is not { } loop || loop.Trip < Math.Max(MinimumTrip, lanes))
      return false;

    var kernel = module.FindFunction(loop.Kernel) ?? module.AddFunction(new IrFunction(loop.Kernel, IrType.Void,
      [new IrArgument(IrType.Ptr, 0, "c"), new IrArgument(IrType.Ptr, 1, "a"),
       new IrArgument(IrType.Ptr, 2, "b"), new IrArgument(IrType.I16, 3, "n")]));
    var preheader = loop.Preheader;
    var jump = preheader.Terminator!;
    IrValue Start(IrValue basePtr, long offset) {
      var address = new IrGep(basePtr, new IrConstantInt(IrType.I32, offset));
      preheader.InsertBefore(address, jump);
      return address;
    }
    var destination = Start(loop.C.Base, loop.C.Offset);
    var left = Start(loop.A.Base, loop.A.Offset);
    var right = Start(loop.B.Base, loop.B.Offset);
    preheader.InsertBefore(new IrCall(IrType.Void, kernel,
      [destination, left, right, new IrConstantInt(IrType.I16, loop.Trip)]), jump);

    // leave by the exit, carrying the counter's final value to anyone who reads it after the loop
    var exitValue = new IrConstantInt(loop.Counter.Type, loop.ExitValue);
    foreach (var phi in loop.Exit.Phis.ToList())
      if (phi.IncomingFrom(header) is { } incoming) {
        phi.RemoveIncoming(header);
        phi.AddIncoming(ReferenceEquals(incoming, loop.Counter) ? exitValue : incoming, preheader);
      }
    foreach (var user in loop.Counter.Users.ToList())
      if (!ReferenceEquals(user.Parent, header) && !ReferenceEquals(user.Parent, loop.Body))
        for (var i = 0; i < user.Operands.Count; ++i)
          if (ReferenceEquals(user.Operands[i], loop.Counter))
            user.SetOperand(i, exitValue);
    ((IrBr)jump).Target = loop.Exit;

    function.RemoveBlock(loop.Body);
    function.RemoveBlock(header);
    return true;
  }

  private sealed record Element(IrValue Base, long Offset);

  private sealed record Loop(IrBasicBlock Preheader, IrBasicBlock Body, IrBasicBlock Exit, IrPhi Counter,
    long Trip, long ExitValue, string Kernel, Element C, Element A, Element B);

  private static Loop? Match(IrFunction function, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr { Condition: IrCmp compare } branch
        || header.Instructions.Count != 3
        || header.Instructions[0] is not IrPhi counter || !ReferenceEquals(header.Instructions[1], compare)
        || counter.Type is not { IsInteger: true, Bits: 16 }
        || !ReferenceEquals(compare.Lhs, counter) || compare.Rhs is not IrConstantInt { Value: var limit }
        || compare.Pred is not (IrCmpPred.Sle or IrCmpPred.Slt))
      return null;
    var body = branch.IfTrue;
    var exit = branch.IfFalse;
    if (ReferenceEquals(body, header) || ReferenceEquals(exit, header) || ReferenceEquals(body, exit)
        || body.Terminator is not IrBr { Target: var back } || !ReferenceEquals(back, header)
        || body.Predecessors.Count() != 1 || function.AddressTakenBlocks().Contains(body)
        || function.AddressTakenBlocks().Contains(header))
      return null;
    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2 || counter.IncomingBlocks.Count != 2)
      return null;
    var preheader = predecessors.Single(p => !ReferenceEquals(p, body));
    if (preheader.Terminator is not IrBr
        || counter.IncomingFrom(preheader) is not IrConstantInt { Value: var start }
        || counter.IncomingFrom(body) is not IrBinary { Op: IrBinaryOp.Add, Rhs: IrConstantInt { Value: 1 } } next
        || !ReferenceEquals(next.Lhs, counter) || !ReferenceEquals(next.Parent, body))
      return null;

    // the trip, in the loop's own signed 16-bit arithmetic; a limit at the top of the range would make
    // the BASIC loop wrap rather than end, and that is not this pass's to reason about
    var last = compare.Pred == IrCmpPred.Sle ? limit : limit - 1;
    if (last >= short.MaxValue)
      return null;
    var trip = last - start + 1;
    if (trip <= 0 || trip > short.MaxValue)
      return null;

    // the body: one store of OP applied to at most two loads, nothing else but address arithmetic
    var stores = body.Instructions.OfType<IrStore>().ToList();
    if (stores is not [{ Value: IrBinary { Type: { IsInteger: true, Bits: 16 } } operation } store]
        || KernelFor(operation.Op) is not { } kernel
        || operation.Lhs is not IrLoad left || operation.Rhs is not IrLoad right
        || body.Instructions.Any(i => i is IrCall or IrPhi || i.IsTerminator && i is not IrBr))
      return null;
    foreach (var instruction in body.Instructions) {
      if (ReferenceEquals(instruction, store) || instruction is IrBr)
        continue;
      if (instruction is not (IrLoad or IrBinary or IrCast or IrGep))
        return null;
      // nothing computed in the loop may be read after it
      if (instruction.Users.Any(user => !ReferenceEquals(user.Parent, body) && !ReferenceEquals(user, counter)))
        return null;
    }
    if (body.Instructions.OfType<IrLoad>().Any(load => !ReferenceEquals(load, left) && !ReferenceEquals(load, right))
        || left.Type is not { Bits: 16 } || right.Type is not { Bits: 16 })
      return null;
    if (ElementAt(store.Pointer, counter, start) is not { } c
        || ElementAt(left.Pointer, counter, start) is not { } a
        || ElementAt(right.Pointer, counter, start) is not { } b)
      return null;
    // the counter itself may be read after the loop, and nothing else from the header
    if (compare.Users.Any(user => !ReferenceEquals(user, branch)))
      return null;

    return new(preheader, body, exit, counter, trip, last + 1, kernel, c, a, b);
  }

  private sealed record CheckedLoop(IrBasicBlock Preheader, IrBasicBlock Header, IrBasicBlock Exit,
    long Trip, string Kernel, Element C, Element A, Element B);

  /// <summary>
  /// The <c>$ERROR OVERFLOW</c> shape: header, a body that computes <c>a OP b</c> and its signed-overflow
  /// test, a trap block that raises Error 6 and rejoins, and a latch that stores and steps.
  /// </summary>
  private static CheckedLoop? MatchChecked(IrFunction function, IrBasicBlock header) {
    if (header.Terminator is not IrCondBr { Condition: IrCmp compare } branch
        || header.Instructions.Count != 3
        || header.Instructions[0] is not IrPhi counter || !ReferenceEquals(header.Instructions[1], compare)
        || counter.Type is not { IsInteger: true, Bits: 16 }
        || !ReferenceEquals(compare.Lhs, counter) || compare.Rhs is not IrConstantInt { Value: var limit }
        || compare.Pred is not (IrCmpPred.Sle or IrCmpPred.Slt))
      return null;
    var body = branch.IfTrue;
    var exit = branch.IfFalse;
    if (body.Terminator is not IrCondBr { Condition: IrCmp overflow } test
        || body.Predecessors.Count() != 1)
      return null;
    var trap = test.IfTrue;
    var latch = test.IfFalse;
    if (trap.Instructions is not [IrCall { Callee: IrFunction { Name: "rt_error" } } raise, IrBr { Target: var rejoin }]
        || raise.Args.ToArray() is not [IrConstantInt { Value: 6 }] || !ReferenceEquals(rejoin, latch)
        || latch.Terminator is not IrBr { Target: var back } || !ReferenceEquals(back, header)
        || latch.Instructions.Count != 3 || latch.Instructions[0] is not IrStore store
        || latch.Instructions[1] is not IrBinary { Op: IrBinaryOp.Add, Rhs: IrConstantInt { Value: 1 } } next
        || !ReferenceEquals(next.Lhs, counter))
      return null;
    var blocks = new[] { header, body, trap, latch };
    var addressTaken = function.AddressTakenBlocks();
    if (blocks.Any(addressTaken.Contains) || blocks.Distinct().Count() != 4 || blocks.Contains(exit))
      return null;
    var predecessors = header.Predecessors.ToList();
    if (predecessors.Count != 2 || !predecessors.Contains(latch))
      return null;
    var preheader = predecessors.Single(p => !ReferenceEquals(p, latch));
    if (preheader.Terminator is not IrBr || counter.IncomingFrom(preheader) is not IrConstantInt { Value: var start }
        || !ReferenceEquals(counter.IncomingFrom(latch), next))
      return null;
    // the counter's final value would need a phi at the exit; only a loop whose counter dies qualifies
    if (counter.Users.Any(user => !blocks.Contains(user.Parent!)))
      return null;

    var last = compare.Pred == IrCmpPred.Sle ? limit : limit - 1;
    if (last >= short.MaxValue)
      return null;
    var trip = last - start + 1;
    if (trip <= 0 || trip > short.MaxValue)
      return null;

    if (store.Value is not IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Sub, Type: { IsInteger: true, Bits: 16 } } operation
        || !ReferenceEquals(operation.Parent, body)
        || operation.Lhs is not IrLoad left || operation.Rhs is not IrLoad right
        || !IsSignedOverflowTest(overflow, operation))
      return null;
    foreach (var instruction in body.Instructions) {
      if (instruction is IrCondBr)
        continue;
      if (instruction is not (IrLoad or IrBinary or IrCast or IrGep or IrCmp))
        return null;
      if (instruction.Users.Any(user => !ReferenceEquals(user.Parent, body) && !ReferenceEquals(user, store)
            && !ReferenceEquals(user.Parent, latch)))
        return null;
    }
    if (body.Instructions.OfType<IrLoad>().Any(load => !ReferenceEquals(load, left) && !ReferenceEquals(load, right)))
      return null;
    if (ElementAt(store.Pointer, counter, start) is not { } c
        || ElementAt(left.Pointer, counter, start) is not { } a
        || ElementAt(right.Pointer, counter, start) is not { } b)
      return null;
    var kernel = operation.Op == IrBinaryOp.Add ? "rt_packed16_add_checked" : "rt_packed16_sub_checked";
    return new(preheader, header, exit, trip, kernel, c, a, b);
  }

  /// <summary>
  /// Whether <paramref name="test"/> is exactly the lowering's signed-overflow test of
  /// <paramref name="operation"/>: <c>(NOT (a XOR b) AND (s XOR a)) &lt; 0</c> for a sum,
  /// <c>((a XOR b) AND (a XOR s)) &lt; 0</c> for a difference. Anything else is a check the preflight
  /// does not model, and the loop keeps it.
  /// </summary>
  private static bool IsSignedOverflowTest(IrCmp test, IrBinary operation) {
    if (test is not { Pred: IrCmpPred.Slt, Rhs: IrConstantInt { Value: 0 }, Lhs: IrBinary { Op: IrBinaryOp.And } both })
      return false;
    var (a, b, s) = (operation.Lhs, operation.Rhs, (IrValue)operation);
    bool IsXor(IrValue v, IrValue x, IrValue y) => v is IrBinary { Op: IrBinaryOp.Xor } xor
      && ((ReferenceEquals(xor.Lhs, x) && ReferenceEquals(xor.Rhs, y)) || (ReferenceEquals(xor.Lhs, y) && ReferenceEquals(xor.Rhs, x)));
    bool IsNotXor(IrValue v, IrValue x, IrValue y) => v is IrBinary { Op: IrBinaryOp.Xor, Rhs: IrConstantInt { Value: -1 } } not
      && IsXor(not.Lhs, x, y);
    return operation.Op == IrBinaryOp.Add
      ? (IsNotXor(both.Lhs, a, b) && IsXor(both.Rhs, s, a)) || (IsNotXor(both.Rhs, a, b) && IsXor(both.Lhs, s, a))
      : (IsXor(both.Lhs, a, b) && IsXor(both.Rhs, a, s)) || (IsXor(both.Rhs, a, b) && IsXor(both.Lhs, a, s));
  }

  /// <summary>Puts the loop behind the checked kernel: 0 means done packed, anything else runs the loop.</summary>
  private static bool RewriteChecked(IrModule module, CheckedLoop loop) {
    var kernel = module.FindFunction(loop.Kernel) ?? module.AddFunction(new IrFunction(loop.Kernel, IrType.I16,
      [new IrArgument(IrType.Ptr, 0, "c"), new IrArgument(IrType.Ptr, 1, "a"),
       new IrArgument(IrType.Ptr, 2, "b"), new IrArgument(IrType.I16, 3, "n")]));
    var preheader = loop.Preheader;
    var jump = preheader.Terminator!;
    IrValue Start(IrValue basePtr, long offset)
      => preheader.InsertBefore(new IrGep(basePtr, new IrConstantInt(IrType.I32, offset)), jump);
    var destination = Start(loop.C.Base, loop.C.Offset);
    var left = Start(loop.A.Base, loop.A.Offset);
    var right = Start(loop.B.Base, loop.B.Offset);
    var answer = preheader.InsertBefore(new IrCall(IrType.I16, kernel,
      [destination, left, right, new IrConstantInt(IrType.I16, loop.Trip)]), jump);
    var done = preheader.InsertBefore(new IrCmp(IrCmpPred.Eq, answer, new IrConstantInt(IrType.I16, 0)), jump);
    // the exit gains the preheader as a predecessor, carrying whatever the header would have handed it
    foreach (var phi in loop.Exit.Phis.ToList())
      if (phi.IncomingFrom(loop.Header) is { } incoming)
        phi.AddIncoming(incoming, preheader);
    jump.EraseFromParent();
    preheader.Append(new IrCondBr(done, loop.Exit, loop.Header));
    return true;
  }

  private static string? KernelFor(IrBinaryOp op) => op switch {
    IrBinaryOp.Add => "rt_packed16_add",
    IrBinaryOp.Sub => "rt_packed16_sub",
    IrBinaryOp.And => "rt_packed16_and",
    IrBinaryOp.Or => "rt_packed16_or",
    IrBinaryOp.Xor => "rt_packed16_xor",
    IrBinaryOp.Mul => "rt_packed16_mul",
    _ => null,
  };

  /// <summary>
  /// The base and first byte offset of an element address that steps two bytes per iteration: a
  /// byte-offset GEP off an alloca or a global whose offset is an affine function of the counter.
  /// </summary>
  private static Element? ElementAt(IrValue pointer, IrPhi counter, long start) {
    if (pointer is not IrGep { ElementType: null } gep || gep.BasePtr is not (IrAlloca or IrGlobalVariable)
        || gep.BasePtr.Type.IsFarPointer)
      return null;
    if (Evaluate(gep.ByteOffset, counter, start) is not { } first
        || Evaluate(gep.ByteOffset, counter, start + 1) is not { } second
        || Evaluate(gep.ByteOffset, counter, start + 2) is not { } third
        || second - first != 2 || third - second != 2)
      return null;
    return new(gep.BasePtr, first);
  }

  /// <summary>The value of a pure offset expression at a given counter value, or null when it is not one.</summary>
  private static long? Evaluate(IrValue value, IrPhi counter, long at) => value switch {
    _ when ReferenceEquals(value, counter) => at,
    IrConstantInt constant => constant.Value,
    IrCast { Op: IrCastOp.SExt or IrCastOp.ZExt or IrCastOp.Trunc } cast => Evaluate(cast.Value, counter, at),
    IrBinary binary when Evaluate(binary.Lhs, counter, at) is { } l && Evaluate(binary.Rhs, counter, at) is { } r
      => binary.Op switch {
        IrBinaryOp.Add => l + r,
        IrBinaryOp.Sub => l - r,
        IrBinaryOp.Mul => l * r,
        IrBinaryOp.Shl when r is >= 0 and < 16 => l << (int)r,
        _ => null,
      },
    _ => null,
  };
}
