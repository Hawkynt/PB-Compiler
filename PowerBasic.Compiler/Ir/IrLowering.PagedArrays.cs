using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.Ir;

/// <summary>
/// The MEMORY-MODEL array classes: <c>DIM HUGE</c>, <c>DIM VIRTUAL</c>, and the pb36 <c>EMS</c> /
/// <c>XMS</c> spellings that ride the same paged machinery. An array of one of these classes does not
/// live in the far array heap every other dynamic array comes out of - HUGE takes a block from DOS
/// (int 21h/48h) and VIRTUAL takes EMS pages (int 67h) - and neither is reachable through one segment.
///
/// <para>
/// What makes them lowerable at all is that the ADDRESSING is arithmetic and the STORAGE is already a
/// runtime service. HUGE normalizes a 32-bit byte offset into <c>base + (offset &gt;&gt; 4)</c> and
/// <c>offset &amp; 15</c>, which is a segment value and a displacement - exactly the pair
/// <see cref="IrFarPtr"/> carries, and it never required the segment to be a constant. VIRTUAL splits
/// the same offset into a 16 KiB logical page and an in-page displacement, maps the page pair into the
/// EMS frame, and then addresses the frame segment the same far way. Everything else - the DOS block,
/// the EMS handle, the page mapper, the zero fill - is a routine the DOS runtime already exports and
/// the direct emitter already calls, so this lowering calls the identical entries with the identical
/// arguments (see <c>RuntimeAbi</c>, and <c>DosRuntime.Ems.cs</c> for the conventions).
/// </para>
///
/// <para>
/// The window cache is the one piece of shared state, and it is deliberately shared: the frame holds
/// ONE page pair for the whole program, so <c>rt_ems_curhnd</c> / <c>rt_ems_curpage</c> are named
/// runtime cells rather than anything this lowering invents. A routed access reads and writes the very
/// words a directly emitted one does, which is what lets the two paths coexist in one image at all.
/// A remap only happens when the cache disagrees, and the ordering is the direct emitter's: the cells
/// are updated BEFORE the mapping call, so a call that never returns cannot leave them claiming a
/// window that was never mapped.
/// </para>
///
/// <para>
/// What it deliberately refuses, each because lowering it would be a guess rather than a translation:
/// </para>
/// <list type="bullet">
///   <item>rank above one, and any element that is not a scalar. The direct emitter refuses both
///   (<c>TryEmitLongBoundsAndByteCount</c>), so there is no behaviour to agree with;</item>
///   <item><c>REDIM PRESERVE</c>, refused on the direct path for the same reason it is refused here -
///   the copy would have to walk two segment-stepped or page-mapped blocks at once;</item>
///   <item><c>ERASE</c> of an <c>EMS</c> or <c>XMS</c> array. HUGE and VIRTUAL have their own arms in
///   the direct emitter's <c>EmitErase</c> and EMS/XMS do not, so one falls through to the
///   conventional path and reclaims a heap block out of a descriptor that is not one. Reproducing
///   that is not fidelity, it is copying a defect into a second place - so it declines, and the
///   program keeps the direct emitter's behaviour whatever that turns out to be;</item>
///   <item>an array whose storage must be shared with a procedure. The descriptor here is a pair of
///   frame slots, and a directly emitted procedure reads the direct emitter's 20-byte DGROUP one;
///   two descriptors for one array agree about nothing;</item>
///   <item>the ADDRESS of an element, as opposed to a read or a write of one. That is
///   <see cref="IrFarPtr"/>'s own rule and it applies unchanged: a far pointer handed to a BYREF
///   parameter or to VARPTR loses its segment silently.</item>
/// </list>
///
/// <para>
/// No bounds check is emitted, and that is fidelity rather than an omission: the direct emitter's
/// <c>EmitHugeOrVirtualElementPlace</c> has none either, so <c>$ERROR BOUNDS ON</c> does not reach
/// these classes on either path.
/// </para>
/// </summary>
public sealed partial class IrLowering {

  /// <summary>
  /// The two words a memory-model array is addressed through, beside the bounds every dynamic array
  /// already keeps in its <see cref="DynArr"/>. HUGE uses <paramref name="Segment"/> alone - the base
  /// of its DOS block; the paged classes use both - the EMS page-frame segment and the handle whose
  /// pages are mapped into it.
  /// </summary>
  private readonly record struct PagedArr(IrValue Segment, IrValue Handle);

