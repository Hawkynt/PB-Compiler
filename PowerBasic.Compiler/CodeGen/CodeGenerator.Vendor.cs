using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>Vendor-corpus wave: BIT statements, EXIT FAR, ARRAY SORT/SCAN.</summary>
public sealed partial class CodeGenerator {

  #region BIT SET / RESET / TOGGLE

  /// <summary>BIT SET/RESET/TOGGLE var, n - builds a (32-bit capable) mask 1&lt;&lt;n and applies it.</summary>
  private void EmitBit(BitStmt bit) {
    var asm = this._asm;
    var targetType = model.TypeOf(bit.Target);
    if (targetType is not ScalarType { IsFloat: false, ByteSize: 1 or 2 or 4 } scalar) {
      this.Unsupported(bit);
      return;
    }

    this.EmitInt16Argument(bit.Bit);
    asm.Push(Reg.AX);
    if (this.EmitPlace(bit.Target) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }
    asm.Pop(Reg.CX);

    // mask = 1 << CL in DX:AX (CX > 31 yields 0 after 32 steps - harmless)
    var apply = asm.DefineLabel();
    var shift = asm.DefineLabel();
    asm.Mov(Reg.AX, 1);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Jcxz(apply);
    asm.MarkLabel(shift);
    asm.Shl(Reg.AX, 1);
    asm.Rcl(Reg.DX, 1);
    asm.Loop(shift);
    asm.MarkLabel(apply);

    var lo = Adjust(place.Cell, 0, scalar.ByteSize == 1 ? OperandSize.Byte : OperandSize.Word);
    var hi = Adjust(place.Cell, 2, OperandSize.Word);
    switch (bit.Op) {
      case BitOp.Set:
        if (scalar.ByteSize == 1)
          asm.Or(lo, Reg.AL);
        else
          asm.Or(lo, Reg.AX);
        if (scalar.ByteSize == 4)
          asm.Or(hi, Reg.DX);
        break;
      case BitOp.Reset:
        asm.Not(Reg.AX);
        asm.Not(Reg.DX);
        if (scalar.ByteSize == 1)
          asm.And(lo, Reg.AL);
        else
          asm.And(lo, Reg.AX);
        if (scalar.ByteSize == 4)
          asm.And(hi, Reg.DX);
        break;
      default: // Toggle
        if (scalar.ByteSize == 1)
          asm.Xor(lo, Reg.AL);
        else
          asm.Xor(lo, Reg.AX);
        if (scalar.ByteSize == 4)
          asm.Xor(hi, Reg.DX);
        break;
    }
  }

  #endregion

  #region REPLACE

  /// <summary>REPLACE find WITH with IN target: rebuilds the target string.</summary>
  private void EmitReplaceStmt(ReplaceStmt replace) {
    var asm = this._asm;
    this.EmitExpression(replace.Target);        // subject (duplicated handle)
    asm.Push(Reg.AX);
    this.EmitExpression(replace.Find);
    asm.Push(Reg.AX);
    this.EmitExpression(replace.With);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.AX);
    asm.Call(this._rt.Replace);                 // -> AX = new handle

