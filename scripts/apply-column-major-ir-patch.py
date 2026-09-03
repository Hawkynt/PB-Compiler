from pathlib import Path

path = Path("PowerBasic.Compiler/Ir/IrLowering.cs")
text = path.read_text(encoding="utf-8")


def replace_once(old: str, new: str) -> None:
  global text
  count = text.count(old)
  if count != 1:
    raise SystemExit(f"expected exactly one match, found {count}: {old.splitlines()[0]!r}")
  text = text.replace(old, new, 1)


replace_once(
'''  /// <summary>The address of one array element, by row-major flattening of the index list.</summary>
''',
'''  /// <summary>The address of one array element in PowerBASIC's first-subscript-fastest layout.</summary>
''')

replace_once(
'''    IrValue? flat = null;
    for (var k = 0; k < bounds.Count; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      if (this._checkBounds)
        this.EmitBoundsCheck(idx, new IrConstantInt(IrType.I32, bounds[k].Lower), new IrConstantInt(IrType.I32, bounds[k].Upper));
      var rel = this._b.Sub(idx, new IrConstantInt(IrType.I32, bounds[k].Lower));
      var size = bounds[k].Upper - bounds[k].Lower + 1;
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, new IrConstantInt(IrType.I32, size)), rel);
    }

    if (arr.Element is StringType)
      return (this._b.Gep(basePtr, flat!, IrType.Ptr), arr.Element);   // ptr-element stride is target-dependent: typed GEP
    var byteOffset = this._b.Mul(flat!, new IrConstantInt(IrType.I32, arr.Element.Size));
''',
'''    // Evaluate subscripts in source order, because a subscript expression may call a function or
    // otherwise have observable effects. Only after all relative indexes exist do we fold them from
    // the last dimension inward: rel0 + size0 * (rel1 + size1 * (...)). PowerBASIC stores the first
    // subscript contiguously, so dimension zero has element stride one.
    var relative = new IrValue[bounds.Count];
    for (var k = 0; k < bounds.Count; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      if (this._checkBounds)
        this.EmitBoundsCheck(idx, new IrConstantInt(IrType.I32, bounds[k].Lower), new IrConstantInt(IrType.I32, bounds[k].Upper));
      relative[k] = this._b.Sub(idx, new IrConstantInt(IrType.I32, bounds[k].Lower));
    }

    IrValue flat = relative[^1];
    for (var k = bounds.Count - 2; k >= 0; --k) {
      var size = bounds[k].Upper - bounds[k].Lower + 1;
      flat = this._b.Add(this._b.Mul(flat, new IrConstantInt(IrType.I32, size)), relative[k]);
    }

    if (arr.Element is StringType)
      return (this._b.Gep(basePtr, flat, IrType.Ptr), arr.Element);   // ptr-element stride is target-dependent: typed GEP
    var byteOffset = this._b.Mul(flat, new IrConstantInt(IrType.I32, arr.Element.Size));
''')

replace_once(
'''  // promotable scalar slot. Sizes feed row-major flattening and the allocation count.
''',
'''  // promotable scalar slot. Sizes feed first-subscript-fastest flattening and the allocation count.
''')

