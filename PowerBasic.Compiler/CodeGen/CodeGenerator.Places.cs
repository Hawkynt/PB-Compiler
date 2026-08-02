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

  /// <summary>
  /// The near cell a scalar write should target: an active inline frame slot (O6
  /// multi-statement inlining) when the symbol is a parameter/local/result of the
  /// procedure being inlined, otherwise its ordinary direct cell. Null when access
  /// needs a pointer.
  /// </summary>
  private Mem? InlineSlotCellOf(VariableSymbol s) {
    // a BYREF inlined parameter's slot holds a near pointer, not the value - it has no direct cell,
    // so the int16 read-modify-write / const-fold fast paths must reach it through EmitPlace instead
    if (this._inlineByRefParams?.Contains(s) == true)
      return null;
    if (this._inlineParamSlots is { } slots && slots.TryGetValue(s, out var slot))
      return slot.Cell;
    return this.TryDirectCell(s);
  }

  /// <summary>Direct cell of a symbol, or null when access needs a pointer (BYREF parameter).</summary>
  private Mem? TryDirectCell(VariableSymbol s) => s.Storage switch {
    VariableStorage.Global or VariableStorage.Static => Mem.At(this.SlotOf(s)),
    _ when s.IsArray => Mem.At(this.SlotOf(s)),     // local arrays use data slots (recursion caveat documented)
    VariableStorage.Local => Mem.At(Reg.BP, s.Offset),
    VariableStorage.Parameter when s.ByVal => Mem.At(Reg.BP, s.Offset),
    _ => null,
  };

  /// <summary>Rebuilds a memory operand with an extra displacement and explicit size (a base+index pair - e.g. a stack array's [BP+DI] - is preserved).</summary>
  private static Mem Adjust(Mem m, int delta, OperandSize size) {
    var result = (m.Base, m.Index, m.Label) switch {
      ({ } b, { } i, null) => Mem.At(b, i, m.Displacement + delta),
      ({ } b, _, { } l) => Mem.At(b, l, m.Displacement + delta),
      ({ } b, null, null) => Mem.At(b, m.Displacement + delta),
      (null, _, { } l) => Mem.At(l, m.Displacement + delta),
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
  /// <summary>
  /// True when computing the target's address needs no code at all - a plain variable with a
  /// direct cell. The value being stored can then stay in the accumulator across it; only a
  /// subscripted or indirect target has to be staged around, and staging a value that nothing
  /// touches is a PUSH/POP pair a hand-written version would never contain.
  /// </summary>
  private bool TargetNeedsNoAddressCode(Expression target) =>
    target is NameExpr && model.VariableBindings.TryGetValue(target, out var symbol)
      ? !symbol.IsArray && this.TryDirectCell(symbol) is not null && this.ResidentRegOf(symbol) is null
      : this.FoldsToConstantElement(target);   // a(7) on a static array is a displacement, not code

  private Place? EmitPlace(Expression e) {
    var asm = this._asm;
    switch (e) {
      case NameExpr n: {
        // copy propagation: a read remapped to the source of a removed copy y = x (the
        // source is a non-escaping tracked scalar, so it always has a direct cell)
        if (this._copyReads is { } copyReads && copyReads.TryGetValue(n, out var copySource)
            && this.TryDirectCell(copySource) is { } srcCell)
          return new(srcCell, false);
        if (!model.VariableBindings.TryGetValue(n, out var symbol)) {
          this.Unsupported(n, $"address of {n.Name}");
          return null;
        }
        // pb36 O6: inside an inlined body, a write to a parameter/local/result maps to
        // its per-inline frame slot (the callee has no real frame)
        if (this._inlineParamSlots is { } inlinedSlots && inlinedSlots.TryGetValue(symbol, out var inlinedSlot)) {
          // a BYREF receiver (THIS): the slot holds a near pointer, so reach the storage through it
          if (this._inlineByRefParams?.Contains(symbol) == true) {
            asm.Mov(Reg.BX, inlinedSlot.Cell);
            return new(Mem.At(Reg.BX), false);
          }
          return new(inlinedSlot.Cell, Far: false);
        }
        if (symbol.Storage == VariableStorage.Captured)        // pb36 closure: reach the captured local through the env pointer
          return this.EmitCapturedPlace(symbol);
        if (this.TryDirectCell(symbol) is { } cell)
          return new(cell, false);
        asm.Mov(Reg.BX, Mem.Word(Reg.BP, symbol.Offset));   // BYREF parameter: load the pointer
        return new(Mem.At(Reg.BX), false);
      }

      case MemberExpr m: {
        // QB-style dotted variable (binder flattened the chain into one symbol)
        if (model.VariableBindings.TryGetValue(m, out var flat)) {
          if (this.TryDirectCell(flat) is { } flatCell)
            return new(flatCell, false);
          asm.Mov(Reg.BX, Mem.Word(Reg.BP, flat.Offset));
          return new(Mem.At(Reg.BX), false);
        }
        if (model.TypeOf(m.Target) is not UdtType udt || udt.FindField(m.Member) is not { } field) {
          this.Unsupported(m, "member access");
          return null;
        }
        if (this.EmitPlace(m.Target) is not { } basePlace)
          return null;
        return basePlace with { Cell = Adjust(basePlace.Cell, field.Offset, OperandSize.None) };
      }

      case CallOrIndexExpr call when model.VariableBindings.TryGetValue(call, out var array):
        return this.EmitArrayElementPlace(call.Arguments, array, call);

      // indexing a flattened dotted array name (Max.X(i)) - a plain array element
      case IndexExpr { Target: MemberExpr mt } ix when model.VariableBindings.TryGetValue(mt, out var dottedArray) && dottedArray.Type is ArrayType:
        return this.EmitArrayElementPlace(ix.Arguments, dottedArray, ix);

      case IndexExpr ix:
        return this.EmitFieldArrayPlace(ix);

      case PtrDerefExpr deref:
        return this.EmitPtrDerefPlace(deref);

      default:
        this.Unsupported(e, "addressable expression");
        return null;
    }
  }

  /// <summary>
  /// pb36 closure capture: a captured local is reached through the lambda's far
  /// environment pointer (ES:BX). Both env kinds share this far access path - only the
  /// in-env displacement differs. Stack closure (non-escaping): the env IS the
  /// enclosing frame, so the variable sits at its enclosing-frame displacement (read
  /// by reference - the live local). Heap closure (escaping): the env is a heap record
  /// holding a by-value snapshot, so the variable sits at its env-record slot offset.
  /// </summary>
  private Place EmitCapturedPlace(VariableSymbol captured) {
    var lambda = this._currentProc!;
    this._asm.Les(Reg.BX, Mem.Dword(Reg.BP, lambda.ClosureEnvPtr!.Offset));   // ES:BX = env (frame or heap block)
    var inEnvOffset = lambda.IsEscapingClosure
      ? captured.EnvSlotOffset                            // heap env: the capture's slot in the snapshot record
      : lambda.Captures[captured.Offset].Offset;          // stack env: the capture's displacement in the enclosing frame
    return new(Mem.At(Reg.BX, inEnvOffset).Seg(Reg.ES), Far: true);
  }

  /// <summary>
  /// <c>@p</c> / <c>@p[i]</c>: evaluates the 32-bit seg:off pointer, adds
  /// i*SIZEOF(target) to the offset, and yields a far ES:BX place.
  /// </summary>
  private Place? EmitPtrDerefPlace(PtrDerefExpr deref) {
    var asm = this._asm;
    var targetType = model.TypeOf(deref);

    this.EmitExpression(deref.Pointer);

    if (deref.Index is { } index) {
      asm.Push(Reg.DX);
      asm.Push(Reg.AX);
      this.EmitInt16Argument(index);          // zero-based, ignores OPTION BASE
      asm.Mov(Reg.BX, Math.Max(targetType.Size, 1));
      asm.Imul(Reg.BX);                       // DX:AX = i * size (offset wraps at 64K like real mode)
      asm.Mov(Reg.BX, Reg.AX);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.DX);
      asm.Add(Reg.BX, Reg.AX);
    } else
      asm.Mov(Reg.BX, Reg.AX);

    asm.Mov(Reg.ES, Reg.DX);
    return new(Mem.At(Reg.BX).Seg(Reg.ES), Far: true);
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
      case ScalarType { ByteSize: 1 } b1:
        // pb36 C1 ($CPU 80386): one MOVZX/MOVSX load replaces the MOV+extend pair
        if (this.Optimize && this.Cpu386) {
          if (b1.Signed)
            asm.Movsx(Reg.AX, Adjust(place.Cell, 0, OperandSize.Byte));
          else
            asm.Movzx(Reg.AX, Adjust(place.Cell, 0, OperandSize.Byte));
          break;
        }
        asm.Mov(Reg.AL, Adjust(place.Cell, 0, OperandSize.Byte));
        if (b1.Signed)
          asm.Cbw();               // SByte: sign-extend AL -> AX
        else
          asm.Xor(Reg.AH, Reg.AH);  // BYTE: zero-extend AL -> AX
        break;

      case ScalarType { IsFloat: false, ByteSize: 2 }:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        break;

      case ScalarType { IsFloat: false, ByteSize: 8 }: // QUAD rides the x87 stack
        asm.Fild(Adjust(place.Cell, 0, OperandSize.Qword));
        break;

      case BcdType { IsFixedPoint: true }: // FIX: scaled int64 / 10^pbvFixDigits
        asm.Fild(Adjust(place.Cell, 0, OperandSize.Qword));
        asm.Call(this._asm.Lbl("rt_fixdn"));
        break;

      case BcdType: // BCD: EXT-backed 10-byte cell
        asm.Fld(Adjust(place.Cell, 0, OperandSize.Tbyte));
        break;

      case ScalarType { IsFloat: false } or PointerType:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        asm.Mov(Reg.DX, Adjust(place.Cell, 2, OperandSize.Word));
        break;

      case ProcPtrType: // fat closure: code far ptr in AX:DX, env far ptr in BX:CX
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
        asm.Mov(Reg.DX, Adjust(place.Cell, 2, OperandSize.Word));
        asm.Mov(Reg.BX, Adjust(place.Cell, 4, OperandSize.Word));
        asm.Mov(Reg.CX, Adjust(place.Cell, 6, OperandSize.Word));
        break;

      case MbfType { IsDouble: false }: // BASICA/GW-BASIC SINGLE: convert MBF32 -> IEEE32, then onto the x87
        this.EmitMbfSingleLoad(place.Cell);
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

      case AsciizType asciiz:
        asm.Lea(Reg.SI, place.Cell);
        if (place.Far)
          asm.Mov(Reg.DX, Reg.ES);
        else
          asm.Mov(Reg.DX, Reg.DS);
        asm.Mov(Reg.CX, asciiz.Length);
        asm.Call(this._rt.AsciizLoad);
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

      case ScalarType { IsFloat: false, ByteSize: 8 }: // QUAD rides the x87 stack
        asm.Fistp(Adjust(place.Cell, 0, OperandSize.Qword));
        break;

      case BcdType { IsFixedPoint: true }: // FIX: round to pbvFixDigits decimals, store scaled
        asm.Call(this._asm.Lbl("rt_fixup"));
        asm.Fistp(Adjust(place.Cell, 0, OperandSize.Qword));
        break;

      case BcdType:
        asm.Fstp(Adjust(place.Cell, 0, OperandSize.Tbyte));
        break;

      case ScalarType { IsFloat: false } or PointerType:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(place.Cell, 2, OperandSize.Word), Reg.DX);
        break;

      case ProcPtrType: // fat closure: code far ptr from AX:DX, env far ptr from BX:CX
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), Reg.AX);
        asm.Mov(Adjust(place.Cell, 2, OperandSize.Word), Reg.DX);
        asm.Mov(Adjust(place.Cell, 4, OperandSize.Word), Reg.BX);
        asm.Mov(Adjust(place.Cell, 6, OperandSize.Word), Reg.CX);
        break;

      case MbfType { IsDouble: false }: // BASICA/GW-BASIC SINGLE: narrow to IEEE32, convert to MBF32, store
        this.EmitMbfSingleStore(place);
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

      case AsciizType asciiz:
        asm.Lea(Reg.DI, place.Cell);
        if (place.Far)
          asm.Mov(Reg.DX, Reg.ES);
        else
          asm.Mov(Reg.DX, Reg.DS);
        asm.Mov(Reg.CX, asciiz.Length);
        asm.Call(this._rt.AsciizStore);
        break;

      default:
        this.Unsupported(at, $"store of {type}");
        break;
    }
  }

  // -------- Microsoft Binary Format single (BASICA / GW-BASIC) --------
  // MBF32 storage layout (little-endian bytes): [0..1] mantissa low 16, [2] = sign
  // (bit 7) | mantissa[22:16], [3] = biased-128 exponent (0 means the value is 0).
  // IEEE32 and MBF32 carry the same 23-bit fraction, so the only differences are the
  // exponent bias (IEEE_exp = MBF_exp - 2) and where the sign bit sits. The value
  // computes on the x87 as usual; these convert at the cell boundary. Shifts use CL
  // (the only 8086 variable shift); the cell is read before BX is reused, and the
  // store preserves a BX-based cell address across the conversion.

  private void EmitMbfSingleLoad(Mem cell) {
    var asm = this._asm;
    asm.Mov(Reg.AX, Adjust(cell, 0, OperandSize.Word));   // mantissa low 16
    asm.Mov(Reg.DX, Adjust(cell, 2, OperandSize.Word));   // DL = sign|mantissa hi, DH = MBF exponent
    var nonzero = asm.DefineLabel();
    var noSign = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Or(Reg.DH, Reg.DH);                                // exponent 0 -> the value is 0.0
    asm.Jnz(nonzero);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Jmp(done);
    asm.MarkLabel(nonzero);
    asm.Mov(Reg.CH, Reg.DL);                               // keep the sign byte
    asm.And(Reg.DL, (Imm)0x7F);                            // mantissa[22:16]
    asm.Sub(Reg.DH, (Imm)2);                               // IEEE biased exponent
    asm.Xor(Reg.BX, Reg.BX);
    asm.Mov(Reg.BL, Reg.DH);
    asm.Mov(Reg.CL, (Imm)7);
    asm.Shl(Reg.BX, Reg.CL);                               // exponent into bits 7..14
    asm.Or(Reg.BL, Reg.DL);                                // mantissa hi into bits 0..6
    asm.Test(Reg.CH, (Imm)0x80);
    asm.Jz(noSign);
    asm.Or(Reg.BH, (Imm)0x80);                             // sign into bit 15
    asm.MarkLabel(noSign);
    asm.Mov(Reg.DX, Reg.BX);                               // IEEE high word (AX still holds the low word)
    asm.MarkLabel(done);
    asm.Mov(Mem.Word(this._scratch), Reg.AX);
    asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
    asm.Fld(Mem.Dword(this._scratch));
  }

  private void EmitMbfSingleStore(Place place) {
    var asm = this._asm;
    asm.Fstp(Mem.Dword(this._scratch));                    // narrow the x87 value to IEEE single
    var bxBased = place.Cell.Base == Reg.BX;               // a computed/byref/far cell holds its address in BX
    if (bxBased)
      asm.Push(Reg.BX);                                    // the conversion reuses BX as scratch
    asm.Mov(Reg.AX, Mem.Word(this._scratch));             // mantissa low 16
    asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));          // sign | exponent | mantissa hi
    var zero = asm.DefineLabel();
    var noSign = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Mov(Reg.BX, Reg.DX);
    asm.Mov(Reg.CL, (Imm)7);
    asm.Shr(Reg.BX, Reg.CL);                               // exponent (bits 7..14) into BL
    asm.And(Reg.BX, (Imm)0xFF);
    asm.Or(Reg.BL, Reg.BL);                                // IEEE exponent 0 -> MBF is 0
    asm.Jz(zero);
    asm.And(Reg.DL, (Imm)0x7F);                            // mantissa[22:16] (drops the exponent's low bit)
    asm.Test(Reg.DH, (Imm)0x80);                           // sign
    asm.Jz(noSign);
    asm.Or(Reg.DL, (Imm)0x80);
    asm.MarkLabel(noSign);
    asm.Add(Reg.BL, (Imm)2);                               // MBF biased exponent
    asm.Mov(Reg.DH, Reg.BL);                               // exponent into byte 3
    asm.Jmp(done);
    asm.MarkLabel(zero);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Xor(Reg.DX, Reg.DX);
    asm.MarkLabel(done);
    if (bxBased)
      asm.Pop(Reg.BX);
    asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), Reg.AX);
    asm.Mov(Adjust(place.Cell, 2, OperandSize.Word), Reg.DX);
  }

  /// <summary>
  /// pb36 O5/O8: <c>acc = acc OP rhs</c> where <c>acc</c> is a register-resident 16-bit variable
  /// and <c>rhs</c> reaches memory in one operand - a direct cell or a static array element. The
  /// ALU op then targets the resident register itself, so the value never travels through AX:
  /// <c>ADD DI,[BX+disp]</c> rather than <c>MOV AX,DI / ADD AX,[BX+disp] / MOV DI,AX</c>.
  ///
  /// Only the unchecked path qualifies. Under <c>$ERROR OVERFLOW</c> the trap belongs to the
  /// operation, and the JNO guard would have to be threaded through here too; the ordinary
  /// load/op/store path already carries it.
  /// </summary>
  private bool TryEmitResidentReadModifyWrite(AssignStmt a, VariableSymbol target, Reg accumulator) {
    if (!this.Optimize || this.CheckOverflow || this.CheckNumeric || accumulator.IsDword())
      return false;
    if (model.TypeOf(a.Target) is not ScalarType { IsFloat: false, ByteSize: 2 })
      return false;
    if (a.Value is not BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract
        or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor } bin)
      return false;
    // the accumulator must be the LEFT operand: these ops are read-modify-write on it, and
    // subtraction is not commutative
    if (bin.Left is not NameExpr leftName
        || !model.VariableBindings.TryGetValue(leftName, out var leftSym)
        || !ReferenceEquals(leftSym, target))
      return false;

    var rhs = this.TryInt16MemOperand(bin.Right, PbType.Integer)
      ?? this.FuseArrayElementOperand(bin.Right, bin.Left, PbType.Integer);
    if (rhs is not { } operand)
      return false;

    var asm = this._asm;
    switch (bin.Op) {
      case BinaryOp.Add: asm.Add(accumulator, operand); break;
      case BinaryOp.Subtract: asm.Sub(accumulator, operand); break;
      case BinaryOp.And: asm.And(accumulator, operand); break;
      case BinaryOp.Or: asm.Or(accumulator, operand); break;
      default: asm.Xor(accumulator, operand); break;
    }
    return true;
  }

  /// <summary>
  /// pb36 O8: <c>target = &lt;integral constant&gt;</c> where the target's address costs no code -
  /// the constant is written as an immediate instead of being staged through the accumulator.
  ///
  /// The constant is the very one <see cref="TryEmitFolded"/> would have loaded (same folder, same
  /// <see cref="WrapToType"/>), and the value's kind must already match the target's, so the
  /// <see cref="Coerce"/> the ordinary path would run is a no-op. Under those two conditions the
  /// bytes reaching memory are identical to the load-then-store sequence.
  /// </summary>
  private bool TryEmitConstantStore(AssignStmt a, PbType targetType) {
    if (!this.Optimize)
      return false;
    if (targetType is not ScalarType { IsFloat: false, ByteSize: 1 or 2 or 4 } target)
      return false;
    if (model.TypeOf(a.Value) is not ScalarType valueType)
      return false;
    if (this._cseMarks?.ContainsKey(a.Value) == true)
      return false;                                // the value comes out of a CSE slot, not a literal
    if (this.OptFolder.TryFold(a.Value) is not { Integer: { } raw } || !this.FoldsWithoutWrap(a.Value))
      return false;
    // a wider or float-promoted constant reaches the cell through a conversion; that conversion can
    // trap under $ERROR NUMERIC/OVERFLOW, so only the unchecked build may pre-compute it
    var direct = !valueType.IsFloat && KindOf(valueType) == KindOf(target);
    if (!direct && (this.CheckNumeric || this.CheckOverflow))
      return false;
    if (!this.TargetWriteEmitsNoAddressCode(a.Target))
      return false;
    if (this.EmitPlace(a.Target) is not { Far: false } place)
      return false;

    var asm = this._asm;
    // exactly what the load-convert-store path would leave in the cell: a 1/2-byte target wraps,
    // a 4-byte signed one takes the x87's integer-indefinite pattern when the value cannot fit
    var value = StoreFoldedPromoted(raw, target, valueType.IsFloat);
    switch (target.ByteSize) {
      case 1:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Byte), (Imm)(byte)value);
        break;
      case 2:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), (Imm)(ushort)value);
        break;
      default:
        asm.Mov(Adjust(place.Cell, 0, OperandSize.Word), (Imm)(ushort)value);
        asm.Mov(Adjust(place.Cell, 2, OperandSize.Word), (Imm)(ushort)(value >> 16));
        break;
    }
    return true;
  }

  private void EmitAssign(AssignStmt a) {
    var targetType = model.TypeOf(a.Target);

    // pb36 O5: the loop accumulator lives in a register - compute the value into
    // AX (the SI-clean modular int16 path) and write the register, not the cell
    if (a.Target is NameExpr regTarget
        && model.VariableBindings.TryGetValue(regTarget, out var regSym)
        && this.ResidentRegOf(regSym) is { } accReg) {
      if (accReg.IsDword()) {
        // a LONG resident in a 32-bit register (EDI under $CPU 80386): compute DX:AX, pack into it
        this.EmitExpression(a.Value);
        this.Coerce(model.TypeOf(a.Value), PbType.Long, a.Value);
        this._asm.Mov(Mem.Word(this._scratch), Reg.AX);
        this._asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
        this._asm.Mov(accReg, Mem.Dword(this._scratch));
        return;
      }
      // "acc = acc OP <memory>" writes the resident register directly: ADD DI,[BX+disp] instead
      // of loading the register into AX, operating there and copying it back. This is the last
      // step of the accumulate-over-an-array loop, and the difference between what the compiler
      // emits and what a person would write by hand.
      if (this.TryEmitResidentReadModifyWrite(a, regSym, accReg))
        return;
      if (model.TypeOf(a.Value) is ScalarType { IsFloat: true } && this.IsModularInt16Tree(a.Value, 0))
        this.EmitModularInt16(a.Value);
      else {
        this.EmitExpression(a.Value);
        this.Coerce(model.TypeOf(a.Value), PbType.Integer, a.Value);
      }
      this._asm.Mov(accReg, Reg.AX);
      return;
    }

    // pb36 O15/O2: a copy of a non-string location onto itself moves identical
    // bytes - a pure no-op. Excludes dynamic-string-bearing types (their
    // assignment frees/reallocates handles, which a self-copy must not skip).
    if (this.Optimize && !EmbedsStringHandle(targetType)
        && targetType is not (StringType or FlexType)
        && this.IsSameLvalue(a.Target, a.Value))
      return;

    // pb36 O8: target = target OP <const|cell> on a non-resident int16 direct cell becomes a
    // memory-destination read-modify-write (ADD [target],x / INC [target]) - no load/op/store
    if (this.TryEmitInt16ReadModifyWrite(a))
      return;

    // pb36 O8: a constant into a cell is one store of an immediate - MOV WORD PTR [x],7 rather
    // than MOV AX,7 / MOV [x],AX. It is shorter, one instruction fewer, and leaves AX alone
    if (this.TryEmitConstantStore(a, targetType))
      return;

    if (targetType is UdtType udt) {
      this.EmitBlockCopy(a.Target, a.Value, udt.Size, a.Position);
      return;
    }

    // pb36 wide integers: convert between an emulated multi-word value and the native scalars
    if (targetType is WideIntType wt) {
      this.EmitWideStore(wt, a);
      return;
    }
    if (model.TypeOf(a.Value) is WideIntType srcWide && targetType is ScalarType { IsFloat: false } narrowTarget) {
      this.EmitWideTruncate(srcWide, narrowTarget, a.Target, a.Value);
      return;
    }

    // FIX literal stores round DECIMALLY at compile time (genuine PBC converts
    // the literal text: 2.555 -> 2.56 even though the binary double is below .555)
    if (targetType is BcdType { IsFixedPoint: true } && TryLiteralValue(a.Value) is { } fixLiteral) {
      var scaled = (long)Math.Round((decimal)fixLiteral * 100m, MidpointRounding.AwayFromZero);
      this._asm.Fild(Mem.Qword(this.QuadConstOf(scaled)));
      if (this.EmitPlace(a.Target) is { } fixPlace)
        this._asm.Fistp(Adjust(fixPlace.Cell, 0, OperandSize.Qword));
      else
        this._asm.Fstp(St.St0);
      return;
    }

    if (targetType is ArrayType) {
      this.Unsupported(a);
      return;
    }

    // pb36 O9 string-temp reuse: a self-concat `s$ = s$ + rhs` (append) or `s$ = rhs + s$`
    // (prepend) passes s$'s handle straight to StrCat - which copies both operands into the
    // result, then frees both - and stores the result, skipping the redundant StrDup of s$
    // (StrCat would copy it again anyway) and the StrAssign free (StrCat already freed the old
    // s$). The other operand is a string literal or bare variable, so it is barrier-free and
    // cannot change s$ before the concat. The string value is identical -> byte-identical.
    if (this.Optimize && targetType is StringType
        && a.Target is NameExpr selfTarget
        && model.VariableBindings.TryGetValue(selfTarget, out var selfSym)
        && a.Value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Concat, Left: { } concatLeft, Right: { } concatRight }
        && this.TryDirectCell(selfSym) is { } selfCell) {
      bool IsSelf(Expression e) => e is NameExpr && model.VariableBindings.TryGetValue(e, out var s) && ReferenceEquals(s, selfSym);
      bool IsBarrierFreeStr(Expression e) => e is StringLiteralExpr or NameExpr && model.TypeOf(e) is StringType;
      // pick the s$ side (passed directly) and the other side (evaluated/dup'd); when both are
      // s$ (self-double) treat the left as s$ so the right is dup'd before s$ is freed
      var selfIsLeft = IsSelf(concatLeft) && IsBarrierFreeStr(concatRight);
      var selfIsRight = !selfIsLeft && IsSelf(concatRight) && IsBarrierFreeStr(concatLeft);
      if (selfIsLeft || selfIsRight) {
        var asm = this._asm;
        var other = selfIsLeft ? concatRight : concatLeft;
        // pb36 O9 in-place: `s$ = s$ + "literal"` appends the literal bytes straight after s$'s
        // data when s$ is the topmost heap block (rt_strcatlit grows it in place, same handle),
        // turning an O(n) build loop O(n) total - no per-append realloc/copy of the whole string.
        // The literal needs no heap temp, so s$ stays topmost across iterations. rt_strcatlit
        // falls back to StrMem+StrCat when s$ is not topmost, so the result is always identical.
        if (selfIsLeft && other is StringLiteralExpr { Value: { Length: > 0 } litText }) {
          asm.Mov(Reg.AX, selfCell.WithSize(OperandSize.Word));  // AX = s$ handle
          asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(litText))); // DS:SI = literal bytes
          asm.Mov(Reg.CX, (Imm)litText.Length);
          asm.Call(this._rt.StrCatLit);                          // AX = result (grown s$ or new)
          asm.Mov(selfCell.WithSize(OperandSize.Word), Reg.AX);
          return;
        }
        // pb36 O9 in-place: `s$ = s$ + v$` (v$ a bare string variable) appends v$'s bytes
        // straight after s$'s data when s$ is topmost - the source is read as its RAW handle (no
        // StrDup temp, so s$ stays topmost) and copied heap-to-heap, then v$ is left intact.
        // rt_strcatvar also covers self-double `s$ = s$ + s$` and falls back to StrDup+StrCat
        // when s$ is not topmost, so the result is always identical.
        if (selfIsLeft && other is NameExpr otherName
            && model.VariableBindings.TryGetValue(otherName, out var otherSym)
            && model.TypeOf(otherName) is StringType
            && this.TryDirectCell(otherSym) is { } otherCell) {
          asm.Mov(Reg.AX, selfCell.WithSize(OperandSize.Word));  // AX = s$ handle
          asm.Mov(Reg.DX, otherCell.WithSize(OperandSize.Word)); // DX = v$ raw handle (no dup)
          asm.Call(this._rt.StrCatVar);
          asm.Mov(selfCell.WithSize(OperandSize.Word), Reg.AX);
          return;
        }
        // emit operands left-to-right (genuine order); s$ is read directly, the other is dup'd
        if (selfIsLeft) {
          asm.Mov(Reg.AX, selfCell.WithSize(OperandSize.Word));   // left = s$ handle
          asm.Push(Reg.AX);
          this.EmitExpression(other);                            // right -> handle in AX
          asm.Mov(Reg.DX, Reg.AX);
          asm.Pop(Reg.AX);
        } else {
          this.EmitExpression(other);                            // left -> handle in AX
          asm.Push(Reg.AX);
          asm.Mov(Reg.DX, selfCell.WithSize(OperandSize.Word));   // right = s$ handle
          asm.Pop(Reg.AX);
        }
        asm.Call(this._rt.StrCat);                               // AX = result; frees s$ and the other temp
        asm.Mov(selfCell.WithSize(OperandSize.Word), Reg.AX);    // store the new handle (old already freed)
        return;
      }
    }

    // $OPTIMIZE SPEED: v = v +/- const on a direct int16 cell is one ALU op
    if (this.OptimizeSpeed && !this.CheckOverflow && !this.CheckNumeric
        && targetType is ScalarType { IsFloat: false, ByteSize: 2 }
        && a.Target is NameExpr targetName
        && model.VariableBindings.TryGetValue(targetName, out var tSym)
        && this.InlineSlotCellOf(tSym) is { } tCell
        && a.Value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract, Left: NameExpr vLeft, Right: IntegerLiteralExpr { Value: >= short.MinValue and <= short.MaxValue } vConst } vBin
        && model.VariableBindings.TryGetValue(vLeft, out var vSym)
        && ReferenceEquals(vSym, tSym)) {
      if (vBin.Op == BinaryOp.Add)
        this._asm.Add(tCell.WithSize(OperandSize.Word), (Imm)(int)vConst.Value);
      else
        this._asm.Sub(tCell.WithSize(OperandSize.Word), (Imm)(int)vConst.Value);
      return;
    }

    // pb36 promotion lowering: a +,-,* tree over 16-bit integral leaves stored
    // into a 16-bit integral target computes bit-identically in modular 16-bit
    // ALU (low bits of the exact x87 value ARE the modular result), so the
    // whole FPU round-trip disappears; checked arithmetic never reaches here
    // because the binder keeps it integral with its own JO semantics
    if (this.Optimize && !this.CheckOverflow && !this.CheckNumeric
        && targetType is ScalarType { IsFloat: false, ByteSize: <= 2 }
        && model.TypeOf(a.Value) is ScalarType { IsFloat: true }
        && this.IsModularInt16Tree(a.Value, 0)) {
      this.EmitModularInt16(a.Value);
      var simple16 = this.TargetNeedsNoAddressCode(a.Target);
      if (!simple16)
        this._asm.Push(Reg.AX);              // a subscripted target's address code may clobber AX
      if (this.EmitPlace(a.Target) is { } modularPlace) {
        if (!simple16)
          this._asm.Pop(Reg.AX);
        this.EmitStorePlace(modularPlace, targetType, a.Target);
      } else if (!simple16)
        this._asm.Pop(Reg.AX);
      return;
    }

    // ... and the same for a 32-bit target: PB promotes LONG arithmetic to DOUBLE, so without
    // this every "l& = l& + k&" pays FILD / the x87 op / FISTP plus a memory staging cell at
    // each end for what the integer ALU does in two instructions
    if (this.Optimize && !this.CheckOverflow && !this.CheckNumeric
        && targetType is ScalarType { IsFloat: false, ByteSize: 4 } int32Target
        && model.TypeOf(a.Value) is ScalarType { IsFloat: true }
        && (this.IsModularInt32Tree(a.Value, int32Target.Signed)
            || this.IsGuardedInt32AddSub(a.Value, int32Target.Signed))) {
      var needsSaturationGuard = !this.IsModularInt32Tree(a.Value, int32Target.Signed);
      this.EmitModularInt32(a.Value);
      if (needsSaturationGuard)
        this.EmitInt32SaturationGuard();
      var simple32 = this.TargetNeedsNoAddressCode(a.Target);
      if (!simple32) {
        this._asm.Push(Reg.DX);              // a subscripted target's address code may clobber the pair
        this._asm.Push(Reg.AX);
      }
      if (this.EmitPlace(a.Target) is { } modular32Place) {
        if (!simple32) {
          this._asm.Pop(Reg.AX);
          this._asm.Pop(Reg.DX);
        }
        this.EmitStorePlace(modular32Place, targetType, a.Target);
      } else if (!simple32) {
        this._asm.Pop(Reg.AX);
        this._asm.Pop(Reg.DX);
      }
      return;
    }

    // evaluate the value first (it may clobber BX/ES), park it, then address the target.
    // pb36: a direct-cell target needs NO address computation (EmitPlace emits no code for it),
    // so the value in AX/DX is never disturbed and the park is pure waste - skip it.
    this.EmitExpression(a.Value);
    this.Coerce(model.TypeOf(a.Value), targetType, a.Value);
    var kind = KindOf(targetType);
    var park = kind != ValueKind.Float && !this.TargetWriteEmitsNoAddressCode(a.Target);

    if (park) {
      if (kind == ValueKind.Int32)
        this._asm.Push(Reg.DX);
      this._asm.Push(Reg.AX);
    }

    if (this.EmitPlace(a.Target) is not { } place) {
      // diagnostics already produced; rebalance the stack
      if (park) {
        this._asm.Pop(Reg.AX);
        if (kind == ValueKind.Int32)
          this._asm.Pop(Reg.DX);
      }
      return;
    }

    if (park) {
      this._asm.Pop(Reg.AX);
      if (kind == ValueKind.Int32)
        this._asm.Pop(Reg.DX);
    }
    this.EmitStorePlace(place, targetType, a.Value);
  }

  /// <summary>
  /// pb36 wide integers: stores into a multi-word target. <c>wide = wideVar</c> copies the common low
  /// words and sign-/zero-extends the rest; <c>wide = narrowExpr</c> puts the native value in AX/DX:AX
  /// and extends. (A wide arithmetic right-hand side is a follow-up - rejected for now.)
  /// </summary>
  private void EmitWideStore(WideIntType wt, AssignStmt a) {
    if (this.EmitPlace(a.Target) is not { } dst) {
      this.Unsupported(a);
      return;
    }
    var asm = this._asm;
    var vt = model.TypeOf(a.Value);
    Mem Word(Mem cell, int w) => Adjust(cell, w * 2, OperandSize.Word);

    // a compile-time integer constant stores its (sign-extended) words directly - covers any
    // literal/equate value up to 64 bits regardless of how the binder typed the expression
    if (this.OptFolder.TryFold(a.Value) is { Integer: { } constant }) {
      var fill = (ushort)(constant < 0 && wt.Signed ? 0xFFFF : 0x0000);
      for (var k = 0; k < wt.Words; ++k) {
        var word = k < 4 ? (ushort)(constant >> (16 * k)) : fill;
        asm.Mov(Word(dst.Cell, k), (Imm)word);
      }
      return;
    }

    if (vt is WideIntType srcW) {
      // wide = a +/- b : an ADC/SBB chain from the low word up (carry/borrow propagates through the words)
      if (a.Value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract, Left: { } bl, Right: { } br } bin
          && model.TypeOf(bl) is WideIntType && model.TypeOf(br) is WideIntType) {
        if (this.EmitPlace(bl) is not { } lhs || this.EmitPlace(br) is not { } rhs) {
          this.Unsupported(a.Value, "wide-integer operand without a memory location");
          return;
        }
        var add = bin.Op == BinaryOp.Add;
        for (var k = 0; k < wt.Words; ++k) {
          asm.Mov(Reg.AX, Word(lhs.Cell, k));
          if (k == 0) {
            if (add) asm.Add(Reg.AX, Word(rhs.Cell, 0)); else asm.Sub(Reg.AX, Word(rhs.Cell, 0));
          } else {
            if (add) asm.Adc(Reg.AX, Word(rhs.Cell, k)); else asm.Sbb(Reg.AX, Word(rhs.Cell, k));
          }
          asm.Mov(Word(dst.Cell, k), Reg.AX);
        }
        return;
      }
      if (a.Value is BinaryExpr or UnaryExpr) {
        this.Unsupported(a.Value, "this wide-integer operation (a follow-up increment)");
        return;
      }
      if (this.EmitPlace(a.Value) is not { } src) {
        this.Unsupported(a.Value, "wide-integer value without a memory location");
        return;
      }
      var common = Math.Min(wt.Words, srcW.Words);
      for (var k = 0; k < common; ++k) {
        asm.Mov(Reg.AX, Word(src.Cell, k));
        asm.Mov(Word(dst.Cell, k), Reg.AX);
      }
      if (wt.Words > common) {
        if (srcW.Signed) {
          asm.Mov(Reg.AX, Word(src.Cell, srcW.Words - 1));
          asm.Cwd();                          // DX = 0xFFFF if the source's top word is negative, else 0
        } else {
          asm.Xor(Reg.DX, Reg.DX);
        }
        for (var k = common; k < wt.Words; ++k)
          asm.Mov(Word(dst.Cell, k), Reg.DX);
      }
      return;
    }

    if (vt is ScalarType { IsFloat: false, ByteSize: <= 4 } narrow) {
      this.EmitExpression(a.Value);
      this.Coerce(vt, narrow, a.Value);
      var srcWords = narrow.ByteSize <= 2 ? 1 : 2;   // AX, or DX:AX
      asm.Mov(Word(dst.Cell, 0), Reg.AX);
      if (srcWords == 2)
        asm.Mov(Word(dst.Cell, 1), Reg.DX);
      if (wt.Words > srcWords) {
        if (narrow.Signed) {
          if (srcWords == 2)
            asm.Mov(Reg.AX, Reg.DX);          // sign of the high word
          asm.Cwd();                          // DX = sign fill
        } else {
          asm.Xor(Reg.DX, Reg.DX);
        }
        for (var k = srcWords; k < wt.Words; ++k)
          asm.Mov(Word(dst.Cell, k), Reg.DX);
      }
      return;
    }

    this.Unsupported(a.Value, "wide-integer assignment from this value (a follow-up increment)");
  }

  /// <summary>pb36 wide integers: <c>narrow = wideVar</c> truncates the wide value to its low word(s).</summary>
  private void EmitWideTruncate(WideIntType srcW, ScalarType narrow, Expression target, Expression wideValue) {
    _ = srcW;
    if (this.EmitPlace(wideValue) is not { } src) {
      this.Unsupported(wideValue, "wide-integer value without a memory location");
      return;
    }
    var asm = this._asm;
    Mem Word(Mem cell, int w) => Adjust(cell, w * 2, OperandSize.Word);
    asm.Mov(Reg.AX, Word(src.Cell, 0));
    if (narrow.ByteSize > 2)
      asm.Mov(Reg.DX, Word(src.Cell, 1));
    if (this.EmitPlace(target) is { } dst) {
      if (narrow.ByteSize == 1) {
        asm.Mov(Adjust(dst.Cell, 0, OperandSize.Byte), Reg.AL);
      } else {
        asm.Mov(Word(dst.Cell, 0), Reg.AX);
        if (narrow.ByteSize > 2)
          asm.Mov(Word(dst.Cell, 1), Reg.DX);
      }
    }
  }

  /// <summary>
  /// True when storing to <paramref name="target"/> needs no address computation - EmitPlace
  /// returns its cell without emitting any instruction (a direct-cell variable, a copy-prop
  /// remap to a direct cell, or an inlined-frame slot). So the value already in AX/DX survives
  /// EmitPlace and need not be parked. A captured local (env-pointer load) or a BYREF parameter
  /// (pointer load) DOES emit address code, as do array/member/pointer targets.
  /// </summary>
  private bool TargetWriteEmitsNoAddressCode(Expression target) {
    if (target is not NameExpr n)
      return this.FoldsToConstantElement(target);  // a(7) on a static array folds to a displacement
    if (this._copyReads is { } cr && cr.TryGetValue(n, out var src) && this.TryDirectCell(src) != null)
      return true;
    if (!model.VariableBindings.TryGetValue(n, out var symbol))
      return false;
    if (this._inlineByRefParams?.Contains(symbol) == true)
      return false;                              // BYREF receiver: EmitPlace loads the pointer (address code)
    if (this._inlineParamSlots?.ContainsKey(symbol) == true)
      return true;
    if (symbol.Storage == VariableStorage.Captured)
      return false;
    return this.TryDirectCell(symbol) != null;
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
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.DX);
    this.EmitBlockMove(byteCount);
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

  /// <summary>Compile-time value of a numeric literal (incl. a leading minus), else null.</summary>
  private static double? TryLiteralValue(Expression e) => e switch {
    FloatLiteralExpr f => f.Value,
    IntegerLiteralExpr i => i.Value,
    UnaryExpr { Op: UnaryOp.Negate } u when TryLiteralValue(u.Operand) is { } inner => -inner,
    _ => null,
  };

  private void EmitLsetRset(LsetRsetStmt ls) {
    var asm = this._asm;
    var targetType = model.TypeOf(ls.Target);

    switch (targetType) {
      case StringType or FlexType: {
        // dynamic string: justify in place within the current length
        this.EmitExpression(ls.Value);
        asm.Push(Reg.AX);
        if (this.EmitPlace(ls.Target) is not { } place) {
          asm.Pop(Reg.AX);
          return;
        }
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));  // raw target handle
        asm.Pop(Reg.DX);
        asm.Mov(Reg.BX, ls.IsLeft ? 0 : 1);
        asm.Call(this._rt.Justify);
        break;
      }

      case FixedStringType fixedString when !ls.IsLeft: { // RSET: right-justified store
        this.EmitExpression(ls.Value);
        asm.Push(Reg.AX);
        if (this.EmitPlace(ls.Target) is not { } place) {
          asm.Pop(Reg.AX);
          return;
        }
        asm.Lea(Reg.DI, place.Cell);
        asm.Mov(Reg.DX, place.Far ? Reg.ES : Reg.DS);
        asm.Mov(Reg.CX, fixedString.Length);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.StoreFixedR);
        break;
      }

      case FixedStringType: // LSET: identical to assignment (copy + blank pad)
        this.EmitAssign(new(ls.Position, ls.Target, ls.Value));
        break;

      case UdtType target when model.TypeOf(ls.Value) is UdtType source:
        this.EmitBlockCopy(ls.Target, ls.Value, Math.Min(target.Size, source.Size), ls.Position);
        break;

      default:
        this.Unsupported(ls);
        break;
    }
  }

  #endregion

  /// <summary>
  /// The exactness budget for a float-promoted integer tree: the x87 computes with a 64-bit
  /// mantissa, so an integer of magnitude below 2^63 travels through it EXACTLY - and the low
  /// bits of an exact value are precisely what the modular integer ALU would have produced.
  /// Past that the FPU rounds and the low bits are no longer the modular result, so the tree
  /// must stay on the promoted path.
  /// </summary>
  private const int ModularMantissaBits = 63;

  /// <summary>
  /// The smallest bit count <c>b</c> in the modular tree's signed convention (a value fits
  /// <c>b</c> bits when it lies in <c>[-2^b, 2^b - 1]</c>) that holds every value of
  /// <c>[lo, hi]</c>. Matches the leaf convention exactly - a full signed 16-bit range gives 15,
  /// a full WORD range gives 16 - so substituting it for a tighter proven range never over-claims.
  /// </summary>
  private static int RangeBits(long lo, long hi) {
    var b = 0;
    while (b < 63 && !(lo >= -(1L << b) && hi <= (1L << b) - 1))
      ++b;
    return b;
  }

  /// <summary>
  /// A conservative bound, in bits, on the magnitude any value in a <c>+ - *</c> (and unary
  /// negate) tree of integral leaves can reach - or null when the tree is not of that shape.
  /// Leaves contribute their type's width, an add/subtract one carry bit over the wider side,
  /// and a multiply the sum of both. Bounds are monotone up the tree, so checking the root
  /// against <see cref="ModularMantissaBits"/> proves exactness at every node.
  /// </summary>
  private int? ModularTreeBits(Expression e, int maxLeafBytes, int depth) {
    if (depth > 16)
      return null;
    if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: var size, Signed: var signed } && size <= maxLeafBytes) {
      // The leaf occupies at most its type's width - but a PROVEN range needs fewer bits, and
      // that is what lets a multiply of two small-ranged operands demote to a native IMUL instead
      // of the FPU round-trip. E.g. (i% AND 255) * (j% AND 255) is <= 65025, so its product fits
      // int32 with room to spare; without the range it counts 31+31 bits and stays on the x87.
      // Sound because RangeBits is an over-approximation and we take the tighter of the two - the
      // demotion still only fires when the whole tree provably fits the target (the <=31 gate).
      var typeBits = signed ? size * 8 - 1 : size * 8;
      // Only tighten in the int32 context (maxLeafBytes == 4). There the tree promotes to DOUBLE
      // and the demotion gate is <= 31 bits, so a newly-qualifying result is < 2^31, exact in the
      // 2^53 mantissa - unconditionally safe. The int16 context promotes to SINGLE (2^24 mantissa)
      // and accepts up to the mantissa-bit bound, where a range-widened deep product could round;
      // leaving those leaves at their type width keeps that path exactly as it was.
      return maxLeafBytes >= 4 && this.IndexRangeOf(e) is { } r
        ? Math.Min(typeBits, RangeBits(r.Lo, r.Hi))
        : typeBits;
    }
    switch (e) {
      case UnaryExpr { Op: UnaryOp.Negate } u:
        return this.ModularTreeBits(u.Operand, maxLeafBytes, depth + 1);
      case BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } b: {
        if (this.ModularTreeBits(b.Left, maxLeafBytes, depth + 1) is not { } l
            || this.ModularTreeBits(b.Right, maxLeafBytes, depth + 1) is not { } r)
          return null;
        var bits = b.Op == BinaryOp.Multiply ? l + r : Math.Max(l, r) + 1;
        return bits > ModularMantissaBits ? null : bits;
      }
      default:
        return null;
    }
  }

  /// <summary>
  /// True for a +,-,* (and unary negate) tree whose leaves are all 16-bit-or-narrower integral
  /// expressions AND whose values provably stay inside the x87's exact-integer range - the
  /// float-promoted result's low 16 bits then equal the modular 16-bit ALU result at every depth.
  /// </summary>
  private bool IsModularInt16Tree(Expression e, int depth) => this.ModularTreeBits(e, 2, depth) is not null;

  /// <summary>
  /// The 32-bit form: a +,-,* tree over 32-bit-or-narrower integral leaves, stored into a 32-bit
  /// integral target. PB promotes such a tree to DOUBLE, so without this it round-trips through
  /// the x87 - <c>FILD</c>, the operation, <c>FISTP</c>, and a memory staging cell at each end -
  /// where the plain 32-bit ALU would do.
  ///
  /// The budget is much tighter than the 16-bit form's, and for a different reason. Storing a
  /// float to a 2-byte integral WRAPS, so there the only question is whether the x87 held the
  /// value exactly. Storing one to a 4-byte integral does NOT wrap: an out-of-range value comes
  /// back as the x87's integer-indefinite sentinel (8000_0000h), which no amount of modular
  /// arithmetic reproduces. So the tree only qualifies when its value provably cannot leave the
  /// destination's range - then exact, modular and stored all coincide. That still covers the
  /// everyday shapes (narrow leaves widened into a LONG, anything the interval lattice bounds);
  /// a genuinely 32-bit-wide sum keeps the promoted path and its saturation.
  /// </summary>
  private bool IsModularInt32Tree(Expression e, bool targetSigned) =>
    this.ModularTreeBits(e, 4, 0) is { } bits && bits <= (targetSigned ? 31 : 32);

  /// <summary>
  /// The one shape worth rescuing from the promoted path even though it CAN leave the
  /// destination's range: a single <c>+</c> or <c>-</c> whose operands are each exactly
  /// representable (their own bound fits int32). The true result then needs exactly 33 bits, so
  /// the ALU's overflow flag says precisely whether it left int32 - and the emitter reproduces
  /// the x87's integer-indefinite sentinel in three instructions rather than paying the whole
  /// FILD/op/FISTP round-trip. This is the everyday <c>total&amp; = total&amp; + delta&amp;</c>.
  /// </summary>
  private bool IsGuardedInt32AddSub(Expression e, bool targetSigned) =>
    targetSigned && e is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } b
    && this.ModularTreeBits(b.Left, 4, 1) is <= 31
    && this.ModularTreeBits(b.Right, 4, 1) is <= 31;

  /// <summary>Evaluates a modular tree into AX with plain 16-bit ALU ops.</summary>
  /// <summary>
  /// pb36 O8: <c>target = target OP x</c> (a read-modify-write of a non-resident 2-byte integer
  /// direct cell, x a constant or another direct cell) compiles to one memory-destination ALU op
  /// (<c>ADD [target],imm</c>, <c>INC/DEC [target]</c> for +/-1, or <c>MOV AX,[x]; ADD [target],AX</c>)
  /// instead of loading the target, operating, and storing it back. Modular 16-bit, so it matches
  /// the generic path bit-for-bit. Gated off under $ERROR OVERFLOW/NUMERIC (the load/op/store path
  /// carries the trap). SUB only when the target is the minuend; ADD/AND/OR/XOR are commutative.
  /// </summary>
  private bool TryEmitInt16ReadModifyWrite(AssignStmt a) {
    if (!this.Optimize || this.CheckOverflow || this.CheckNumeric)
      return false;
    if (a.Target is not NameExpr tn
        || model.TypeOf(a.Target) is not ScalarType { IsFloat: false, ByteSize: 2 }
        || !model.VariableBindings.TryGetValue(tn, out var tsym)
        || this.ResidentRegOf(tsym) != null
        || this.TryInt16MemOperand(tn, PbType.Integer) is not { } tcell)
      return false;
    if (a.Value is not BinaryExpr bin
        || bin.Op is not (BinaryOp.Add or BinaryOp.Subtract or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor))
      return false;

    Expression other;
    if (this.IsSameLvalue(bin.Left, a.Target))
      other = bin.Right;
    else if (bin.Op is not BinaryOp.Subtract && this.IsSameLvalue(bin.Right, a.Target))
      other = bin.Left;
    else
      return false;

    var asm = this._asm;
    if (this.TryModularFoldConst(other, out var raw)) {
      var imm = (short)(raw & 0xFFFF);
      switch (bin.Op) {
        case BinaryOp.Add when imm == 1: asm.Inc(tcell); break;
        case BinaryOp.Add when imm == -1: asm.Dec(tcell); break;
        case BinaryOp.Add: asm.Add(tcell, (Imm)imm); break;
        case BinaryOp.Subtract when imm == 1: asm.Dec(tcell); break;
        case BinaryOp.Subtract when imm == -1: asm.Inc(tcell); break;
        case BinaryOp.Subtract: asm.Sub(tcell, (Imm)imm); break;
        case BinaryOp.And: asm.And(tcell, (Imm)imm); break;
        case BinaryOp.Or: asm.Or(tcell, (Imm)imm); break;
        default: asm.Xor(tcell, (Imm)imm); break;
      }
      return true;
    }
    if (this.TryInt16MemOperand(other, PbType.Integer) is { } ocell) {
      asm.Mov(Reg.AX, ocell);
      switch (bin.Op) {
        case BinaryOp.Add: asm.Add(tcell, Reg.AX); break;
        case BinaryOp.Subtract: asm.Sub(tcell, Reg.AX); break;
        case BinaryOp.And: asm.And(tcell, Reg.AX); break;
        case BinaryOp.Or: asm.Or(tcell, Reg.AX); break;
        default: asm.Xor(tcell, Reg.AX); break;
      }
      return true;
    }
    return false;
  }

  private void EmitModularInt16(Expression e) {
    // pb36 O3 (modular context): a marked composite modular subtree defines or
    // reloads its 16-bit slot; the value is always one word in AX
    if (this._cseMarks is { } marks && marks.TryGetValue(e, out var mark)) {
      var slot = this.CseSlot(mark.Slot);
      if (!mark.IsDefine) {
        this._asm.Mov(Reg.AX, slot);
        return;
      }
      this.EmitModularInt16Core(e);
      this._asm.Mov(slot, Reg.AX);
      return;
    }
    this.EmitModularInt16Core(e);
  }

  /// <summary>
  /// Evaluates a modular 32-bit tree into DX:AX with the plain integer ALU. Leaves fall back to
  /// the ordinary emitter; every operator goes through <see cref="EmitInt32Op"/>, so this path
  /// inherits the whole 32-bit repertoire - the $CPU 80386 register forms, the O16 narrowing to
  /// a 16-bit multiply, the immediate folding - instead of duplicating any of it.
  /// </summary>
  private void EmitModularInt32(Expression e) {
    var asm = this._asm;
    if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 4 } leafType) {
      this.EmitExpression(e);
      this.Coerce(leafType, PbType.Long, e);
      return;
    }
    switch (e) {
      case UnaryExpr u:
        this.EmitModularInt32(u.Operand);
        asm.Not(Reg.DX);
        asm.Neg(Reg.AX);
        asm.Sbb(Reg.DX, -1);
        break;

      case BinaryExpr b:
        // a 4-byte direct cell on the right loads straight into BX:CX - no push/pop staging
        if (this.TryInt32MemOperand(b.Right) is { } rmem) {
          this.EmitModularInt32(b.Left);
          asm.Mov(Reg.BX, rmem);
          asm.Mov(Reg.CX, Adjust(rmem, 2, OperandSize.Word));
          this.EmitInt32Op(b);
          break;
        }
        this.EmitModularInt32(b.Left);
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        this.EmitModularInt32(b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Reg.CX, Reg.DX);
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
        this.EmitInt32Op(b);
        break;
    }
  }

  /// <summary>
  /// Reproduces what an out-of-range float-to-LONG store does: the x87 writes its
  /// integer-indefinite value (8000_0000h) rather than wrapping. The preceding 32-bit ADD/ADC
  /// (or SUB/SBB) leaves OF set exactly when the true 33-bit result left the signed 32-bit
  /// range, so one branch decides it.
  /// </summary>
  private void EmitInt32SaturationGuard() {
    var asm = this._asm;
    var inRange = asm.DefineLabel();
    asm.Jno(inRange);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Mov(Reg.DX, unchecked((short)0x8000));
    asm.MarkLabel(inRange);
  }

  private void EmitModularInt16Core(Expression e) {
    // pb36 O17: a modular tree that SCCP proved constant collapses to one load
    if (this.TryEmitModularProvenConstant(e))
      return;

    var asm = this._asm;
    if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 2 } leafType) {
      this.EmitExpression(e);
      this.Coerce(leafType, PbType.Integer, e);
      return;
    }
    switch (e) {
      case UnaryExpr u:
        this.EmitModularInt16(u.Operand);
        asm.Neg(Reg.AX);
        break;
      case BinaryExpr b:
        // pb36 O4: v * const lowers to a shift/add chain (SPEED) instead of IMUL
        if (b.Op == BinaryOp.Multiply && this.TryEmitModularConstMul(b))
          break;
        // pb36 O8: v +/- const becomes one immediate ALU op (smaller and faster)
        if (b.Op is BinaryOp.Add or BinaryOp.Subtract && this.TryEmitModularConstAddSub(b))
          break;
        // pb36 O8: a direct-memory right operand reads straight into the ALU op (ADD AX,[mem])
        // instead of being staged through BX (push left / eval right / mov bx / pop). An array
        // element fuses the same way - its address goes into BX first, then the left is loaded -
        // which is what turns "acc = acc + a(i)" into a load and an add.
        if (b.Op is BinaryOp.Add or BinaryOp.Subtract
            && (this.TryInt16MemOperand(b.Right, PbType.Integer)
                ?? this.FuseArrayElementOperand(b.Right, b.Left, PbType.Integer)) is { } rmem) {
          this.EmitModularInt16(b.Left);
          if (b.Op == BinaryOp.Add)
            asm.Add(Reg.AX, rmem);
          else
            asm.Sub(Reg.AX, rmem);
          break;
        }
        // pb36 O8: a direct-memory right operand of a multiply reads straight into the one-operand
        // IMUL (DX:AX = AX * [mem]; the low word in AX is the modular int16 product) instead of being
        // staged through BX (push left / eval right / mov bx / pop). The modular path carries no
        // overflow check, so the low 16 bits are all that matter - identical to the staged IMUL BX.
        if (b.Op == BinaryOp.Multiply && this.TryInt16MemOperand(b.Right, PbType.Integer) is { } mmem) {
          this.EmitModularInt16(b.Left);
          asm.Imul(mmem);
          break;
        }
        this.EmitModularInt16(b.Left);
        asm.Push(Reg.AX);
        this.EmitModularInt16(b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Pop(Reg.AX);
        switch (b.Op) {
          case BinaryOp.Add: asm.Add(Reg.AX, Reg.BX); break;
          case BinaryOp.Subtract: asm.Sub(Reg.AX, Reg.BX); break;
          default: asm.Imul(Reg.BX); break;
        }
        break;
    }
  }
}