    asm.Push(Reg.AX);
    if (this.EmitPlace(replace.Target) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }
    asm.Pop(Reg.AX);
    this.EmitStorePlace(place, model.TypeOf(replace.Target), replace.Target);
  }

  #endregion

  #region EXIT FAR

  /// <summary>
  /// EXIT FAR AT label records the unwind point (SP/BP + target offset);
  /// a bare EXIT FAR restores them, abandoning all nested frames and GOSUBs.
  /// </summary>
  private void EmitExitFar(ExitFarStmt ef) {
    var asm = this._asm;
    if (ef.AtLabel is { } label) {
      asm.Mov(Mem.Word(asm.Lbl("rt_efar_tgt")), Imm.OffsetOf(this.UserLabel(label)));
      asm.Mov(Mem.Word(asm.Lbl("rt_efar_sp")), Reg.SP);
      asm.Mov(Mem.Word(asm.Lbl("rt_efar_bp")), Reg.BP);
      return;
    }
    asm.Mov(Reg.SP, Mem.Word(asm.Lbl("rt_efar_sp")));
    asm.Mov(Reg.BP, Mem.Word(asm.Lbl("rt_efar_bp")));
    asm.Jmp(Mem.Word(asm.Lbl("rt_efar_tgt")));
  }

  #endregion

  #region ARRAY SORT / SCAN

  /// <summary>Relop encoding shared with rt_scanstr (flags high byte).</summary>
  private static int ScanRelopCode(CaseComparison op) => op switch {
    CaseComparison.Equal => 0,
    CaseComparison.NotEqual => 1,
    CaseComparison.Less => 2,
    CaseComparison.LessEqual => 3,
    CaseComparison.Greater => 4,
    _ => 5,
  };

  /// <summary>
  /// Fills the shared rt_arpb parameter block (descriptor, start, count, range,
  /// collate) for ARRAY SORT/SCAN. Returns false (with a diagnostic) when the
  /// array is not a dynamic-string array. The collate handle cell is owned by
  /// the caller and must be freed after the runtime call.
  /// </summary>
  private bool TryEmitArrayStatementHeader(CallOrIndexExpr array, Expression? count, Expression? fromPos, Expression? toPos, Expression? collate, Statement at) {
    var asm = this._asm;
    if (!model.VariableBindings.TryGetValue(array, out var symbol) || symbol.Type is not ArrayType arrayType) {
      this.Unsupported(at);
      return false;
    }
    if (arrayType.Element is not StringType) {
      this.Unsupported(at.Position, "ARRAY SORT/SCAN on non-string arrays (comes with a later wave)");
      return false;
    }

    var arpb = asm.Lbl("rt_arpb");

    // descriptor pointer (shadow descriptors get refreshed for static arrays)
    this.EmitArrayDescriptorPush(array, symbol, arrayType);
    asm.Pop(Reg.AX);
    asm.Mov(Mem.Word(arpb), Reg.AX);

    // start index (defaults to the array's lower bound)
    if (array.Arguments.Count == 1)
      this.EmitInt16Argument(array.Arguments[0]);
    else {
      asm.Mov(Reg.BX, Mem.Word(arpb));
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, 8));
    }
    asm.Mov(Mem.Word(arpb, 2), Reg.AX);

    // count (defaults to lower + extent - start)
    if (count != null)
      this.EmitInt16Argument(count);
    else {
      asm.Mov(Reg.BX, Mem.Word(arpb));
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, 8));
      asm.Add(Reg.AX, Mem.Word(Reg.BX, 10));
      asm.Sub(Reg.AX, Mem.Word(arpb, 2));
    }
    asm.Mov(Mem.Word(arpb, 4), Reg.AX);

    // FROM x TO y (defaults: whole string)
    if (fromPos != null) {
      this.EmitInt16Argument(fromPos);
      asm.Mov(Mem.Word(arpb, 8), Reg.AX);
      this.EmitInt16Argument(toPos!);
      asm.Mov(Mem.Word(arpb, 10), Reg.AX);
    } else {
      asm.Mov(Mem.Word(arpb, 8), 1);
      asm.Mov(Mem.Word(arpb, 10), 0x7FFF);
    }

    // COLLATE table handle (owned - caller frees after the runtime call)
    if (collate != null) {
      this.EmitExpression(collate);
      asm.Mov(Mem.Word(arpb, 6), Reg.AX);
    } else
      asm.Mov(Mem.Word(arpb, 6), (Imm)0);

    return true;
  }

  private void EmitFreeArpbHandle(int offset) {
    var asm = this._asm;
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_arpb"), offset));
    asm.Call(this._rt.StrFree);
  }

  private void EmitArraySort(ArraySortStmt sort) {
    var asm = this._asm;
    if (sort.TagArray != null) {
      this.Unsupported(sort.Position, "ARRAY SORT TAGARRAY (comes with a later wave)");
      return;
    }
    if (!this.TryEmitArrayStatementHeader(sort.Array, sort.Count, sort.FromPos, sort.ToPos, sort.Collate, sort))
      return;
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 12), sort.Descend ? 1 : 0);
    asm.Call(this._rt.SortStr);
    this.EmitFreeArpbHandle(6);
  }

  private void EmitArrayScan(ArrayScanStmt scan) {
    var asm = this._asm;
    if (!this.TryEmitArrayStatementHeader(scan.Array, scan.Count, scan.FromPos, scan.ToPos, scan.Collate, scan))
      return;
    // flags: bit1 = the FROM/TO range clamps the element side only; relop in the high byte
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 12), 2 | (ScanRelopCode(scan.Op) << 8));
    this.EmitExpression(scan.Match);
    asm.Mov(Mem.Word(asm.Lbl("rt_arpb"), 14), Reg.AX);
    asm.Call(this._rt.ScanStr);
    asm.Push(Reg.AX);
    this.EmitFreeArpbHandle(6);
    this.EmitFreeArpbHandle(14);
    asm.Pop(Reg.AX);

    var targetType = model.TypeOf(scan.Target);
    this.Coerce(PbType.Integer, targetType, scan.Target);
    var kind = KindOf(targetType);
    if (kind == ValueKind.Int32)
      asm.Push(Reg.DX);
    if (kind != ValueKind.Float)
      asm.Push(Reg.AX);
    if (this.EmitPlace(scan.Target) is not { } place) {
      if (kind != ValueKind.Float)
        asm.Pop(Reg.AX);
      if (kind == ValueKind.Int32)
        asm.Pop(Reg.DX);
      return;
    }
    if (kind != ValueKind.Float)
      asm.Pop(Reg.AX);
    if (kind == ValueKind.Int32)
      asm.Pop(Reg.DX);
    this.EmitStorePlace(place, targetType, scan.Target);
  }

  /// <summary>
  /// Pushes the address of the array's runtime descriptor (shared with array
  /// arguments: parameters forward their pointer, dynamic arrays use their
  /// slot, static arrays go through a refreshed shadow descriptor).
  /// </summary>
  private void EmitArrayDescriptorPush(Expression arg, VariableSymbol symbol, ArrayType arrayType) {
    var asm = this._asm;
    _ = arg;
    if (symbol.Storage == VariableStorage.Parameter) {
      asm.Push(Mem.Word(Reg.BP, symbol.Offset));
      return;
    }

    if (arrayType.IsDynamic) {
      asm.Push(Imm.OffsetOf(this.SlotOf(symbol)));
      return;
    }

    var descriptor = this.ShadowDescriptorOf(symbol, arrayType);
    asm.Mov(Mem.Word(descriptor), Reg.DS);
    asm.Mov(Mem.Word(descriptor, 2), Imm.OffsetOf(this.SlotOf(symbol)));
    asm.Mov(Mem.Word(descriptor, 4), Math.Max(arrayType.Element.Size, 1));
    asm.Mov(Mem.Word(descriptor, 6), arrayType.Rank);
    for (var d = 0; d < arrayType.Rank; ++d) {
      var (lower, upper) = arrayType.StaticBounds![d];
      asm.Mov(Mem.Word(descriptor, 8 + d * 4), lower);
      asm.Mov(Mem.Word(descriptor, 8 + d * 4 + 2), upper - lower + 1);
    }
    asm.Push(Imm.OffsetOf(descriptor));
  }

  #endregion
}