replace_once(
'''  /// <summary>The address of one element of a runtime-allocated dynamic array (row-major flattening).</summary>
  private (IrValue Address, PbType Element) DynamicElementAddress(CallOrIndexExpr expr, VariableSymbol symbol, ArrayType arr) {
    if (expr.Arguments.Count != arr.Rank)
      throw new IrLoweringException("dynamic array rank mismatch");
    var descriptor = this.DynDescriptor(symbol, arr.Rank);
    var data = this._b.Load(IrType.FarPtr, descriptor.Data);

    IrValue? flat = null;
    for (var k = 0; k < arr.Rank; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var lo = this._b.Load(IrType.I32, descriptor.Lo[k]);
      // $ERROR BOUNDS ON over a dynamic array: the dimension is not a compile-time constant, so the
      // upper bound is reconstructed from the descriptor the REDIM filled in - lo + size - 1
      if (this._checkBounds) {
        var size = this._b.Load(IrType.I32, descriptor.Size[k]);
        this.EmitBoundsCheck(idx, lo, this._b.Sub(this._b.Add(lo, size), new IrConstantInt(IrType.I32, 1)));
      }
      var rel = this._b.Sub(idx, lo);
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, this._b.Load(IrType.I32, descriptor.Size[k])), rel);
    }

    if (arr.Element is StringType)
      return (this._b.Gep(data, flat!, IrType.Ptr), arr.Element);
    return (this._b.Gep(data, this._b.Mul(flat!, new IrConstantInt(IrType.I32, arr.Element.Size))), arr.Element);
  }
''',
'''  /// <summary>The address of one runtime-allocated element in PowerBASIC's first-subscript-fastest layout.</summary>
  private (IrValue Address, PbType Element) DynamicElementAddress(CallOrIndexExpr expr, VariableSymbol symbol, ArrayType arr) {
    if (expr.Arguments.Count != arr.Rank)
      throw new IrLoweringException("dynamic array rank mismatch");
    var descriptor = this.DynDescriptor(symbol, arr.Rank);
    var data = this._b.Load(IrType.FarPtr, descriptor.Data);

    // Preserve source-order evaluation just as the direct emitter does. Descriptor extents are read
    // afterwards for the reverse Horner fold, so changing the physical layout never reorders a call
    // nested inside a subscript expression.
    var relative = new IrValue[arr.Rank];
    for (var k = 0; k < arr.Rank; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var lo = this._b.Load(IrType.I32, descriptor.Lo[k]);
      // $ERROR BOUNDS ON over a dynamic array: the dimension is not a compile-time constant, so the
      // upper bound is reconstructed from the descriptor the REDIM filled in - lo + size - 1
      if (this._checkBounds) {
        var size = this._b.Load(IrType.I32, descriptor.Size[k]);
        this.EmitBoundsCheck(idx, lo, this._b.Sub(this._b.Add(lo, size), new IrConstantInt(IrType.I32, 1)));
      }
      relative[k] = this._b.Sub(idx, lo);
    }

    IrValue flat = relative[^1];
    for (var k = arr.Rank - 2; k >= 0; --k)
      flat = this._b.Add(this._b.Mul(flat, this._b.Load(IrType.I32, descriptor.Size[k])), relative[k]);

    if (arr.Element is StringType)
      return (this._b.Gep(data, flat, IrType.Ptr), arr.Element);
    return (this._b.Gep(data, this._b.Mul(flat, new IrConstantInt(IrType.I32, arr.Element.Size))), arr.Element);
  }
''')

replace_once(
'''  /// The address of one element of a <c>DIM ... AT segment</c> array: the same row-major flattening
  /// every other array gets, ending in a FAR pointer rather than a near one.
''',
'''  /// The address of one element of a <c>DIM ... AT segment</c> array: the same PowerBASIC
  /// first-subscript-fastest flattening every other array gets, ending in a FAR pointer rather than a near one.
''')

replace_once(
'''    var descriptor = this.DynDescriptor(symbol, arr.Rank);
    IrValue? flat = null;
    for (var k = 0; k < arr.Rank; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var lo = this._b.Load(IrType.I32, descriptor.Lo[k]);
      if (this._checkBounds) {
        var size = this._b.Load(IrType.I32, descriptor.Size[k]);
        this.EmitBoundsCheck(idx, lo, this._b.Sub(this._b.Add(lo, size), new IrConstantInt(IrType.I32, 1)));
      }
      var rel = this._b.Sub(idx, lo);
      flat = flat is null ? rel : this._b.Add(this._b.Mul(flat, this._b.Load(IrType.I32, descriptor.Size[k])), rel);
    }

    var offset = this._b.Trunc(this._b.Mul(flat!, new IrConstantInt(IrType.I32, Math.Max(arr.Element.Size, 1))), IrType.I16);
''',
'''    var descriptor = this.DynDescriptor(symbol, arr.Rank);
    var relative = new IrValue[arr.Rank];
    for (var k = 0; k < arr.Rank; ++k) {
      var idx = this.Coerce(this.LowerExpr(expr.Arguments[k]), this._model.TypeOf(expr.Arguments[k]), PbType.Long);
      var lo = this._b.Load(IrType.I32, descriptor.Lo[k]);
      if (this._checkBounds) {
        var size = this._b.Load(IrType.I32, descriptor.Size[k]);
        this.EmitBoundsCheck(idx, lo, this._b.Sub(this._b.Add(lo, size), new IrConstantInt(IrType.I32, 1)));
      }
      relative[k] = this._b.Sub(idx, lo);
    }

    IrValue flat = relative[^1];
    for (var k = arr.Rank - 2; k >= 0; --k)
      flat = this._b.Add(this._b.Mul(flat, this._b.Load(IrType.I32, descriptor.Size[k])), relative[k]);

    var offset = this._b.Trunc(this._b.Mul(flat, new IrConstantInt(IrType.I32, Math.Max(arr.Element.Size, 1))), IrType.I16);
''')

path.write_text(text, encoding="utf-8")
