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
      var symbol = this.LookupVariable(v.Name, v.Suffix);
      if (symbol?.Type is not ArrayType { IsDynamic: true })
        continue;   // static arrays and scalars are laid out at compile time
      this.EmitArrayAllocation(symbol, v.ArrayBounds, dim.Position);
    }
  }

  private void EmitRedim(RedimStmt redim) {
    foreach (var v in redim.Variables) {
      var symbol = this.LookupVariable(v.Name, v.Suffix);
      if (symbol?.Type is not ArrayType { IsDynamic: true } || v.ArrayBounds == null) {
        this.Unsupported(redim);
        continue;
      }
      this.EmitArrayAllocation(symbol, v.ArrayBounds, redim.Position);
    }
  }

  /// <summary>Fills the descriptor and allocates zeroed storage from the far array heap.</summary>
  private void EmitArrayAllocation(VariableSymbol symbol, IReadOnlyList<(Expression? Lower, Expression Upper)> bounds, SourcePosition position) {
    var asm = this._asm;
    var arrayType = (ArrayType)symbol.Type;
    var descriptor = this.SlotOf(symbol);
    var elementSize = Math.Max(arrayType.Element.Size, 1);

    if (bounds.Count != arrayType.Rank) {
      this.Unsupported(position, $"REDIM rank mismatch for {symbol.Name}");
      return;
    }

    asm.Mov(Mem.Word(descriptor, 4), elementSize);
    asm.Mov(Mem.Word(descriptor, 6), arrayType.Rank);

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

  private void EmitErase(EraseStmt erase) {
    var asm = this._asm;
    foreach (var array in erase.Arrays) {
      if (!model.VariableBindings.TryGetValue(array, out var symbol) || symbol.Type is not ArrayType arrayType) {
        this.Unsupported(erase);
        continue;
      }
      var slot = this.SlotOf(symbol);
      if (arrayType.IsDynamic) {
        asm.Mov(Mem.Word(slot), (Imm)0);   // storage is leaked - bump allocator (documented)
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

    var descriptor = this.SlotOf(symbol);
    asm.Mov(Reg.AX, Mem.Word(descriptor, 8 + (dimension - 1) * 4));
    if (isUpper) {
      asm.Add(Reg.AX, Mem.Word(descriptor, 8 + (dimension - 1) * 4 + 2));
      asm.Dec(Reg.AX);
    }
    asm.Cwd();
  }

  /// <summary>
  /// Address of an array element: row-major linear index scaled by the element
  /// size. Static arrays resolve to [BX + label - bias]; dynamic arrays load ES
  /// from the descriptor and resolve to ES:[BX + offset].
  /// </summary>
  private Place? EmitArrayElementPlace(CallOrIndexExpr call, VariableSymbol symbol) {
    var asm = this._asm;
    if (symbol.Type is not ArrayType arrayType || call.Arguments.Count != arrayType.Rank) {
      this.Unsupported(call, $"indexing {symbol.Name}");
      return null;
    }
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
        this.EmitInt16Argument(call.Arguments[d]);
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

    // dynamic: Horner over the descriptor extents
    var descriptor = this.SlotOf(symbol);
    for (var d = 0; d < arrayType.Rank; ++d) {
      this.EmitInt16Argument(call.Arguments[d]);
      asm.Sub(Reg.AX, Mem.Word(descriptor, 8 + d * 4));
      if (d > 0) {
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Imul(Reg.AX, Mem.Word(descriptor, 8 + d * 4 + 2));
        asm.Add(Reg.AX, Reg.CX);
      }
      if (d < arrayType.Rank - 1)
        asm.Push(Reg.AX);
    }
    if (elementSize != 1)
      asm.Imul(Reg.AX, Reg.AX, elementSize);
    asm.Add(Reg.AX, Mem.Word(descriptor, 2));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.ES, Mem.Word(descriptor));
    return new(Mem.At(Reg.BX).Es(), true);
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