  private readonly Dictionary<VariableSymbol, PagedArr> _pagedArrays = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The descriptor slots, created on first use and explicitly ZEROED in the entry block. The zero is
  /// load-bearing: an allocation frees whatever the slot already held, and the direct emitter's
  /// equivalent is a DGROUP cell that starts at zero - which both <c>rt_hugefree</c> and
  /// <c>rt_emsfree</c> read as "nothing to release". An alloca left uninitialised would hand them a
  /// frame leftover to free.
  /// </summary>
  private PagedArr PagedDescriptor(VariableSymbol symbol) {
    if (this._pagedArrays.TryGetValue(symbol, out var existing))
      return existing;
    var segment = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I16) { Name = symbol.Name + ".seg" });
    this._entry.InsertAt(this._entryAllocaCount++, new IrStore(new IrConstantInt(IrType.I16, 0), segment));
    var handle = this._entry.InsertAt(this._entryAllocaCount++,
      new IrAlloca(IrType.I16) { Name = symbol.Name + ".hnd" });
    this._entry.InsertAt(this._entryAllocaCount++, new IrStore(new IrConstantInt(IrType.I16, 0), handle));
    var descriptor = new PagedArr(segment, handle);
    this._pagedArrays[symbol] = descriptor;
    return descriptor;
  }

  /// <summary>The shape both paths agree on; anything else declines rather than being approximated.</summary>
  private void RequirePagedArrayShape(VariableSymbol symbol, ArrayType arr) {
    if (arr.Rank != 1)
      throw new IrLoweringException(
        $"a {symbol.ArrayClass} array of rank {arr.Rank} (the direct emitter takes rank 1 only)");
    if (arr.Element is StringType or FlexType)
      throw new IrLoweringException($"dynamic strings inside a {symbol.ArrayClass} array");
    if (arr.Element is not ScalarType)
      throw new IrLoweringException($"a {arr.Element} element of a {symbol.ArrayClass} array");
    if (this.NeedsSharedStorage(symbol))
      throw new IrLoweringException($"a {symbol.ArrayClass} array a procedure also reaches");
  }

  /// <summary>True when the class is addressed through the EMS page frame rather than by stepping a segment.</summary>
  private static bool IsEmsPaged(ArrayClass arrayClass) => arrayClass is not ArrayClass.Huge;

  private void LowerPagedDim(DimStmt d) {
    foreach (var v in d.Variables) {
      if (v.ArrayBounds is not { } dims)
        throw new IrLoweringException($"DIM {d.Class} {v.Name} without array bounds");
      if (this.ArrayVariable(v) is not { Type: ArrayType arr } symbol)
        throw new IrLoweringException($"DIM {d.Class}: no array symbol for {v.Name}");
      this.LowerPagedAllocation(symbol, arr, dims);
    }
  }

  /// <summary>
  /// <c>DIM HUGE/VIRTUAL a(lo TO hi)</c> and the <c>REDIM</c> that repeats it: release, measure,
  /// allocate, zero - the direct emitter's order, statement for statement
  /// (<c>EmitHugeAllocation</c> / <c>EmitVirtualAllocation</c>). The bounds go into the same
  /// descriptor slots a conventional dynamic array uses, so LBOUND and UBOUND need no case of their
  /// own.
  /// </summary>
  private void LowerPagedAllocation(VariableSymbol symbol, ArrayType arr,
      IReadOnlyList<(Expression? Lower, Expression Upper)> dims) {
    this.RequirePagedArrayShape(symbol, arr);
    if (dims.Count != 1)
      throw new IrLoweringException($"{symbol.ArrayClass} array rank mismatch");

    var descriptor = this.PagedDescriptor(symbol);
    var bounds = this.DynDescriptor(symbol, 1);
    var paged = IsEmsPaged(symbol.ArrayClass);

    // the previous allocation goes back BEFORE the new bounds are written, because the bounds about to
    // be overwritten are the ones that block was measured by
    if (paged) {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_ems_free", IrType.Void, IrType.I16),
        this._b.Load(IrType.I16, descriptor.Handle));
      this._b.Store(new IrConstantInt(IrType.I16, 0), descriptor.Handle);
    } else {
      this._b.Call(IrType.Void, this.RuntimeFn("rt_huge_free", IrType.Void, IrType.I16),
        this._b.Load(IrType.I16, descriptor.Segment));
      this._b.Store(new IrConstantInt(IrType.I16, 0), descriptor.Segment);
    }

    var (lower, upper) = dims[0];
    var lo = lower is null
      ? new IrConstantInt(IrType.I32, 0)
      : this.Coerce(this.LowerExpr(lower), this._model.TypeOf(lower), PbType.Long);
    var hi = this.Coerce(this.LowerExpr(upper), this._model.TypeOf(upper), PbType.Long);
    var size = this._b.Add(this._b.Sub(hi, lo), new IrConstantInt(IrType.I32, 1));
    this._b.Store(lo, bounds.Lo[0]);
    this._b.Store(size, bounds.Size[0]);
    // the byte count is a 32-bit product for the reason ArrayBytes gives, and more so here: an array
    // that fits a word had no business being HUGE
    var bytes = this._b.Mul(size, new IrConstantInt(IrType.I32, System.Math.Max(arr.Element.Size, 1)));

    if (!paged) {
      var block = this._b.Call(IrType.I16, this.RuntimeFn("rt_huge_alloc", IrType.I16, IrType.I32), bytes);
      this._b.Store(block, descriptor.Segment);
      this._b.Call(IrType.Void, this.RuntimeFn("rt_huge_zero", IrType.Void, IrType.I16, IrType.I32), block, bytes);
      return;
    }
    var handle = this._b.Call(IrType.I16, this.RuntimeFn("rt_ems_alloc", IrType.I16, IrType.I32), bytes);
    this._b.Store(handle, descriptor.Handle);
    this._b.Store(this._b.Call(IrType.I16, this.RuntimeFn("rt_ems_frame", IrType.I16)), descriptor.Segment);
    // rt_emszero remaps the frame as it goes and invalidates the window cache itself, so nothing here
    // has to - which is also why the first element access after a DIM always remaps
    this._b.Call(IrType.Void, this.RuntimeFn("rt_ems_zero", IrType.Void, IrType.I16, IrType.I32), handle, bytes);
  }

  /// <summary>
  /// <c>ERASE</c> gives the storage back and leaves the descriptor empty - it does not zero-fill, as
  /// erasing a static array does, because the memory is no longer the program's.
  /// </summary>
  private void LowerPagedErase(VariableSymbol symbol, ArrayType arr) {
    this.RequirePagedArrayShape(symbol, arr);
    var descriptor = this.PagedDescriptor(symbol);
    switch (symbol.ArrayClass) {
      case ArrayClass.Huge:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_huge_free", IrType.Void, IrType.I16),
          this._b.Load(IrType.I16, descriptor.Segment));
        this._b.Store(new IrConstantInt(IrType.I16, 0), descriptor.Segment);
        return;
      case ArrayClass.Virtual:
        this._b.Call(IrType.Void, this.RuntimeFn("rt_ems_free", IrType.Void, IrType.I16),
          this._b.Load(IrType.I16, descriptor.Handle));
        this._b.Store(new IrConstantInt(IrType.I16, 0), descriptor.Handle);
        this._b.Store(new IrConstantInt(IrType.I16, 0), descriptor.Segment);
        return;
      default:
        // EMS/XMS have no arm in the direct emitter's EmitErase and fall through to the conventional
        // reclaim, which reads an HV descriptor as a heap block. See the type comment.
        throw new IrLoweringException(
          $"ERASE of the {symbol.ArrayClass} array {symbol.Name} (the direct path reclaims it as a heap block)");
    }
  }

  /// <summary>
  /// One element of a memory-model array, as the far pointer a load or a store goes straight through.
  ///
  /// <para>
  /// The 32-bit byte offset is taken apart into its two 16-bit halves and recombined exactly as the
  /// direct emitter's <c>SHR AX,n / SHL DX,16-n / OR AX,DX</c> does. Doing the shift at 32 bits would
  /// be the same value and would not select: a pair shift is one bit per step and the selector caps it
  /// at eight, which a shift of fourteen is not. Both spellings drop everything above bit 19 (HUGE) or
  /// bit 17 (paged), which is the wrap the machine has and no allocation can reach anyway.
  /// </para>
  /// </summary>
  private (IrValue Address, PbType Element) PagedElementAddress(
      CallOrIndexExpr expr, VariableSymbol symbol, ArrayType arr) {
    this.RequirePagedArrayShape(symbol, arr);
    if (expr.Arguments.Count != 1)
      throw new IrLoweringException($"{symbol.ArrayClass} array rank mismatch");
    // the descriptor is this function's own storage, so an access the declaration has not reached -
    // a use before the DIM, or an array declared in another function - has no segment to name
    if (!this._pagedArrays.ContainsKey(symbol))
      throw new IrLoweringException($"element of {symbol.Name} before its DIM was lowered");

    var descriptor = this.PagedDescriptor(symbol);
    var bounds = this.DynDescriptor(symbol, 1);
    var index = this.Coerce(this.LowerExpr(expr.Arguments[0]), this._model.TypeOf(expr.Arguments[0]), PbType.Long);
    var byteOffset = this._b.Mul(this._b.Sub(index, this._b.Load(IrType.I32, bounds.Lo[0])),
      new IrConstantInt(IrType.I32, System.Math.Max(arr.Element.Size, 1)));
    var low = this._b.Trunc(byteOffset, IrType.I16);
    var high = this._b.Trunc(
      this._b.Binary(IrBinaryOp.LShr, byteOffset, new IrConstantInt(IrType.I32, 16)), IrType.I16);

    if (symbol.ArrayClass == ArrayClass.Huge) {
      var step = this._b.Or(
        this._b.Binary(IrBinaryOp.LShr, low, new IrConstantInt(IrType.I16, 4)),
        this._b.Shl(high, new IrConstantInt(IrType.I16, 12)));
      var segment = this._b.Add(step, this._b.Load(IrType.I16, descriptor.Segment));
      return (this._b.FarPtr(segment, this._b.And(low, new IrConstantInt(IrType.I16, 15))), arr.Element);
    }

    var page = this._b.Or(
      this._b.Binary(IrBinaryOp.LShr, low, new IrConstantInt(IrType.I16, 14)),
      this._b.Shl(high, new IrConstantInt(IrType.I16, 2)));
    var inPage = this._b.And(low, new IrConstantInt(IrType.I16, 0x3FFF));
    this.MapEmsWindow(this._b.Load(IrType.I16, descriptor.Handle), page);
    return (this._b.FarPtr(this._b.Load(IrType.I16, descriptor.Segment), inPage), arr.Element);
  }

  /// <summary>
  /// Brings the 16 KiB page pair holding the element into the EMS frame, unless it is already there.
  ///
  /// <para>
  /// The cache is GLOBAL because the frame is: every paged array in the image shares one window, so
  /// the test has to cover which HANDLE is mapped as well as which page. Skipping the remap when both
  /// agree is sound in either direction - the only things that change the window are these two cells'
  /// own writers and the runtime routines that reset them, and a call between two accesses
  /// invalidates every cached load the optimizer holds.
  /// </para>
  /// </summary>
  private void MapEmsWindow(IrValue handle, IrValue page) {
    var currentPage = this.ErrorCell("rt_ems_curpage", IrType.I16);
    var currentHandle = this.ErrorCell("rt_ems_curhnd", IrType.I16);
    var stale = this._b.Or(
      this._b.Cmp(IrCmpPred.Ne, page, this._b.Load(IrType.I16, currentPage)),
      this._b.Cmp(IrCmpPred.Ne, handle, this._b.Load(IrType.I16, currentHandle)));

    var remap = this.NewBlock("ems.remap");
    var mapped = this.NewBlock("ems.mapped");
    this._b.CondBr(stale, remap, mapped);
    this._b.Position(remap);
    this._b.Store(page, currentPage);
    this._b.Store(handle, currentHandle);
    this._b.Call(IrType.Void, this.RuntimeFn("rt_ems_map2", IrType.Void, IrType.I16, IrType.I16), handle, page);
    this._b.Br(mapped);
    this._b.Position(mapped);
  }

  /// <summary>
  /// <c>FRE(-11)</c>: how many EMS bytes are unallocated, which only the EMM can say. It is here
  /// rather than with the other intrinsics because it is the same subject - it is how a program checks
  /// that a VIRTUAL array really went to EMS, which is exactly what DIFF17 asserts.
  ///
  /// <para>
  /// Every OTHER spelling of FRE declines. The direct emitter answers them with an advisory 32767
  /// after evaluating and discarding the argument - and discarding it is not free, because a STRING
  /// argument is a handle the call RELEASES. Lowering the informative case and refusing the ones whose
  /// only content is a side effect keeps the two paths from disagreeing about ownership.
  /// </para>
  /// </summary>
  private IrValue LowerFre(CallOrIndexExpr call) {
    // the same shape the direct emitter matches on: a literal, or a negated literal
    var isEms = call.Arguments is [{ } argument]
      && argument is IntegerLiteralExpr { Value: -11 }
        or UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteralExpr { Value: 11 } };
    if (!isEms)
      throw new IrLoweringException("FRE other than FRE(-11)");
    return this.Coerce(this._b.Call(IrType.I32, this.RuntimeFn("rt_ems_fre", IrType.I32)),
      PbType.Long, this._model.TypeOf(call));
  }
}
