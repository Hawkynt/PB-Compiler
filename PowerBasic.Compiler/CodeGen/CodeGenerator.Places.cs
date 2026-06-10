using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// An addressable storage location. <see cref="Cell"/> is either a direct
  /// memory operand (data label or BP displacement) or BX-based when the
  /// address had to be computed at run time. <see cref="Far"/> locations live
  /// in the array heap; ES holds their segment when the place is produced.
  /// </summary>
  private readonly record struct Place(Mem Cell, bool Far);

  /// <summary>Direct cell of a symbol, or null when access needs a pointer (BYREF parameter).</summary>
  private Mem? TryDirectCell(VariableSymbol s) => s.Storage switch {
    VariableStorage.Global or VariableStorage.Static => Mem.At(this.SlotOf(s)),
    _ when s.IsArray => Mem.At(this.SlotOf(s)),     // local arrays use data slots (recursion caveat documented)
    VariableStorage.Local => Mem.At(Reg.BP, s.Offset),
    VariableStorage.Parameter when s.ByVal => Mem.At(Reg.BP, s.Offset),
    _ => null,
  };

  /// <summary>Rebuilds a memory operand with an extra displacement and explicit size.</summary>
  private static Mem Adjust(Mem m, int delta, OperandSize size) {
    var result = (m.Base, m.Label) switch {
      ({ } b, { } l) => Mem.At(b, l, m.Displacement + delta),
      ({ } b, null) => Mem.At(b, m.Displacement + delta),
      (null, { } l) => Mem.At(l, m.Displacement + delta),
      _ => Mem.At(m.Displacement + delta),
    };
    if (m.Segment is { } seg)
      result = result.Seg(seg);
    return result.WithSize(size);
  }

  /// <summary>
  /// Emits the address computation for an lvalue. Result cells based on BX must
  /// be consumed before BX (and ES, for far places) is clobbered; stores push
  /// the value around this call. Returns null (with a diagnostic) when the
  /// expression is not addressable.
  /// </summary>
  private Place? EmitPlace(Expression e) {
    var asm = this._asm;
    switch (e) {
      case NameExpr n: {
        if (!model.VariableBindings.TryGetValue(n, out var symbol)) {
          this.Unsupported(n, $"address of {n.Name}");
          return null;
        }
        if (this.TryDirectCell(symbol) is { } cell)
          return new(cell, false);
        asm.Mov(Reg.BX, Mem.Word(Reg.BP, symbol.Offset));   // BYREF parameter: load the pointer
        return new(Mem.At(Reg.BX), false);
      }

      case MemberExpr m: {
        if (model.TypeOf(m.Target) is not UdtType udt || udt.FindField(m.Member) is not { } field) {
          this.Unsupported(m, "member access");
          return null;
        }
        if (this.EmitPlace(m.Target) is not { } basePlace)
          return null;
        return basePlace with { Cell = Adjust(basePlace.Cell, field.Offset, OperandSize.None) };
      }

      case CallOrIndexExpr call when model.VariableBindings.TryGetValue(call, out var array):
        return this.EmitArrayElementPlace(call, array);

      case IndexExpr ix:
        return this.EmitFieldArrayPlace(ix);

      default:
        this.Unsupported(e, "addressable expression");
        return null;
    }
  }

  /// <summary>True when the expression is a near-addressable lvalue (no far heap involved).</summary>
  private bool IsNearLValue(Expression e) => e switch {
    NameExpr n => model.VariableBindings.TryGetValue(n, out var s) && !s.IsArray,
    CallOrIndexExpr c => model.VariableBindings.TryGetValue(c, out var s)
      && s.Type is ArrayType { IsDynamic: false },
    MemberExpr m => this.IsNearLValue(m.Target),
    IndexExpr ix => this.IsNearLValue(ix.Target),
    _ => false,
  };

  #region loads & stores

  /// <summary>Loads the value at <paramref name="place"/> into the evaluation registers for <paramref name="type"/>.</summary>
  private void EmitLoadPlace(Place place, PbType type, Expression at) {
    var asm = this._asm;
    switch (type) {
      case ScalarType { ByteSize: 1 }:
        asm.Mov(Reg.AL, Adjust(place.Cell, 0, OperandSize.Byte));
        asm.Xor(Reg.AH, Reg.AH);
        break;

      case ScalarType { IsFloat: false, ByteSize: 2 }:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        break;

      case ScalarType { IsFloat: false }:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        asm.Mov(Reg.DX, Adjust(place.Cell, 2, OperandSize.Word));
        break;

      case ScalarType { ByteSize: 4 }:
        asm.Fld(Adjust(place.Cell, 0, OperandSize.Dword));
        break;

      case ScalarType { ByteSize: 8 }:
        asm.Fld(Adjust(place.Cell, 0, OperandSize.Qword));
        break;

      case ScalarType:
        asm.Fld(Adjust(place.Cell, 0, OperandSize.Tbyte));
        break;

      case StringType or FlexType:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        asm.Call(this._rt.StrDup);
        break;

      case FixedStringType fixedString:
        asm.Lea(Reg.SI, place.Cell);
        if (place.Far)
          asm.Mov(Reg.DX, Reg.ES);
        else
          asm.Mov(Reg.DX, Reg.DS);
        asm.Mov(Reg.CX, fixedString.Length);
        asm.Call(this._rt.StrMem);
        break;

      default:
        this.Unsupported(at, $"load of {type}");
        break;
    }
  }

  /// <summary>Stores the evaluation registers into <paramref name="place"/>; the value must already be coerced.</summary>
  private void EmitStorePlace(Place place, PbType type, Expression at) {
    var asm = this._asm;
    switch (type) {
      case ScalarType { ByteSize: 1 }:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Byte), Reg.AL);
        break;

      case ScalarType { IsFloat: false, ByteSize: 2 }:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), Reg.AX);
        break;

      case ScalarType { IsFloat: false }:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(place.Cell, 2, OperandSize.Word), Reg.DX);
        break;

      case ScalarType { ByteSize: 4 }:
        asm.Fstp(Adjust(place.Cell, 0, OperandSize.Dword));
        break;

      case ScalarType { ByteSize: 8 }:
        asm.Fstp(Adjust(place.Cell, 0, OperandSize.Qword));
        break;

      case ScalarType:
        asm.Fstp(Adjust(place.Cell, 0, OperandSize.Tbyte));
        break;

      case StringType or FlexType:
        asm.Lea(Reg.BX, place.Cell);
        asm.Call(place.Far ? this._rt.StrAssignEs : this._rt.StrAssign);
        break;

      case FixedStringType fixedString:
        asm.Lea(Reg.DI, place.Cell);
        if (place.Far)
          asm.Mov(Reg.DX, Reg.ES);
        else
          asm.Mov(Reg.DX, Reg.DS);
        asm.Mov(Reg.CX, fixedString.Length);
        asm.Call(this._rt.StoreFixed);
        break;

      default:
        this.Unsupported(at, $"store of {type}");
        break;
    }
  }

  private void EmitAssign(AssignStmt a) {
    var targetType = model.TypeOf(a.Target);

    if (targetType is UdtType udt) {
      this.EmitBlockCopy(a.Target, a.Value, udt.Size, a.Position);
      return;
    }

    if (targetType is ArrayType) {
      this.Unsupported(a);
      return;
    }

    // evaluate the value first (it may clobber BX/ES), park it, then address the target
    this.EmitExpression(a.Value);
    this.Coerce(model.TypeOf(a.Value), targetType, a.Value);
    var kind = KindOf(targetType);

    if (kind == ValueKind.Int32)
      this._asm.Push(Reg.DX);
    if (kind != ValueKind.Float)
      this._asm.Push(Reg.AX);

    if (this.EmitPlace(a.Target) is not { } place) {
      // diagnostics already produced; rebalance the stack
      if (kind != ValueKind.Float)
        this._asm.Pop(Reg.AX);
      if (kind == ValueKind.Int32)
        this._asm.Pop(Reg.DX);
      return;
    }

    if (kind != ValueKind.Float)
      this._asm.Pop(Reg.AX);
    if (kind == ValueKind.Int32)
      this._asm.Pop(Reg.DX);
    this.EmitStorePlace(place, targetType, a.Value);
  }

  /// <summary>UDT-to-UDT assignment / LSET: a flat byte copy between two lvalues.</summary>
  private void EmitBlockCopy(Expression target, Expression value, int byteCount, SourcePosition position) {
    var asm = this._asm;
    if (this.EmitPlace(value) is not { } source) {
      this.Unsupported(position, "UDT copy source");
      return;
    }
    asm.Lea(Reg.SI, source.Cell);
    if (source.Far)
      asm.Mov(Reg.DX, Reg.ES);
    else
      asm.Mov(Reg.DX, Reg.DS);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);

    if (this.EmitPlace(target) is not { } dest) {
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      return;
    }
    asm.Lea(Reg.DI, dest.Cell);
    if (!dest.Far) {
      asm.Push(Reg.DS);
      asm.Pop(Reg.ES);
    }
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Mov(Reg.CX, byteCount);
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.DX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.DS);
  }

  private void EmitMidAssign(MidAssignStmt mid) {
    var asm = this._asm;
    if (model.TypeOf(mid.Target) is not StringType) {
      this.Unsupported(mid);
      return;
    }

    this.EmitExpression(mid.Start);
    this.Coerce(model.TypeOf(mid.Start), PbType.Integer, mid.Start);
    asm.Push(Reg.AX);
    if (mid.Length != null) {
      this.EmitExpression(mid.Length);
      this.Coerce(model.TypeOf(mid.Length), PbType.Integer, mid.Length);
    } else
      asm.Mov(Reg.AX, 0x7FFF);
    asm.Push(Reg.AX);
    this.EmitExpression(mid.Value);     // replacement handle
    asm.Push(Reg.AX);

    if (this.EmitPlace(mid.Target) is not { } place) {
      asm.Pop(Reg.AX);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.AX);
      return;
    }
    asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));   // raw target handle (mutated in place)
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Call(this._rt.MidSet);
  }

  private void EmitLsetRset(LsetRsetStmt ls) {
    var targetType = model.TypeOf(ls.Target);
    if (!ls.IsLeft) {
      this.Unsupported(ls);   // RSET
      return;
    }

    switch (targetType) {
      case FixedStringType: {
        // identical to assignment: copy + blank pad
        this.EmitAssign(new(ls.Position, ls.Target, ls.Value));
        break;
      }
      case UdtType target when model.TypeOf(ls.Value) is UdtType source:
        this.EmitBlockCopy(ls.Target, ls.Value, Math.Min(target.Size, source.Size), ls.Position);
        break;
      default:
        this.Unsupported(ls);
        break;
    }
  }

  #endregion
}
