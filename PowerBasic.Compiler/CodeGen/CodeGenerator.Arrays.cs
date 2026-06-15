using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  // Dynamic array descriptor layout (in the data segment, ArrayType.Size = 8 + rank*4):
  //   +0 segment (0 = unallocated)   +2 data offset
  //   +4 element size                +6 rank
  //   +8 + d*4: lower bound (word), extent (word) per dimension

  private void EmitDim(DimStmt dim) {
    foreach (var v in dim.Variables) {
      if (v.ArrayBounds == null)
        continue;
      var symbol = this.LookupVariable(v.Name, v.Suffix, isArray: true) ?? this.LookupVariable(v.Name, TypeSuffix.None, isArray: true);
      if (symbol?.Type is not ArrayType { IsDynamic: true })
        continue;   // static arrays and scalars are laid out at compile time
      this.EmitClassedAllocation(symbol, v.ArrayBounds, dim.AtAddress, dim.Position);
    }
  }

  private void EmitRedim(RedimStmt redim) {
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
      this.EmitClassedAllocation(symbol, v.ArrayBounds, null, redim.Position);
    }
  }

  /// <summary>Dispatches DIM/REDIM allocation by array class (conventional / HUGE / VIRTUAL / ABSOLUTE).</summary>
  private void EmitClassedAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, Expression? atAddress, SourcePosition position) {
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
        this.EmitArrayAllocation(symbol, bounds, position);
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
    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual or ArrayClass.Absolute) {
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
    asm.Imul(Reg.AX, Reg.AX, elementSize);
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
    asm.Imul(Reg.AX, Reg.AX, elementSize);
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
  private void EmitArrayAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position, bool reclaimOld = true) {
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
    asm.Call(this._rt.ArrAlloc);
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
      asm.Mov(Reg.CX, (arrayType.Size + 1) / 2);
      asm.Xor(Reg.AX, Reg.AX);
      asm.Rep();
      asm.Stosw();
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

    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual) {
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
  private Place? EmitArrayElementPlace(IReadOnlyList<Expression> indexes, VariableSymbol symbol, Expression at) {
    var asm = this._asm;
    // dynamic arrays may be re-DIMed with a different rank (the descriptor is
    // runtime state) - only static arrays pin their rank at compile time
    if (symbol.Type is not ArrayType arrayType || (arrayType.StaticBounds != null && indexes.Count != arrayType.Rank)) {
      this.Unsupported(at, $"indexing {symbol.Name}");
      return null;
    }

    if (symbol.ArrayClass is ArrayClass.Huge or ArrayClass.Virtual)
      return this.EmitHugeOrVirtualElementPlace(indexes, symbol, arrayType, at);
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    if (arrayType.StaticBounds is { } bounds) {
      // strides and the lower-bound bias fold at compile time
      var strides = new int[bounds.Count];
      var stride = 1;
      for (var d = bounds.Count - 1; d >= 0; --d) {
        strides[d] = stride;
        stride *= bounds[d].Upper - bounds[d].Lower + 1;
      }
      var bias = 0;
      for (var d = 0; d < bounds.Count; ++d)
        bias += bounds[d].Lower * strides[d];

      for (var d = 0; d < bounds.Count; ++d) {
        this.EmitInt16Argument(indexes[d]);
        // pb36 O16: a constant index provably inside the static bounds can
        // never raise Error 9 - the check is dead and disappears
        var provablyInRange = this.Optimize
          && this.Pb36Folder.TryFold(indexes[d]) is { Integer: { } ci }
          && ci >= bounds[d].Lower && ci <= bounds[d].Upper;
        if (this.CheckBounds && !provablyInRange) { // $ERROR BOUNDS ON -> Error 9
          asm.Cmp(Reg.AX, bounds[d].Lower);
          this.EmitRaiseWhen(asm.Jge, 9);
          asm.Cmp(Reg.AX, bounds[d].Upper);
          this.EmitRaiseWhen(asm.Jle, 9);
        }
        if (strides[d] != 1)
          asm.Imul(Reg.AX, Reg.AX, strides[d]);
        if (d > 0) {
          asm.Pop(Reg.BX);
          asm.Add(Reg.AX, Reg.BX);
        }
        if (d < bounds.Count - 1)
          asm.Push(Reg.AX);
      }
      if (elementSize != 1)
        asm.Imul(Reg.AX, Reg.AX, elementSize);
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
      asm.Imul(Reg.AX, Reg.AX, elementSize);
    asm.Add(Reg.AX, descriptor(2));
    asm.Mov(Reg.BX, Reg.AX);
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

    // VIRTUAL: logical page = offset >> 14; in-page offset = offset & 0x3FFF
    var mapped = asm.DefineLabel();
    asm.Mov(Reg.BX, Reg.AX);
    asm.And(Reg.BX, 0x3FFF);
    asm.Mov(Reg.CL, (Imm)14);
    asm.Shr(Reg.AX, Reg.CL);
    asm.Mov(Reg.CL, (Imm)2);
    asm.Shl(Reg.DX, Reg.CL);
    asm.Or(Reg.AX, Reg.DX);                       // AX = logical page
    asm.Cmp(Reg.AX, Mem.Word(descriptor, 18));
    asm.Je(mapped);
    asm.Mov(Mem.Word(descriptor, 18), Reg.AX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.DX, Mem.Word(descriptor, 16));    // EMS handle
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
      asm.Imul(Reg.AX, Reg.AX, elementSize);
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
