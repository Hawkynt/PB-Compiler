using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// Scales AX by an element size or stride. A power of two becomes shifts, which is what a hand
  /// written version would do: <c>IMUL AX, AX, imm</c> costs about 21 cycles where <c>SHL AX,1</c>
  /// costs two, and it is an 80186 instruction - so on the 8086 this is not merely slower but
  /// outside the declared target. Anything else keeps the multiply (the faithful path keeps it
  /// unconditionally, so the golden gate is untouched).
  /// </summary>
  private void EmitIndexScale(int factor) {
    var asm = this._asm;
    if (this.Optimize && factor > 0 && (factor & (factor - 1)) == 0) {
      this.EmitShiftLeft(Reg.AX, System.Numerics.BitOperations.TrailingZeroCount((uint)factor));
      return;
    }
    asm.Imul(Reg.AX, Reg.AX, factor);
  }

  // Dynamic array descriptor layout (in the data segment, ArrayType.Size = 8 + rank*4):
  //   +0 segment (0 = unallocated)   +2 data offset
  //   +4 element size                +6 rank
  //   +8 + d*4: lower bound (word), extent (word) per dimension

  private void EmitDim(DimStmt dim) {
    var skipZero = this._coveredArrayDims?.Contains(dim) == true;   // O0068: fill loop covers it
    foreach (var v in dim.Variables) {
      if (v.ArrayBounds == null)
        continue;
      var symbol = this.LookupVariable(v.Name, v.Suffix, isArray: true) ?? this.LookupVariable(v.Name, TypeSuffix.None, isArray: true);
      if (symbol?.Type is not ArrayType { IsDynamic: true })
        continue;   // static arrays and scalars are laid out at compile time
      this.EmitClassedAllocation(symbol, v.ArrayBounds, dim.AtAddress, dim.Position, skipZero);
    }
  }

  /// <summary>
  /// O0068: the DIM / REDIM statements whose array a directly-following FOR provably fills in full
  /// before anything reads it, so the allocation can skip its zero-fill. Rebuilt per body; null
  /// clears it.
  /// </summary>
  private HashSet<Statement>? _coveredArrayDims;

  /// <summary>Marks each covered <c>DIM/REDIM a(lo TO hi) : FOR i = lo TO hi : a(i) = expr : NEXT</c> pair (O0068).</summary>
  private void PrepareArrayFill(IReadOnlyList<Statement> body) {
    this._coveredArrayDims = null;
    // an error handler could run mid-fill (a trapping fill expression) and observe the array before
    // the loop finishes - where the genuine zero-fill shows 0 and the elided one shows garbage
    if (!this.Optimize || ContainsErrorHandling(body))
      return;
    this.ScanArrayFill(body);
  }

  private void ScanArrayFill(IReadOnlyList<Statement> body) {
    for (var i = 0; i + 1 < body.Count; ++i) {
      if (body[i + 1] is not ForStmt loop)
        continue;
      // DIM (conventional) or REDIM without PRESERVE - both allocate a fresh zero-filled block
      var decl = body[i] switch {
        DimStmt { Class: ArrayClass.Default, Variables: [{ } d] } => d,
        RedimStmt { Preserve: false, Variables: [{ } d] } => d,
        _ => null,
      };
      if (decl != null && this.IsCoveredArrayFill(decl, loop))
        (this._coveredArrayDims ??= new(ReferenceEqualityComparer.Instance)).Add(body[i]);
    }
    foreach (var s in body)
      foreach (var block in ChildStatementBlocks(s))
        this.ScanArrayFill(block);
  }

  /// <summary>
  /// True when <paramref name="loop"/> writes every element of the single conventional dynamic
  /// rank-1 non-string array <paramref name="decl"/> declares, exactly once, before any read: the
  /// counter spans the array's explicit bounds with step 1, the body is the lone assignment
  /// <c>a(i) = expr</c> with <c>i</c> the subscript, and <c>expr</c> neither reads <c>a</c> nor calls
  /// anything (so it cannot observe a not-yet-written element). Then the zero-fill is dead.
  /// </summary>
  private bool IsCoveredArrayFill(VariableDecl decl, ForStmt loop) {
    if (decl is not { ArrayBounds: { Count: 1 } bounds })
      return false;
    var (lower, upper) = bounds[0];
    if (lower == null)               // require an explicit lower bound (side-steps OPTION BASE)
      return false;

    if (loop.Variable is not NameExpr ctr
        || !model.VariableBindings.TryGetValue(ctr, out var ctrSym))
      return false;
    if (loop.Step != null && this.OptFolder.TryFold(loop.Step) is not { Integer: 1 })
      return false;
    if (!this.SameDivOperand(loop.From, lower) || !this.SameDivOperand(loop.To, upper))
      return false;   // the counter must span exactly [lower, upper]

    // exactly one top-level array-element write (the coverage store); it must not sit inside an IF
    // or nested loop, or some pass could skip element i. Any other body statement is scanned below.
    AssignStmt? coverWrite = null;
    foreach (var st in loop.Body) {
      if (st is AssignStmt { Target: CallOrIndexExpr } arrayWrite) {
        if (coverWrite != null)
          return false;   // two array writes - keep the coverage argument simple
        coverWrite = arrayWrite;
      }
    }
    if (coverWrite is not { Target: CallOrIndexExpr target, Value: { } fill })
      return false;
    if (!model.VariableBindings.TryGetValue(target, out var arrSym)
        || arrSym.Type is not ArrayType { IsDynamic: true, Rank: 1, Element: { } elem }
        || arrSym.ArrayClass != ArrayClass.Default)
      return false;
    // every other statement must be a-free scalar work (no read/write of the array, no call, no
    // control flow), so nothing observes a half-filled array and the coverage write always runs
    foreach (var st in loop.Body)
      if (!ReferenceEquals(st, coverWrite) && !this.IsArrayFreeScalarStatement(st, arrSym))
        return false;
    if (elem is StringType or FlexType || EmbedsStringHandle(elem))
      return false;   // a garbage embedded string handle would corrupt the string heap
    if (!decl.Name.Equals(arrSym.Name, StringComparison.OrdinalIgnoreCase))
      return false;   // the DIM declares this very array (one symbol per name in scope)
    if (target.Arguments is not [NameExpr sub]
        || !model.VariableBindings.TryGetValue(sub, out var subSym) || !ReferenceEquals(subSym, ctrSym))
      return false;   // subscripted by exactly the counter, so element i is the one written on pass i
    return this.IsSafeFillValue(fill, arrSym);
  }

  /// <summary>
  /// An auxiliary fill-loop statement that touches no array and calls nothing: a scalar assignment
  /// or incr/decr whose value is itself array-free and call-free (<see cref="IsSafeFillValue"/> with
  /// no other-array reads permitted). So it can neither observe the target's half-filled state nor
  /// have a side effect that reads it, and (being a plain store) it always runs, never skipping the
  /// coverage write. Anything else - a print, a call, control flow, an array write - declines.
  /// </summary>
  private bool IsArrayFreeScalarStatement(Statement st, VariableSymbol targetArr) {
    switch (st) {
      case AssignStmt { Target: NameExpr t, Value: { } v }
          when model.VariableBindings.TryGetValue(t, out var ts) && ts.Type is ScalarType:
        return this.IsSafeFillValue(v, targetArr) && !this.ReadsAnyArray(v);
      case IncrDecrStmt { Target: NameExpr t } id
          when model.VariableBindings.TryGetValue(t, out var ts) && ts.Type is ScalarType:
        return id.Amount == null || (this.IsSafeFillValue(id.Amount, targetArr) && !this.ReadsAnyArray(id.Amount));
      default:
        return false;
    }
  }

  /// <summary>True if the expression reads any array element - auxiliary statements must be wholly array-free (even of other arrays).</summary>
  private bool ReadsAnyArray(Expression e) => e switch {
    CallOrIndexExpr c => model.VariableBindings.TryGetValue(c, out var s) && s.Type is ArrayType || c.Arguments.Any(this.ReadsAnyArray),
    UnaryExpr u => this.ReadsAnyArray(u.Operand),
    BinaryExpr b => this.ReadsAnyArray(b.Left) || this.ReadsAnyArray(b.Right),
    _ => false,
  };

  /// <summary>
  /// A fill value that neither reads the target array <paramref name="targetArr"/> (so it cannot
  /// observe a not-yet-written element) nor calls anything (which could read the array elsewhere or
  /// have a side effect): the counter, constants, plain scalars, non-trapping arithmetic, and an
  /// element read of a DIFFERENT array (a copy like <c>a(i) = b(i)</c>, distinct storage - no alias).
  /// </summary>
  private bool IsSafeFillValue(Expression e, VariableSymbol targetArr) => e switch {
    IntegerLiteralExpr or FloatLiteralExpr or NamedConstantExpr => true,
    NameExpr n => !model.IntrinsicBindings.ContainsKey(n)
      && model.VariableBindings.TryGetValue(n, out var s) && s.Type is ScalarType,
    UnaryExpr { Op: UnaryOp.Negate or UnaryOp.Not } u => this.IsSafeFillValue(u.Operand, targetArr),
    BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
        or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor
        or BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith } b
      => this.IsSafeFillValue(b.Left, targetArr) && this.IsSafeFillValue(b.Right, targetArr),
    // an element read of another array (not a function call, not the target): its distinct storage
    // cannot alias the target, and its own subscript must be equally safe
    CallOrIndexExpr c when model.VariableBindings.TryGetValue(c, out var other)
      && other.Type is ArrayType && !ReferenceEquals(other, targetArr)
      => c.Arguments.All(a => this.IsSafeFillValue(a, targetArr)),
    _ => false,
  };

  private void EmitRedim(RedimStmt redim) {
    var skipZero = this._coveredArrayDims?.Contains(redim) == true;   // O0068: fill loop covers it
    foreach (var v in redim.Variables) {
      var symbol = this.LookupVariable(v.Name, v.Suffix, isArray: true) ?? this.LookupVariable(v.Name, TypeSuffix.None, isArray: true);
      if (symbol?.Type is not ArrayType { IsDynamic: true } || v.ArrayBounds == null) {
        this.Unsupported(redim);
        continue;
      }
      if (redim.Preserve) {
        this.EmitRedimPreserve(symbol, v.ArrayBounds, redim.Position);
        continue;
      }
      this.EmitClassedAllocation(symbol, v.ArrayBounds, null, redim.Position, skipZero);
    }
  }

  /// <summary>Dispatches DIM/REDIM allocation by array class (conventional / HUGE / VIRTUAL / ABSOLUTE).</summary>
  private void EmitClassedAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, Expression? atAddress, SourcePosition position, bool skipZero = false) {
    switch (symbol.ArrayClass) {
      case ArrayClass.Huge:
        this.EmitHugeAllocation(symbol, bounds, position);
        break;
      // PB 3.6 EMS/XMS arrays: EMS uses the existing EMS-paged allocator; XMS is
      // routed through it too as a working stand-in until the optimizer instance
      // adds a true XMS-backed runtime (observably identical - it is just storage).
      case ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms:
        this.EmitVirtualAllocation(symbol, bounds, position);
        break;
      case ArrayClass.Absolute:
        this.EmitAbsoluteMapping(symbol, bounds, atAddress, position);
        break;
      default:
        this.EmitArrayAllocation(symbol, bounds, position, skipZeroFill: skipZero);
        break;
    }
  }

  // HUGE/VIRTUAL descriptor layout (rank 1, LONG bounds; 20-byte slot):
  //   +0 segment (HUGE: DOS 48h block; VIRTUAL: EMS page frame)
  //   +2 offset (0)    +4 element size    +6 rank (1)
  //   +8 lower (dword) +12 extent (dword)
  //   +16 EMS handle (VIRTUAL)            +18 mapped logical page cache
  internal const int HvDescriptorBytes = 20;

  /// <summary>Evaluates the rank-1 LONG bounds into the descriptor; leaves the byte count in DX:AX.</summary>
  private bool TryEmitLongBoundsAndByteCount(Label descriptor, VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position) {
    var asm = this._asm;
    var arrayType = (ArrayType)symbol.Type;
    var elementSize = Math.Max(arrayType.Element.Size, 1);
    if (bounds.Count != 1) {
      this.Unsupported(position, $"{symbol.ArrayClass} array {symbol.Name} with rank {bounds.Count} (only rank 1 is supported)");
      return false;
    }
    if (arrayType.Element is StringType or FlexType) {
      this.Unsupported(position, $"dynamic strings inside a {symbol.ArrayClass} array");
      return false;
    }

    asm.Mov(Mem.Word(descriptor, 4), elementSize);
    asm.Mov(Mem.Word(descriptor, 6), 1);

    var (lower, upper) = bounds[0];
    if (lower != null) {
      this.EmitExpression(lower);
      this.Coerce(model.TypeOf(lower), PbType.Long, lower);
    } else {
      asm.Xor(Reg.AX, Reg.AX);
      asm.Xor(Reg.DX, Reg.DX);
    }
    asm.Mov(Mem.Word(descriptor, 8), Reg.AX);
    asm.Mov(Mem.Word(descriptor, 10), Reg.DX);

    this.EmitExpression(upper);
    this.Coerce(model.TypeOf(upper), PbType.Long, upper);
    asm.Sub(Reg.AX, Mem.Word(descriptor, 8));
    asm.Sbb(Reg.DX, Mem.Word(descriptor, 10));
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.Mov(Mem.Word(descriptor, 12), Reg.AX);
    asm.Mov(Mem.Word(descriptor, 14), Reg.DX);

    // byte count = extent * element size
    asm.Mov(Reg.BX, elementSize);
    asm.Xor(Reg.CX, Reg.CX);
    asm.Call(this._rt.LongMul);
    return true;
  }

  /// <summary>HUGE: conventional memory from DOS 48h, segment-stepping element access.</summary>
  private void EmitHugeAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position) {
    var asm = this._asm;
    var descriptor = this.SlotOf(symbol);

    asm.Mov(Reg.AX, Mem.Word(descriptor));        // release a previous allocation
    asm.Call(this._rt.HugeFree);
    asm.Mov(Mem.Word(descriptor), (Imm)0);

    if (!this.TryEmitLongBoundsAndByteCount(descriptor, symbol, bounds, position))
      return;

    asm.Push(Reg.DX);                             // keep the byte count for zeroing
    asm.Push(Reg.AX);
    asm.Call(this._rt.HugeAlloc);                 // DX:AX bytes -> AX segment
    asm.Mov(Mem.Word(descriptor), Reg.AX);
    asm.Mov(Mem.Word(descriptor, 2), (Imm)0);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Call(this._rt.HugeZero);                  // AX = segment, CX:BX = bytes
  }

  /// <summary>VIRTUAL: EMS-backed storage (int 67h), page-mapped element access.</summary>
  private void EmitVirtualAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position) {
    var asm = this._asm;
    var descriptor = this.SlotOf(symbol);

    asm.Mov(Reg.DX, Mem.Word(descriptor, 16));    // release a previous allocation
    asm.Call(this._rt.EmsFree);
    asm.Mov(Mem.Word(descriptor, 16), (Imm)0);

    if (!this.TryEmitLongBoundsAndByteCount(descriptor, symbol, bounds, position))
      return;

    asm.Push(Reg.DX);
    asm.Push(Reg.AX);
    asm.Call(this._rt.EmsAlloc);                  // DX:AX bytes -> AX handle
    asm.Mov(Mem.Word(descriptor, 16), Reg.AX);
    asm.Call(this._rt.EmsFrame);
    asm.Mov(Mem.Word(descriptor), Reg.AX);
    asm.Mov(Mem.Word(descriptor, 2), (Imm)0);
    asm.Mov(Mem.Word(descriptor, 18), 0xFFFF);    // mapped-page cache: invalid
    asm.Mov(Reg.DX, Mem.Word(descriptor, 16));
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Call(this._rt.EmsZero);                   // DX = handle, CX:BX = bytes
  }

  /// <summary>ABSOLUTE: zero-copy mapping of an existing segment (DIM x(...) AT seg).</summary>
  private void EmitAbsoluteMapping(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, Expression? atAddress, SourcePosition position) {
    var asm = this._asm;
    var arrayType = (ArrayType)symbol.Type;
    var descriptor = this.SlotOf(symbol);
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    asm.Mov(Mem.Word(descriptor, 4), elementSize);
    asm.Mov(Mem.Word(descriptor, 6), bounds.Count);
    for (var d = 0; d < bounds.Count; ++d) {
      var (lower, upper) = bounds[d];
      if (lower != null) {
        this.EmitInt16Argument(lower);
        asm.Mov(Mem.Word(descriptor, 8 + d * 4), Reg.AX);
      } else
        asm.Mov(Mem.Word(descriptor, 8 + d * 4), (Imm)0);
      this.EmitInt16Argument(upper);
      asm.Sub(Reg.AX, Mem.Word(descriptor, 8 + d * 4));
      asm.Inc(Reg.AX);
      asm.Mov(Mem.Word(descriptor, 8 + d * 4 + 2), Reg.AX);
    }

    if (atAddress == null) {
      this.Unsupported(position, $"ABSOLUTE array {symbol.Name} without an AT segment");
      return;
    }
    this.EmitInt16Argument(atAddress);            // segment value; data starts at seg:0
    asm.Mov(Mem.Word(descriptor), Reg.AX);
    asm.Mov(Mem.Word(descriptor, 2), (Imm)0);
  }

  /// <summary>
  /// REDIM PRESERVE (conventional dynamic arrays; the spec allows changing the
  /// outermost bound only - the contents prefix carries over byte-for-byte).
  /// The old block stays in the bump allocator (documented leak).
  /// </summary>
  private void EmitRedimPreserve(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position) {
    var asm = this._asm;
    var arrayType = (ArrayType)symbol.Type;
    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute or ArrayClass.Ems or ArrayClass.Xms) {
      this.Unsupported(position, $"REDIM PRESERVE on {symbol.ArrayClass} arrays");
      return;
    }
    var descriptor = this.SlotOf(symbol);
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    var oldBytes = this.AllocTemp(2);
    var oldOffset = this.AllocTemp(2);

    // old byte count (0 when never allocated)
    var unallocated = asm.DefineLabel();
    var measured = asm.DefineLabel();
    asm.Cmp(Mem.Word(descriptor), (Imm)0);
    asm.Je(unallocated);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 8 + 2));
    for (var d = 1; d < arrayType.Rank; ++d)
      asm.Imul(Reg.AX, Mem.Word(descriptor, 8 + d * 4 + 2));
    this.EmitIndexScale(elementSize);
    asm.Jmp(measured);
    asm.MarkLabel(unallocated);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(measured);
    asm.Mov(oldBytes, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 2));
    asm.Mov(oldOffset, Reg.AX);

    this.EmitArrayAllocation(symbol, bounds, position, reclaimOld: false);

    // copy min(old, new) bytes inside the array heap segment
    var copyDone = asm.DefineLabel();
    var sizeOk = asm.DefineLabel();
    asm.Mov(Reg.CX, oldBytes);
    asm.Jcxz(copyDone);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 8 + 2)); // new byte count
    for (var d = 1; d < arrayType.Rank; ++d)
      asm.Imul(Reg.AX, Mem.Word(descriptor, 8 + d * 4 + 2));
    this.EmitIndexScale(elementSize);
    asm.Cmp(Reg.CX, Reg.AX);
    asm.Jbe(sizeOk);
    asm.Mov(Reg.CX, Reg.AX);
    asm.MarkLabel(sizeOk);
    asm.Mov(Reg.SI, oldOffset);
    asm.Mov(Reg.DI, Mem.Word(descriptor, 2));
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_arrseg")));
    asm.Push(Reg.DS);
    asm.Mov(Reg.AX, Reg.ES);
    asm.Mov(Reg.DS, Reg.AX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
    asm.MarkLabel(copyDone);

    this.ReleaseTemp(2);
    this.ReleaseTemp(2);
  }

  /// <summary>Fills the descriptor and allocates zeroed storage from the far array heap.</summary>
  private void EmitArrayAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position, bool reclaimOld = true, bool skipZeroFill = false) {
    var asm = this._asm;
    var arrayType = (ArrayType)symbol.Type;
    var descriptor = this.SlotOf(symbol);
    var elementSize = Math.Max(arrayType.Element.Size, 1);
    _ = position;

    if (reclaimOld)
      this.EmitReclaimArrayBlock(descriptor, arrayType);

    asm.Mov(Mem.Word(descriptor, 4), elementSize);
    asm.Mov(Mem.Word(descriptor, 6), bounds.Count); // runtime rank follows the executing DIM/REDIM

    for (var d = 0; d < bounds.Count; ++d) {
      var (lower, upper) = bounds[d];
      if (lower != null) {
        this.EmitInt16Argument(lower);
        asm.Mov(Mem.Word(descriptor, 8 + d * 4), Reg.AX);
      } else
        asm.Mov(Mem.Word(descriptor, 8 + d * 4), (Imm)0);
      this.EmitInt16Argument(upper);
      asm.Sub(Reg.AX, Mem.Word(descriptor, 8 + d * 4));
      asm.Inc(Reg.AX);
      asm.Mov(Mem.Word(descriptor, 8 + d * 4 + 2), Reg.AX);
    }

    // total elements (16-bit product) * element size -> DX:AX bytes
    asm.Mov(Reg.AX, Mem.Word(descriptor, 8 + 2));
    for (var d = 1; d < bounds.Count; ++d)
      asm.Imul(Reg.AX, Mem.Word(descriptor, 8 + d * 4 + 2));
    asm.Mov(Reg.CX, elementSize);
    asm.Mul(Reg.CX);
    asm.Call(skipZeroFill ? this._rt.ArrAllocNoZero : this._rt.ArrAlloc);   // O0068: elide the fill when the covering loop follows
    asm.Mov(Mem.Word(descriptor, 2), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(this._asm.Lbl("rt_arrseg")));
    asm.Mov(Mem.Word(descriptor), Reg.AX);
  }

  /// <summary>
  /// Gives the array's current block back to the bump allocator when it is the
  /// most recent allocation (offset + bytes == top). Skips unallocated arrays.
  /// </summary>
  private void EmitReclaimArrayBlock(Label descriptor, ArrayType arrayType) {
    var asm = this._asm;
    var skip = asm.DefineLabel();
    asm.Cmp(Mem.Word(descriptor), (Imm)0);
    asm.Je(skip);
    asm.Cmp(Mem.Word(descriptor, 6), arrayType.Rank);  // rank changed at runtime: size math below would lie
    asm.Jne(skip);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 8 + 2));      // element count = extent product
    for (var d = 1; d < arrayType.Rank; ++d)
      asm.Imul(Reg.AX, Mem.Word(descriptor, 8 + d * 4 + 2));
    asm.Mov(Reg.CX, Mem.Word(descriptor, 4));          // * element size
    asm.Mul(Reg.CX);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jnz(skip);                                     // >64K would be bogus - leave it
    asm.Mov(Reg.CX, Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 2));
    asm.Call(this._rt.ArrFree);
    asm.MarkLabel(skip);
  }

  private void EmitErase(EraseStmt erase) {
    var asm = this._asm;
    foreach (var array in erase.Arrays) {
      if (!model.VariableBindings.TryGetValue(array, out var symbol) || symbol.Type is not ArrayType arrayType) {
        this.Unsupported(erase);
        continue;
      }
      var slot = this.SlotOf(symbol);
      if (symbol.ArrayClass == ArrayClass.Huge) {
        asm.Mov(Reg.AX, Mem.Word(slot));
        asm.Call(this._rt.HugeFree);
        asm.Mov(Mem.Word(slot), (Imm)0);
        continue;
      }
      if (symbol.ArrayClass == ArrayClass.Virtual) {
        asm.Mov(Reg.DX, Mem.Word(slot, 16));
        asm.Call(this._rt.EmsFree);
        asm.Mov(Mem.Word(slot, 16), (Imm)0);
        asm.Mov(Mem.Word(slot), (Imm)0);
        continue;
      }
      if (symbol.ArrayClass == ArrayClass.Absolute) {  // unmap only - the memory is not ours
        asm.Mov(Mem.Word(slot), (Imm)0);
        continue;
      }
      if (arrayType.IsDynamic) {
        this.EmitReclaimArrayBlock(slot, arrayType);   // rolls back when topmost; interleaved frees leak
        asm.Mov(Mem.Word(slot), (Imm)0);
        continue;
      }
      // static arrays: zero-fill in place
      asm.Push(Reg.DS);
      asm.Pop(Reg.ES);
      asm.Mov(Reg.DI, Imm.OffsetOf(slot));
      // pb36 C1 ($CPU 80386): zero the (word-rounded) block DWORD-wide with REP STOSD,
      // a trailing STOSW for the odd leftover word - same byte count as the REP STOSW,
      // about twice as fast. The result (all zeros) is identical.
      var fillBytes = ((arrayType.Size + 1) / 2) * 2;   // exactly what REP STOSW covers
      if (this.Optimize && this.Has32BitCpu && fillBytes >= 8) {
        asm.Xor(Reg.EAX, Reg.EAX);
        asm.Mov(Reg.CX, (Imm)(fillBytes / 4));
        asm.Rep();
        asm.Stosd();
        if (fillBytes % 4 != 0)
          asm.Stosw();
      } else {
        asm.Mov(Reg.CX, (arrayType.Size + 1) / 2);
        asm.Xor(Reg.AX, Reg.AX);
        asm.Rep();
        asm.Stosw();
      }
    }
  }

  /// <summary>UBOUND/LBOUND: constants for static arrays, descriptor reads for dynamic ones.</summary>
  private void EmitBound(Expression call, IReadOnlyList<Expression> args, bool isUpper) {
    var asm = this._asm;
    var arrayArg = args[0];
    if (!model.VariableBindings.TryGetValue(arrayArg, out var symbol) || symbol.Type is not ArrayType arrayType) {
      this.Unsupported(call, "UBOUND/LBOUND argument");
      return;
    }

    var dimension = 1;
    if (args.Count > 1) {
      if (args[1] is IntegerLiteralExpr d)
        dimension = (int)d.Value;
      else {
        this.Unsupported(call, "non-constant UBOUND/LBOUND dimension");
        return;
      }
    }
    if (dimension < 1 || dimension > arrayType.Rank) {
      this.Unsupported(call, "UBOUND/LBOUND dimension out of range");
      return;
    }

    if (arrayType.StaticBounds is { } staticBounds) {
      var (lower, upper) = staticBounds[dimension - 1];
      asm.Mov(Reg.AX, isUpper ? upper : lower);
      asm.Cwd();
      return;
    }

    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms) {
      var hv = this.SlotOf(symbol);
      asm.Mov(Reg.AX, Mem.Word(hv, 8));
      asm.Mov(Reg.DX, Mem.Word(hv, 10));
      if (isUpper) {
        asm.Add(Reg.AX, Mem.Word(hv, 12));
        asm.Adc(Reg.DX, Mem.Word(hv, 14));
        asm.Sub(Reg.AX, 1);
        asm.Sbb(Reg.DX, (Imm)0);
      }
      return;
    }

    var descriptor = this.DescriptorAccessorOf(symbol);
    asm.Mov(Reg.AX, descriptor(8 + (dimension - 1) * 4));
    if (isUpper) {
      asm.Add(Reg.AX, descriptor(8 + (dimension - 1) * 4 + 2));
      asm.Dec(Reg.AX);
    }
    asm.Cwd();
  }

  /// <summary>
  /// Descriptor cell accessor: direct data label for module/static arrays,
  /// SI-indirect through the [BP+n] pointer for array parameters (SI is
  /// reloaded on every access, so index evaluation may clobber it).
  /// </summary>
  private Func<int, Mem> DescriptorAccessorOf(VariableSymbol symbol) {
    if (symbol.Storage != VariableStorage.Parameter) {
      var label = this.SlotOf(symbol);
      return disp => Mem.Word(label, disp);
    }
    var asm = this._asm;
    return disp => {
      asm.Mov(Reg.SI, Mem.Word(Reg.BP, symbol.Offset));
      return Mem.Word(Reg.SI, disp);
    };
  }

  /// <summary>
  /// Address of an array element: row-major linear index scaled by the element
  /// size. Static arrays resolve to [BX + label - bias]; dynamic arrays load ES
  /// from the descriptor and resolve to ES:[BX + offset].
  /// </summary>
  /// <summary>
  /// pb36 O6: folds an all-constant subscript list into the flattened element index, so its
  /// address becomes a compile-time displacement. Every subscript must fold AND lie inside the
  /// declared bounds - an out-of-range constant keeps the ordinary path, which is where
  /// <c>$ERROR BOUNDS</c> raises Error 9 and where the unchecked 16-bit address arithmetic
  /// (which may wrap) is reproduced exactly.
  /// </summary>
  private bool TryFoldSubscripts(
    IReadOnlyList<Expression> indexes, IReadOnlyList<(int Lower, int Upper)> bounds, out int flat) {
    var strides = StridesOf(bounds);
    flat = 0;
    for (var d = 0; d < bounds.Count; ++d) {
      if (this.OptFolder.TryFold(indexes[d]) is not { Integer: { } index })
        return false;
      if (index < bounds[d].Lower || index > bounds[d].Upper)
        return false;
      flat += (int)index * strides[d];
    }
    return true;
  }

  /// <summary>The element stride of each dimension, row-major - the multipliers of the flattened index.</summary>
  private static int[] StridesOf(IReadOnlyList<(int Lower, int Upper)> bounds) {
    var strides = new int[bounds.Count];
    var stride = 1;
    for (var d = bounds.Count - 1; d >= 0; --d) {
      strides[d] = stride;
      stride *= bounds[d].Upper - bounds[d].Lower + 1;
    }
    return strides;
  }

  /// <summary>
  /// True when <paramref name="e"/> is a static-array element whose every subscript is a constant
  /// inside the bounds - so <see cref="EmitArrayElementPlace"/> answers with a bare displacement
  /// and emits nothing. Stores then need no PUSH/POP staging around the address.
  /// </summary>
  private bool FoldsToConstantElement(Expression e) {
    var (indexes, symbol) = e switch {
      CallOrIndexExpr call when model.VariableBindings.TryGetValue(call, out var array) => (call.Arguments, array),
      IndexExpr { Target: MemberExpr mt } ix when model.VariableBindings.TryGetValue(mt, out var dotted)
        && dotted.Type is ArrayType => (ix.Arguments, dotted),
      _ => (null, null),
    };
    return this.Optimize
      && indexes != null && symbol != null
      && symbol.ArrayClass is not (ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms)
      && symbol.Type is ArrayType { StaticBounds: { } bounds } arrayType
      && indexes.Count == arrayType.Rank
      && this.TryFoldSubscripts(indexes, bounds, out _);
  }

  private Place? EmitArrayElementPlace(IReadOnlyList<Expression> indexes, VariableSymbol symbol, Expression at) {
    var asm = this._asm;
    // dynamic arrays may be re-DIMed with a different rank (the descriptor is
    // runtime state) - only static arrays pin their rank at compile time
    if (symbol.Type is not ArrayType arrayType || (arrayType.StaticBounds != null && indexes.Count != arrayType.Rank)) {
      this.Unsupported(at, $"indexing {symbol.Name}");
      return null;
    }

    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms)
      return this.EmitHugeOrVirtualElementPlace(indexes, symbol, arrayType, at);   // EMS/XMS ride the paged (VIRTUAL) machinery
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    if (arrayType.StaticBounds is { } bounds) {
      // strides and the lower-bound bias fold at compile time
      var strides = StridesOf(bounds);
      var bias = 0;
      for (var d = 0; d < bounds.Count; ++d)
        bias += bounds[d].Lower * strides[d];

      // pb36 O6: every subscript is a compile-time constant, so the element's address is one too.
      // The whole scale-and-add sequence (MOV AX,k / SHL AX,1 / MOV BX,AX, plus a PUSH/POP pair
      // per extra dimension) collapses into the displacement of a direct memory operand - what
      // anyone writing this by hand would use, and it leaves BX free.
      if (this.Optimize && this.TryFoldSubscripts(indexes, bounds, out var flat)) {
        var offset = unchecked((short)((flat - bias) * elementSize));
        return symbol.ArrayClass == ArrayClass.Stack
          ? new(Mem.At(Reg.BP, symbol.Offset + offset), false)
          : new(Mem.At(this.SlotOf(symbol), offset), false);
      }

      for (var d = 0; d < bounds.Count; ++d) {
        this.EmitInt16Argument(indexes[d]);
        // pb36 O16: an index provably inside the static bounds can never raise Error 9 -
        // the check is dead and disappears. The proven range covers a compile-time constant,
        // a FOR counter, and an affine counter expression (counter +/- constant, e.g. a(i-1)).
        var provablyInRange = this.Optimize
          && this.IndexRangeOf(indexes[d]) is { } range
          && range.Lo >= bounds[d].Lower && range.Hi <= bounds[d].Upper;
        if (this.CheckBounds && !provablyInRange) { // $ERROR BOUNDS ON -> Error 9
          asm.Cmp(Reg.AX, bounds[d].Lower);
          this.EmitRaiseWhen(asm.Jge, 9);
          asm.Cmp(Reg.AX, bounds[d].Upper);
          this.EmitRaiseWhen(asm.Jle, 9);
        }
        if (strides[d] != 1)
          this.EmitIndexScale(strides[d]);
        if (d > 0) {
          asm.Pop(Reg.BX);
          asm.Add(Reg.AX, Reg.BX);
        }
        if (d < bounds.Count - 1)
          asm.Push(Reg.AX);
      }
      if (elementSize != 1)
        this.EmitIndexScale(elementSize);
      // pb36 STACK array: the data lives in the frame, so the element is [BP+DI+disp]
      // (BP pairs only with SI/DI in 16-bit addressing; DI is free here)
      if (symbol.ArrayClass == ArrayClass.Stack) {
        asm.Mov(Reg.DI, Reg.AX);
        return new(Mem.At(Reg.BP, Reg.DI, symbol.Offset - bias * elementSize), false);
      }
      asm.Mov(Reg.BX, Reg.AX);
      return new(Mem.At(Reg.BX, this.SlotOf(symbol), -bias * elementSize), false);
    }

    // dynamic (or parameter) arrays: Horner over the descriptor extents
    var descriptor = this.DescriptorAccessorOf(symbol);
    for (var d = 0; d < indexes.Count; ++d) {
      this.EmitInt16Argument(indexes[d]);
      asm.Sub(Reg.AX, descriptor(8 + d * 4));
      if (this.CheckBounds) { // $ERROR BOUNDS ON -> Error 9
        asm.Cmp(Reg.AX, descriptor(8 + d * 4 + 2));
        this.EmitRaiseWhen(asm.Jb, 9);
      }
      if (d > 0) {
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Imul(Reg.AX, descriptor(8 + d * 4 + 2));
        asm.Add(Reg.AX, Reg.CX);
      }
      if (d < indexes.Count - 1)
        asm.Push(Reg.AX);
    }
    if (elementSize != 1)
      this.EmitIndexScale(elementSize);
    asm.Add(Reg.AX, descriptor(2));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.ES, descriptor(0));
    return new(Mem.At(Reg.BX).Es(), true);
  }

  /// <summary>
  /// The address of an array's FIRST element, for the statements that take a whole array rather
  /// than one of its elements - <c>GET (0,0)-(3,3), spr%()</c>.
  ///
  /// <para>
  /// It is not the same question as indexing, which is why it is not spelled as an index of zero:
  /// the first element is at the array's lower bound, whatever that is, and its address is the
  /// array's own base with no bias to subtract and no bound to check. A static array is therefore
  /// its slot, and a dynamic one is the descriptor's segment:offset - the same pair the indexed
  /// path adds its computed offset to.
  /// </para>
  /// </summary>
  private Place? ArrayBasePlace(VariableSymbol symbol, Expression at) {
    var asm = this._asm;
    if (symbol.Type is not ArrayType) {
      this.Unsupported(at, $"{symbol.Name} is not an array");
      return null;
    }
    // The paged classes address through a window that is mapped per ACCESS, so "the base" is not an
    // address the caller could then walk - it is only meaningful one element at a time.
    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Ems or ArrayClass.Xms) {
      this.Unsupported(at, $"a whole {symbol.ArrayClass} array as a buffer");
      return null;
    }
    if (symbol.ArrayClass == ArrayClass.Stack)
      return new(Mem.At(Reg.BP, symbol.Offset), false);
    if (symbol.Type is ArrayType { StaticBounds: not null })
      return new(Mem.At(this.SlotOf(symbol), 0), false);

    var descriptor = this.DescriptorAccessorOf(symbol);
    asm.Mov(Reg.BX, descriptor(2));
    asm.Mov(Reg.ES, descriptor(0));
    return new(Mem.At(Reg.BX).Es(), true);
  }

  /// <summary>
  /// HUGE/VIRTUAL element place (rank 1, LONG index). HUGE normalizes the
  /// 32-bit byte offset into segment + 0..15 offset (huge pointer arithmetic);
  /// VIRTUAL maps the 16 KiB EMS page pair holding the element into the frame.
  /// </summary>
  private Place? EmitHugeOrVirtualElementPlace(IReadOnlyList<Expression> indexes, VariableSymbol symbol, ArrayType arrayType, Expression at) {
    var asm = this._asm;
    if (indexes.Count != 1) {
      this.Unsupported(at, $"{symbol.ArrayClass} array {symbol.Name} with {indexes.Count} indexes (only rank 1 is supported)");
      return null;
    }
    var descriptor = this.SlotOf(symbol);
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    // 32-bit byte offset = (index - lower) * element size
    this.EmitExpression(indexes[0]);
    this.Coerce(model.TypeOf(indexes[0]), PbType.Long, indexes[0]);
    asm.Sub(Reg.AX, Mem.Word(descriptor, 8));
    asm.Sbb(Reg.DX, Mem.Word(descriptor, 10));
    asm.Mov(Reg.BX, elementSize);
    asm.Xor(Reg.CX, Reg.CX);
    asm.Call(this._rt.LongMul);                   // DX:AX = byte offset

    if (symbol.ArrayClass == ArrayClass.Huge) {
      // ES = base segment + (offset >> 4); BX = offset & 15
      asm.Mov(Reg.BX, Reg.AX);
      asm.And(Reg.BX, (Imm)15);
      asm.Mov(Reg.CL, (Imm)4);
      asm.Shr(Reg.AX, Reg.CL);
      asm.Mov(Reg.CL, (Imm)12);
      asm.Shl(Reg.DX, Reg.CL);
      asm.Or(Reg.AX, Reg.DX);
      asm.Add(Reg.AX, Mem.Word(descriptor));
      asm.Mov(Reg.ES, Reg.AX);
      return new(Mem.At(Reg.BX).Es(), Far: true);
    }

    // VIRTUAL/EMS/XMS: logical page = offset >> 14; in-page offset = offset & 0x3FFF.
    // The mapping cache is GLOBAL (rt_ems_curhnd/curpage): all paged arrays share the one
    // page frame, so the check must cover both which handle and which page is in the window.
    var remap = asm.DefineLabel();
    var mapped = asm.DefineLabel();
    asm.Mov(Reg.BX, Reg.AX);
    asm.And(Reg.BX, 0x3FFF);
    asm.Mov(Reg.CL, (Imm)14);
    asm.Shr(Reg.AX, Reg.CL);
    asm.Mov(Reg.CL, (Imm)2);
    asm.Shl(Reg.DX, Reg.CL);
    asm.Or(Reg.AX, Reg.DX);                       // AX = logical page
    asm.Mov(Reg.DX, Mem.Word(descriptor, 16));    // EMS handle
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_ems_curpage")));
    asm.Jne(remap);
    asm.Cmp(Reg.DX, Mem.Word(asm.Lbl("rt_ems_curhnd")));
    asm.Je(mapped);
    asm.MarkLabel(remap);
    asm.Mov(Mem.Word(asm.Lbl("rt_ems_curpage")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_ems_curhnd")), Reg.DX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Call(this._rt.EmsMap2);                   // page pair -> physical 0/1
    asm.Pop(Reg.BX);
    asm.MarkLabel(mapped);
    asm.Mov(Reg.ES, Mem.Word(descriptor));        // page frame segment
    return new(Mem.At(Reg.BX).Es(), Far: true);
  }

  /// <summary>Element of a UDT array field, e.g. <c>ctx.Timers(i)</c>: base address plus a zero-based scaled index.</summary>
  private Place? EmitFieldArrayPlace(IndexExpr ix) {
    var asm = this._asm;
    if (ix.Arguments.Count != 1) {
      this.Unsupported(ix, "multi-dimensional UDT field array");
      return null;
    }
    if (this.EmitPlace(ix.Target) is not { } basePlace)
      return null;

    var elementSize = Math.Max(model.TypeOf(ix).Size, 1);
    asm.Push(Reg.BX);   // harmless when the base place is direct
    if (basePlace.Far) {
      asm.Push(Reg.ES);
      this.EmitInt16Argument(ix.Arguments[0]);
      asm.Pop(Reg.ES);
    } else
      this.EmitInt16Argument(ix.Arguments[0]);
    if (elementSize != 1)
      this.EmitIndexScale(elementSize);
    asm.Pop(Reg.BX);

    // fold the computed index into the base cell: needs a BX-based cell
    var cell = basePlace.Cell;
    if (cell.Base == null) {
      asm.Mov(Reg.BX, Reg.AX);
      var direct = Mem.At(Reg.BX, cell.Displacement);
      if (cell.Label is { } label)
        direct = Mem.At(Reg.BX, label, cell.Displacement);
      if (cell.Segment is { } seg)
        direct = direct.Seg(seg);
      return basePlace with { Cell = direct };
    }

    asm.Add(Reg.BX, Reg.AX);
    return basePlace;
  }
}
