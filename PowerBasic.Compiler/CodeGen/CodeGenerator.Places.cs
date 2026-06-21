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
      case ScalarType { ByteSize: 1 }:
        asm.Mov(Reg.AL, Adjust(place.Cell, 0, OperandSize.Byte));
        asm.Xor(Reg.AH, Reg.AH);
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

    if (targetType is UdtType udt) {
      this.EmitBlockCopy(a.Target, a.Value, udt.Size, a.Position);
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
      this._asm.Push(Reg.AX);                // the target's subscripts may clobber AX
      if (this.EmitPlace(a.Target) is { } modularPlace) {
        this._asm.Pop(Reg.AX);
        this.EmitStorePlace(modularPlace, targetType, a.Target);
      } else
        this._asm.Pop(Reg.AX);
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
  /// True when storing to <paramref name="target"/> needs no address computation - EmitPlace
  /// returns its cell without emitting any instruction (a direct-cell variable, a copy-prop
  /// remap to a direct cell, or an inlined-frame slot). So the value already in AX/DX survives
  /// EmitPlace and need not be parked. A captured local (env-pointer load) or a BYREF parameter
  /// (pointer load) DOES emit address code, as do array/member/pointer targets.
  /// </summary>
  private bool TargetWriteEmitsNoAddressCode(Expression target) {
    if (target is not NameExpr n)
      return false;
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
  /// True for a +,-,* (and unary negate) tree whose leaves are all 16-bit-or-
  /// narrower integral expressions - the float-promoted result's low 16 bits
  /// equal the modular 16-bit ALU result at every depth.
  /// </summary>
  private bool IsModularInt16Tree(Expression e, int depth) {
    if (depth > 16)
      return false;
    if (model.TypeOf(e) is ScalarType { IsFloat: false, ByteSize: <= 2 })
      return true; // integral leaf of any shape - evaluated through the normal emitter
    return e switch {
      UnaryExpr { Op: UnaryOp.Negate } u => this.IsModularInt16Tree(u.Operand, depth + 1),
      BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } b =>
        this.IsModularInt16Tree(b.Left, depth + 1) && this.IsModularInt16Tree(b.Right, depth + 1),
      _ => false,
    };
  }

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
        // instead of being staged through BX (push left / eval right / mov bx / pop)
        if (b.Op is BinaryOp.Add or BinaryOp.Subtract && this.TryInt16MemOperand(b.Right, PbType.Integer) is { } rmem) {
          this.EmitModularInt16(b.Left);
          if (b.Op == BinaryOp.Add)
            asm.Add(Reg.AX, rmem);
          else
            asm.Sub(Reg.AX, rmem);
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
