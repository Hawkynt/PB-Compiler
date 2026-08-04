using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private void EmitExpression(Expression expression) {
    // pb36 O3: a marked common subexpression either defines its slot (emit the
    // tree once, then stash) or reloads it (no recompute, identical value)
    if (this._cseMarks is { } marks && marks.TryGetValue(expression, out var mark)
        && model.TypeOf(expression) is ScalarType { IsFloat: false }) {
      var asm = this._asm;
      var wide = model.TypeOf(expression) is ScalarType { ByteSize: 4 };
      var slot = this.CseSlot(mark.Slot);
      if (!mark.IsDefine) {
        asm.Mov(Reg.AX, slot);
        if (wide)
          asm.Mov(Reg.DX, Adjust(slot, 2, OperandSize.Word));
        return;
      }
      this.EmitExpressionCore(expression);
      asm.Mov(slot, Reg.AX);
      if (wide)
        asm.Mov(Adjust(slot, 2, OperandSize.Word), Reg.DX);
      return;
    }
    this.EmitExpressionCore(expression);
  }

  /// <summary>The frame cell of common-subexpression slot <paramref name="index"/> (4 bytes reserved each, below the locals).</summary>
  private Mem CseSlot(int index) => Mem.At(Reg.BP, -(this._frameLocalBytes + (index + 1) * 4)).WithSize(OperandSize.Word);

  /// <summary>
  /// pb36 O15/O2: structural lvalue identity - two expressions that designate
  /// the same, side-effect-free, statically-known storage. Used to fold
  /// self-compares and elide self-copies. Array/pointer indices must be equal
  /// constants and free of volatile reads; bound to the same variable symbol.
  /// </summary>
  private bool IsSameLvalue(Expression a, Expression b) {
    switch (a, b) {
      case (NameExpr, NameExpr):
        return model.VariableBindings.TryGetValue(a, out var sa)
          && model.VariableBindings.TryGetValue(b, out var sb)
          && ReferenceEqualityComparer.Instance.Equals(sa, sb)
          && !model.IntrinsicBindings.ContainsKey((Expression)a);
      case (MemberExpr ma, MemberExpr mb):
        return ma.Member.Equals(mb.Member, StringComparison.OrdinalIgnoreCase)
          && this.IsSameLvalue(ma.Target, mb.Target);
      case (CallOrIndexExpr ca, CallOrIndexExpr cb)
          when model.VariableBindings.TryGetValue(ca, out var aa) && model.VariableBindings.TryGetValue(cb, out var ab):
        return ReferenceEqualityComparer.Instance.Equals(aa, ab)
          && ca.Arguments.Count == cb.Arguments.Count
          && Enumerable.Range(0, ca.Arguments.Count).All(i => SameConstIndex(ca.Arguments[i], cb.Arguments[i]));
      default:
        return false;
    }

    bool SameConstIndex(Expression x, Expression y)
      => this.OptFolder.TryFold(x) is { Integer: { } ix }
        && this.OptFolder.TryFold(y) is { Integer: { } iy } && ix == iy;
  }

  private void EmitExpressionCore(Expression expression) {
    // pb36 interpolated string: emit the bound concatenation the binder desugared it to
    if (model.Desugared.TryGetValue(expression, out var desugared)) {
      this.EmitExpression(desugared);
      return;
    }

    // pb36 ENUM member: the binder resolved this node to a compile-time integer
    if (model.ResolvedConstants.TryGetValue(expression, out var enumConst) && model.TypeOf(expression) is ScalarType enumType) {
      this.EmitIntegralConstant(WrapToType(enumConst, enumType), KindOf(enumType));
      return;
    }

    // pb36 O17: SCCP-proven constant reads fold here (cross-block propagation)
    if (this.TryEmitProvenConstant(expression))
      return;

    var asm = this._asm;
    switch (expression) {
      case IntegerLiteralExpr i:
        // TB types integer literals beyond LONG as DOUBLE (no QUAD there)
        if (model.TypeOf(i) is ScalarType { IsFloat: true })
          asm.Fld(Mem.Qword(this.FloatConstOf(i.Value)));
        else
          this.EmitIntegralConstant(i.Value, KindOf(model.TypeOf(i)));
        break;

      case FloatLiteralExpr f: {
        // unsuffixed literals are SINGLE in PB: quantize so the single-precision
        // noise (0.1! = 0.100000001490116...) propagates exactly like genuine PBC
        var literal = model.TypeOf(f) is ScalarType { Kind: ScalarKind.Single } ? (float)f.Value : f.Value;
        asm.Fld(Mem.Qword(this.FloatConstOf(literal)));
        break;
      }

      case StringLiteralExpr s:
        this.EmitStringLiteral(s.Value);
        break;

      case NamedConstantExpr c: {
        if (model.Equates.TryGetValue(c.Name, out var v) && v.Text is { } text) {
          this.EmitStringLiteral(text);
          break;
        }
        this.EmitIntegralConstant(v.AsInteger, KindOf(model.TypeOf(c)));
        break;
      }

      case NameExpr n: {
        if (model.IntrinsicBindings.TryGetValue(n, out var bareIntrinsic)) {
          this.EmitIntrinsic(n, [], bareIntrinsic);
          break;
        }
        if (model.CallBindings.TryGetValue(n, out var fn)) {
          this.EmitCall(fn, [], wantResult: true, n.Position);
          break;
        }
        if (!model.VariableBindings.TryGetValue(n, out var symbol)) {
          this.Unsupported(n, $"unbound name {n.Name}");
          break;
        }
        if (symbol.Type is ArrayType) {
          this.Unsupported(n, "whole-array reference");
          break;
        }
        // pb36 O6: inside an inlined body, parameter reads come from the
        // argument temps instead of a (nonexistent) callee frame
        if (this._inlineParamSlots is { } inlined && inlined.TryGetValue(symbol, out var slot)) {
          // a BYREF scalar parameter's slot holds a pointer - read through it (EmitPlace loads it)
          if (this._inlineByRefParams?.Contains(symbol) == true)
            this.EmitLoadPlace(this.EmitPlace(n)!.Value, slot.Type, n);
          else
            this.EmitLoadPlace(new(slot.Cell, Far: false), slot.Type, n);
          break;
        }
        // pb36 O5: a variable resident in a register this loop (FOR counter in
        // SI, accumulator in DI) reads straight from the register
        if (this.ResidentRegOf(symbol) is { } residentReg) {
          if (residentReg.IsDword()) {
            // a LONG counter resident in a 32-bit register (ESI under $CPU 80386): split to DX:AX
            asm.Mov(Mem.Dword(this._scratch), residentReg);
            asm.Mov(Reg.AX, Mem.Word(this._scratch));
            asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
          } else
            asm.Mov(Reg.AX, residentReg);
          break;
        }
        // pb36 O18 (IPCP): a parameter that is the same constant at every call
        // site and never written reads as that literal
        if (this._ipcp is { } ipcp && ipcp.TryGetValue(symbol, out var constant)
            && symbol.Type is ScalarType ipcpType) {
          if (ipcpType.IsFloat)
            asm.Fld(Mem.Qword(this.FloatConstOf(ipcpType.ByteSize == 4 ? (float)constant.AsFloat : constant.AsFloat)));
          else
            this.EmitIntegralConstant(WrapToType(constant.AsInteger, ipcpType), KindOf(ipcpType));
          break;
        }
        if (this.EmitPlace(n) is { } place)
          this.EmitLoadPlace(place, symbol.Type, n);
        break;
      }

      case CallOrIndexExpr call:
        // pb36 O25: a pure-function call with all-constant arguments was evaluated at
        // compile time - emit the folded result and skip the call entirely
        if (this._pureFold is { } pf && pf.TryGetValue(call, out var pureResult)
            && model.TypeOf(call) is ScalarType { IsFloat: false } pureType) {
          this.EmitIntegralConstant(WrapToType(pureResult.AsInteger, pureType), KindOf(pureType));
          break;
        }
        if (model.ProcPtrCalls.TryGetValue(call, out var ptrSig))
          this.EmitProcPtrCall(call, ptrSig);
        else if (model.IntrinsicBindings.TryGetValue(call, out var intrinsic))
          this.EmitIntrinsic(call, call.Arguments, intrinsic);
        else if (model.VariableBindings.TryGetValue(call, out var array)) {
          if (call.Arguments.Count == 0) {
            this.Unsupported(call, "whole-array reference");
            break;
          }
          if (this.EmitPlace(call) is { } place)
            this.EmitLoadPlace(place, ((ArrayType)array.Type).Element, call);
        } else if (model.CallBindings.TryGetValue(call, out var proc))
          this.EmitCall(proc, model.ReorderedArguments.GetValueOrDefault(call) ?? call.Arguments, wantResult: true, call.Position);
        else
          this.Unsupported(call, $"call or index {call.Name}");
        break;

      case MemberExpr m:
        if (this.EmitPlace(m) is { } memberPlace)
          this.EmitLoadPlace(memberPlace, model.TypeOf(m), m);
        break;

      case IndexExpr ix:
        if (this.EmitPlace(ix) is { } indexPlace)
          this.EmitLoadPlace(indexPlace, model.TypeOf(ix), ix);
        break;

      case PtrDerefExpr deref:
        if (this.EmitPlace(deref) is { } derefPlace)
          this.EmitLoadPlace(derefPlace, model.TypeOf(deref), deref);
        break;

      case ByValArgExpr byVal: // outside an argument list the override is the identity
        this.EmitExpression(byVal.Value);
        break;

      case AnyMatchExpr any:  // the ANY flag itself is consumed by the intrinsic emitter
        this.EmitExpression(any.Value);
        break;

      case UnaryExpr u: // pb36 O1: pure integral expressions fold to one literal load
        if (!this.TryEmitFolded(u))
          this.EmitUnary(u);
        break;

      case BinaryExpr b:
        if (!this.TryEmitFolded(b))
          this.EmitBinary(b);
        break;

      case FileNumberExpr fn:
        this.EmitExpression(fn.Number);
        this.Coerce(model.TypeOf(fn.Number), PbType.Integer, fn.Number);
        break;

      case IfExpr ternary: // pb36 O1: a constant-condition ternary folds to its taken branch
        if (!this.TryEmitFolded(ternary))
          this.EmitTernaryIf(ternary);
        break;

      case LambdaExpr lambda when model.LambdaProcs.TryGetValue(lambda, out var lambdaProc):
        // the lambda value is a fat closure: far code pointer (AX:DX) of its lifted
        // proc (like CODEPTR32) plus a far environment pointer (BX:CX)
        this.EmitClosureEnv(lambdaProc);                  // BX:CX = env (built when capturing, else null)
        asm.Mov(Reg.AX, Imm.OffsetOf(this.ThunkOf(lambdaProc)));
        asm.Mov(Reg.DX, Reg.CS);
        break;

      default:
        this.Unsupported(expression, expression.GetType().Name);
        break;
    }
  }

  /// <summary>
  /// pb36 short-circuit ternary IF(cond, t, f): evaluates the condition, then only
  /// the taken branch; both branches are coerced to the result type so the value
  /// lands in the same place (AX / DX:AX / FPU / string handle in AX).
  /// </summary>
  private void EmitTernaryIf(IfExpr t) {
    var asm = this._asm;
    var resultType = model.TypeOf(t);
    var elseLabel = asm.DefineLabel();
    var endLabel = asm.DefineLabel();

    this.EmitConditionalBranch(t.Condition, elseLabel, whenFalse: true);
    this.EmitExpression(t.WhenTrue);
    this.Coerce(model.TypeOf(t.WhenTrue), resultType, t.WhenTrue);
    asm.Jmp(endLabel);

    asm.MarkLabel(elseLabel);
    this.EmitExpression(t.WhenFalse);
    this.Coerce(model.TypeOf(t.WhenFalse), resultType, t.WhenFalse);
    asm.MarkLabel(endLabel);
  }

  private void EmitStringLiteral(string text) {
    var asm = this._asm;
    if (text.Length == 0) {
      asm.Xor(Reg.AX, Reg.AX);
      return;
    }
    asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(text)));
    asm.Mov(Reg.CX, text.Length);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this._rt.StrMem);
  }

  private void EmitUnary(UnaryExpr u) {
    var asm = this._asm;
    // O0077: -(-x) is x - the two sign flips cancel exactly (FCHS then FCHS for floats, NEG then NEG
    // for integers, wrap-identical even at -32768). Guarded on both negations producing the same
    // type, so there is no rounding step between them; emit the inner value coerced to that type.
    if (this.Optimize && u.Op == UnaryOp.Negate
        && u.Operand is UnaryExpr { Op: UnaryOp.Negate, Operand: { } inner }
        && model.TypeOf(u).Equals(model.TypeOf(u.Operand))) {
      this.EmitExpression(inner);
      this.Coerce(model.TypeOf(inner), model.TypeOf(u), inner);
      return;
    }
    this.EmitExpression(u.Operand);
    var kind = KindOf(model.TypeOf(u.Operand));
    switch (u.Op, kind) {
      // PB-lineage promotion: integral negation bound to a float type goes
      // through the FPU so -N% with N% = -32768 yields 32768, not the wrap
      case (UnaryOp.Negate, ValueKind.Int16 or ValueKind.Int32) when model.TypeOf(u) is ScalarType { IsFloat: true } promoted:
        this.Coerce(model.TypeOf(u.Operand), promoted, u.Operand);
        asm.Fchs();
        break;
      case (UnaryOp.Negate, ValueKind.Int16):
        asm.Neg(Reg.AX);
        break;
      case (UnaryOp.Negate, ValueKind.Int32):
        asm.Not(Reg.DX);
        asm.Neg(Reg.AX);
        asm.Sbb(Reg.DX, -1);
        break;
      case (UnaryOp.Negate, ValueKind.Float):
      case (UnaryOp.Negate, ValueKind.Int64):
        asm.Fchs();
        break;
      case (UnaryOp.Not, ValueKind.Int16):
        asm.Not(Reg.AX);
        break;
      case (UnaryOp.Not, ValueKind.Int32):
        asm.Not(Reg.AX);
        asm.Not(Reg.DX);
        break;
      case (UnaryOp.Not, ValueKind.Int64):
        asm.Fistp(Mem.Qword(asm.Lbl("rt_q0")));
        asm.Call(this._rt.QuadNot);
        asm.Fild(Mem.Qword(asm.Lbl("rt_q0")));
        break;
      default:
        this.Unsupported(u, "unary op");
        break;
    }
  }

  private void EmitBinary(BinaryExpr b) {
    var asm = this._asm;
    var leftType = model.TypeOf(b.Left);
    var rightType = model.TypeOf(b.Right);
    var resultType = model.TypeOf(b);

    // pb36 scaled pointer arithmetic: ptr +* i / ptr -* i (offset-only, like @p[i])
    if (b.Op is BinaryOp.PointerAdd or BinaryOp.PointerSub) {
      this.EmitPointerArith(b, leftType);
      return;
    }

    // pb36 O16: a comparison of a FOR-counter range against a constant whose result is
    // invariant over the range folds to the constant boolean
    if (this.TryEmitRangeComparison(b))
      return;

    // pb36 O4: (x MOD 2^j) = 0 tests only whether the low j bits are zero, which x AND (2^j-1)
    // answers directly - the signed-modulo fixup (CWD / AND / ADD / AND / SUB, correcting the
    // sign for a negative dividend) does not change the zero-ness of the result, so it is dead
    // when the modulo is only compared to zero. The everyday even/odd test.
    if (this.TryEmitModuloZeroTest(b))
      return;

    // whole-value TYPE/UNION = / <> (PB 3.1): memcmp semantics
    if (leftType is UdtType leftUdt && rightType is UdtType) {
      // pb36 O15: a self-compare folds to its constant truth - memcmp of a
      // location against itself compares identical bytes and is always equal
      // (NaN-immune, unlike a value compare), so rec = rec is -1, rec <> rec 0
      if (this.Optimize && this.IsSameLvalue(b.Left, b.Right)) {
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? -1 : 0);
        return;
      }
      this.EmitUdtCompare(b, leftUdt.Size);
      return;
    }

    if (KindOf(leftType) == ValueKind.Str || KindOf(rightType) == ValueKind.Str) {
      this.EmitStringBinary(b);
      return;
    }

    // arithmetic runs in the result type; comparisons in the widest operand type
    var isComparison = b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual;
    PbType opType;
    bool unsignedCompare;
    if (isComparison) {
      var leftUnsigned = leftType is ScalarType { IsFloat: false, Signed: false };
      var rightUnsigned = rightType is ScalarType { IsFloat: false, Signed: false };
      var dwordOperand = (leftUnsigned && leftType.Size == 4) || (rightUnsigned && rightType.Size == 4);
      var widest = WidestOf(leftType, rightType);
      if (dwordOperand && widest is ScalarType { IsFloat: false, ByteSize: <= 4 }) {
        // a DWORD operand forces an unsigned 32-bit comparison even against a signed
        // operand: genuine PBC reads the signed side as unsigned (4000000000 > -1 is
        // FALSE), while a wider QUAD/float operand keeps the widened compare
        opType = PbType.Dword;
        unsignedCompare = true;
      } else if (leftUnsigned && rightUnsigned) {
        opType = leftType.Size > 2 || rightType.Size > 2 ? PbType.Dword : PbType.Word;
        unsignedCompare = true;
      } else {
        // a WORD/BYTE mixed with a signed type compares signed, but widened to the next
        // signed size that holds the unsigned operand (WORD->LONG, BYTE->INTEGER) so its
        // value stays positive (w?? = 50000 > -1 is TRUE, not a 16-bit -15536 > -1)
        opType = widest;
        if (leftUnsigned != rightUnsigned && widest is ScalarType { IsFloat: false, ByteSize: <= 4 }) {
          var unsignedOperand = leftUnsigned ? leftType : rightType;
          var promoted = unsignedOperand.Size == 1 ? PbType.Integer : PbType.Long;
          if (promoted.Size > ((ScalarType)widest).Size)
            opType = promoted;
        }
        unsignedCompare = false;
      }
    } else {
      opType = resultType;
      unsignedCompare = false;
    }

    // pb36 O16: an operation the value facts prove does nothing (or produces a constant)
    if (this.TryEmitFactRedundantOp(b, opType))
      return;

    // pb36 O4: x * 2^n as shifts (wrap-identical to the product's low bits)
    if (this.TryEmitStrengthReducedMultiply(b, opType))
      return;

    // pb36 O4: x \ 2^n and x MOD 2^n as shift/mask with PB truncation fix-up
    if (this.TryEmitStrengthReducedDivMod(b, opType))
      return;

    switch (KindOf(opType)) {
      case ValueKind.Int16:
        // pb36 O8: fold a constant operand into one immediate ALU op
        if (this.TryEmitInt16ConstBinary(b, opType, unsignedCompare))
          break;
        // $OPTIMIZE SPEED: x * 2^n inlines as shifts (no overflow checking applies)
        if (this.OptimizeSpeed && !this.CheckOverflow && b.Op == BinaryOp.Multiply
            && b.Right is IntegerLiteralExpr { Value: > 0 and <= 16384 } pot && long.IsPow2(pot.Value)) {
          this.EmitExpression(b.Left);
          this.Coerce(leftType, opType, b.Left);
          for (var shifts = System.Numerics.BitOperations.TrailingZeroCount((ulong)pot.Value); shifts > 0; --shifts)
            asm.Shl(Reg.AX, 1);
          break;
        }
        // pb36 O8: a comparison against a direct-memory right operand reads it straight into the
        // CMP (CMP AX,[mem]) instead of staging it through BX - then the usual SETcc/branch result.
        if (isComparison && this.TryInt16MemOperand(b.Right, opType) is { } cmem) {
          this.EmitExpression(b.Left);
          this.Coerce(leftType, opType, b.Left);
          asm.Cmp(Reg.AX, cmem);
          var (cmpJump, cmpCond) = Int16CompareSelector(b.Op, unsignedCompare);
          if (!this.TryEmitCompareAsBranch(b, cmpCond))
            this.EmitInt16CompareResult(cmpJump, cmpCond);
          break;
        }
        // pb36 O8: a same-width direct-memory right operand of a commutative/subtractive ALU op
        // is read straight into the instruction (ADD AX,[mem]) instead of being staged through BX
        // (push left / eval right / mov bx / pop) - one memory-operand instruction, no spill.
        // An ARRAY ELEMENT right operand fuses the same way: its address goes into BX first (which
        // needs AX), then the left operand is loaded, then one ADD AX,[BX+disp] does the work.
        // "acc = acc + a(i)" is the loop this exists for.
        if (b.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor
            && (this.TryInt16MemOperand(b.Right, opType)
                ?? this.FuseArrayElementOperand(b.Right, b.Left, opType)) is { } rmem) {
          this.EmitExpression(b.Left);
          this.Coerce(leftType, opType, b.Left);
          switch (b.Op) {
            case BinaryOp.Add:
              asm.Add(Reg.AX, rmem);
              if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
                this.EmitRaiseWhen(asm.Jno, 6);
              break;
            case BinaryOp.Subtract:
              asm.Sub(Reg.AX, rmem);
              if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
                this.EmitRaiseWhen(asm.Jno, 6);
              break;
            case BinaryOp.And: asm.And(Reg.AX, rmem); break;
            case BinaryOp.Or: asm.Or(Reg.AX, rmem); break;
            default: asm.Xor(Reg.AX, rmem); break;
          }
          break;
        }
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        asm.Push(Reg.AX);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Pop(Reg.AX);
        this.EmitInt16Op(b, unsignedCompare);
        break;

      case ValueKind.Int32:
        // pb36 O8: fold a constant operand into immediate pair ops
        if (this.TryEmitInt32ConstBinary(b, opType))
          break;
        // pb36: a 4-byte direct-cell right operand loads straight into BX:CX (no
        // push/pop staging of the left) - EmitInt32Op sees the same AX:DX/BX:CX
        // state, so it works for every 32-bit op and the output is unchanged.
        if (this.Optimize && this.TryInt32MemOperand(b.Right) is { } lo32) {
          this.EmitExpression(b.Left);
          this.Coerce(leftType, opType, b.Left);
          asm.Mov(Reg.BX, lo32);
          asm.Mov(Reg.CX, Adjust(lo32, 2, OperandSize.Word));
          this.EmitInt32Op(b, unsignedCompare, opType is ScalarType { IsFloat: false, Signed: false });
          break;
        }
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Reg.CX, Reg.DX);
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
        this.EmitInt32Op(b, unsignedCompare, opType is ScalarType { IsFloat: false, Signed: false });
        break;

      case ValueKind.Float:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        // pb36: x87 reads the right float operand from memory (FADD/FSUB/FMUL/FDIV
        // or FCOMP m32|m64) instead of FLD-ing it and popping. Order is preserved -
        // left is already in ST0, so FSUB/FDIV/FCOMP [right] = left OP right exactly
        // as the FsubP/FCOMPP path would. Power (^) stays on the runtime-call path.
        if (this.Optimize && this.TryFloatMemOperand(b.Right) is { } fmem && this.TryEmitFloatMemOp(b.Op, fmem))
          break;
        // pb36: a float op against a signed integer cell reads it with an x87 integer
        // memory operand (FIADD/FISUB/FIMUL/FIDIV m16|m32) - no AX load, no FILD scratch.
        if (this.Optimize && this.TryFloatIntMemOperand(b.Right) is { } imem && this.TryEmitFloatIntMemOp(b.Op, imem))
          break;
        // pb36: a float op against a float literal reads it from its data-segment QWORD
        // constant (FADD/FSUB/FMUL/FDIV/FCOMP qword [f_n]) instead of FLD const + pop.
        // Gated off Power (runtime call); the const is the SAME one the FLD path emits.
        if (this.Optimize && b.Op is not BinaryOp.Power && this.TryFloatConstMemOperand(b.Right) is { } kmem && this.TryEmitFloatMemOp(b.Op, kmem))
          break;
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        this.EmitFloatOp(b);
        break;

      case ValueKind.Int64:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        this.EmitInt64Op(b);
        break;

      default:
        this.Unsupported(b, "binary op on this type");
        break;
    }
  }

  /// <summary>
  /// QUAD arithmetic on the x87 stack (left ST1, right ST0): +, -, * and the
  /// comparisons are exact within 64-bit range; \, MOD and the bitwise family
  /// route through the 4-word rt_quad* routines (or the inline pb36 386 path).
  /// The float-typed operators (/, ^) never reach here - the binder types
  /// <c>QUAD /</c> as DOUBLE and <c>QUAD ^</c> as EXT, so they run on the float
  /// path; the default arm is a defensive guard for any future integral op.
  /// </summary>
  private void EmitInt64Op(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add: asm.Faddp(); break;
      case BinaryOp.Subtract: asm.Fsubp(); break;
      case BinaryOp.Multiply: asm.Fmulp(); break;
      case BinaryOp.Equal: this.EmitFloatCompare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitFloatCompare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitFloatCompare(asm => asm.Jb); break;
      case BinaryOp.Greater: this.EmitFloatCompare(asm => asm.Ja); break;
      case BinaryOp.LessEqual: this.EmitFloatCompare(asm => asm.Jbe); break;
      case BinaryOp.GreaterEqual: this.EmitFloatCompare(asm => asm.Jae); break;
      case BinaryOp.IntegerDivide: this.EmitQuadMemoryOp(this._rt.QuadDiv); break;
      case BinaryOp.Modulo: this.EmitQuadMemoryOp(this._rt.QuadMod); break;
      case BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Eqv or BinaryOp.Imp:
        // pb36 C1 ($CPU 80386): a 64-bit bitwise op runs inline as two 32-bit halves
        // instead of a runtime call - bitwise ops can't trap, so no error path is lost
        if (this.Optimize && this.Cpu386)
          this.EmitQuad386Bitwise(b.Op);
        else
          this.EmitQuadMemoryOp(b.Op switch {
            BinaryOp.And => this._rt.QuadAnd,
            BinaryOp.Or => this._rt.QuadOr,
            BinaryOp.Xor => this._rt.QuadXor,
            BinaryOp.Eqv => this._rt.QuadEqv,
            _ => this._rt.QuadImp,
          });
        break;
      default:
        asm.Fstp(St.St0);
        asm.Fstp(St.St0);
        this.Unsupported(b, $"QUAD {b.Op}");
        break;
    }
  }

  /// <summary>
  /// QUAD \, MOD and the bitwise family: spill ST1 (left) / ST0 (right) into
  /// the rt_q0/rt_q1 memory cells, run the 4-word routine, reload the result.
  /// </summary>
  private void EmitQuadMemoryOp(Label routine) {
    var asm = this._asm;
    asm.Fistp(Mem.Qword(asm.Lbl("rt_q1")));   // right (top of stack)
    asm.Fistp(Mem.Qword(asm.Lbl("rt_q0")));   // left
    asm.Call(routine);
    asm.Fild(Mem.Qword(asm.Lbl("rt_q0")));
  }

  /// <summary>
  /// pb36 C1 ($CPU 80386): a 64-bit bitwise op (AND/OR/XOR/EQV/IMP) done inline as two
  /// 32-bit halves in EAX, replacing the runtime QuadAnd/.. call. The operands are staged
  /// to rt_q0 (left) / rt_q1 (right) by the same FISTP pops, the result is built into rt_q0
  /// and loaded back with FILD. EQV = NOT(a XOR b), IMP = (NOT a) OR b - per-half, exactly
  /// the runtime's whole-word definition.
  /// </summary>
  private void EmitQuad386Bitwise(BinaryOp op) {
    var asm = this._asm;
    var sc = this._scratch;                 // own 16-byte scratch: [0..8) left, [8..16) right
    asm.Fistp(Mem.Qword(sc, 8));            // right
    asm.Fistp(Mem.Qword(sc, 0));            // left
    for (var off = 0; off <= 4; off += 4) {
      asm.Mov(Reg.EAX, Mem.Dword(sc, off));
      switch (op) {
        case BinaryOp.And: asm.And(Reg.EAX, Mem.Dword(sc, 8 + off)); break;
        case BinaryOp.Or: asm.Or(Reg.EAX, Mem.Dword(sc, 8 + off)); break;
        case BinaryOp.Xor: asm.Xor(Reg.EAX, Mem.Dword(sc, 8 + off)); break;
        case BinaryOp.Eqv: asm.Xor(Reg.EAX, Mem.Dword(sc, 8 + off)); asm.Not(Reg.EAX); break;
        case BinaryOp.Imp: asm.Not(Reg.EAX); asm.Or(Reg.EAX, Mem.Dword(sc, 8 + off)); break;
      }
      asm.Mov(Mem.Dword(sc, off), Reg.EAX);
    }
    asm.Fild(Mem.Qword(sc, 0));
  }

  /// <summary>
  /// pb36 <c>ptr +* i</c> / <c>ptr -* i</c>: moves the far pointer's offset by
  /// <c>i * targetSize</c> (segment fixed, offset wraps at 64K - the same real-mode
  /// scaling <c>@p[i]</c> uses); result DX:AX = seg:off.
  /// </summary>
  private void EmitPointerArith(BinaryExpr b, PbType pointerType) {
    var asm = this._asm;
    var size = Math.Max((pointerType as PointerType)?.Target.Size ?? 1, 1);
    this.EmitExpression(b.Left);     // DX:AX = seg:off of the pointer
    asm.Push(Reg.DX);
    asm.Push(Reg.AX);
    this.EmitInt16Argument(b.Right); // AX = index (16-bit)
    asm.Mov(Reg.BX, size);
    asm.Imul(Reg.BX);                // DX:AX = index * size
    asm.Mov(Reg.BX, Reg.AX);         // BX = low word = offset delta
    asm.Pop(Reg.AX);                 // AX = original offset
    asm.Pop(Reg.DX);                 // DX = original segment (preserved)
    if (b.Op == BinaryOp.PointerAdd)
      asm.Add(Reg.AX, Reg.BX);
    else
      asm.Sub(Reg.AX, Reg.BX);
  }

  /// <summary>Concatenation and bytewise comparisons over string temporaries (both operands consumed).</summary>
  /// <summary>
  /// True when evaluating <paramref name="e"/> yields a FRESH, DEAD, topmost heap string temp -
  /// one safe to extend in place (rt_strcatlit/rt_strcatvar reuse its handle). A concat result is
  /// such a temp, and so is a substring constructor (LEFT$/RIGHT$/MID$ -> StrLeft/StrRight/StrMid
  /// allocate a fresh result). A bare variable, array element or member is LIVE storage, not a
  /// dead temp, so it is excluded - mutating it in place would corrupt the program's value.
  /// (The runtime still checks topmost at run time and falls back to a copy, so this only governs
  /// which left shapes are eligible, never correctness.)
  /// </summary>
  private bool IsDeadStringTemp(Expression e) {
    if (e is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Concat })
      return true;
    return e is CallOrIndexExpr c
      && model.IntrinsicBindings.TryGetValue(c, out var intr)
      && intr.Name is "LEFT$" or "RIGHT$" or "MID$";
  }

  /// <summary>
  /// True when a string expression is side-effect-free, so the concat optimizer may evaluate it
  /// before its sibling without changing observable behavior. Conservative: a string literal, a
  /// plain (non-intrinsic) string variable read, or a concat of such - no function/intrinsic call
  /// (which could print, read volatile state, or modify a global) and no array/member/pointer.
  /// </summary>
  private bool IsReorderableStringExpr(Expression e) => e switch {
    StringLiteralExpr => true,
    NameExpr n => !model.IntrinsicBindings.ContainsKey(n)
      && model.TypeOf(n) is StringType,
    BinaryExpr { Op: BinaryOp.Add or BinaryOp.Concat } b =>
      this.IsReorderableStringExpr(b.Left) && this.IsReorderableStringExpr(b.Right),
    _ => false,
  };

  /// <summary>
  /// O24 multi-concat: flattens a maximal tree of string <c>&amp;</c>/<c>+</c> concatenations into its
  /// ordered list of leaf operands (left-to-right, in evaluation order). A child node is descended
  /// only when it is itself a string concat whose own operands are strings; everything else (a
  /// variable, literal, call, substring, etc.) is a leaf. Returns null when fewer than three leaves
  /// result (the two-operand StrCat / O9 in-place paths already cover those) or when the chain
  /// exceeds the runtime operand-list capacity.
  /// </summary>
  private List<Expression>? FlattenStringConcat(BinaryExpr root) {
    var leaves = new List<Expression>();
    if (!Collect(root))
      return null;
    return leaves.Count >= 3 ? leaves : null;

    bool Collect(Expression e) {
      if (e is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Concat } node
          && model.TypeOf(node.Left) is StringType && model.TypeOf(node.Right) is StringType)
        return Collect(node.Left) && Collect(node.Right);
      // O0178: an empty-literal operand contributes nothing - drop it from the chain (it has no
      // side effect) rather than staging a zero-length handle into rt_strcatn
      if (e is StringLiteralExpr { Value.Length: 0 })
        return true;
      // A leaf is only safe to pre-stage if evaluating a LATER operand cannot invalidate this one's
      // handle: a literal or a plain string variable yields a fresh, independent, freeable handle.
      // A call/intrinsic (function or LEFT$/MID$/...), array element, member or pointer deref reuses a
      // shared result buffer or borrows storage - staging it up-front then concatenating would alias
      // or corrupt it (e.g. f$()&g$()&h$() would read "hhh"). Those fall back to the pairwise/O9 path,
      // which consumes each operand immediately after evaluating it.
      if (!this.IsReorderableStringExpr(e))
        return false;
      leaves.Add(e);
      return leaves.Count <= Runtime.DosRuntime._STRCATN_MAX;
    }
  }

  /// <summary>
  /// O24 multi-concat single-allocation build. Evaluates every leaf operand of the flattened concat
  /// chain strictly left-to-right (each yields an owned handle in AX, so PB's evaluation order and
  /// any operand side effects are preserved exactly and each is evaluated once), stages the handles
  /// into rt_catlist, then calls rt_strcatn to sum the lengths, allocate the result ONCE and copy
  /// each operand in order - O(n) bytes and a single allocation instead of the pairwise chain's N-1
  /// allocations and O(n^2) copying. rt_strcatn consumes (frees) every operand handle, exactly as
  /// the equivalent StrCat chain would, so the result and the freed temporaries are identical.
  /// </summary>
  private void EmitMultiConcat(IReadOnlyList<Expression> leaves) {
    var asm = this._asm;
    var catlist = asm.Lbl("rt_catlist");
    for (var i = 0; i < leaves.Count; ++i) {
      this.EmitExpression(leaves[i]);                       // AX = owned handle of leaf i
      asm.Mov(Mem.Word(catlist, i * 2), Reg.AX);            // stage it (handles are stable indices)
    }
    asm.Mov(Reg.CX, (Imm)leaves.Count);
    asm.Call(this._rt.StrCatN);                             // AX = single-alloc concatenation
  }

  private void EmitStringBinary(BinaryExpr b) {
    var asm = this._asm;
    if (KindOf(model.TypeOf(b.Left)) != ValueKind.Str || KindOf(model.TypeOf(b.Right)) != ValueKind.Str) {
      this.Unsupported(b, "mixed string/numeric operands");
      return;
    }

    // O0178 empty-concat identity: x$ + "" and "" + x$ are x$. Reading any string expression yields
    // an OWNED handle (a variable StrDup's, a literal/call/temp is already owned) - exactly what
    // StrCat(x$, "") produces, but without the extra copy-and-free of a zero-length operand. The
    // empty literal has no side effect, so dropping it preserves PB's evaluation order either way.
    if (this.Optimize && b.Op is BinaryOp.Add or BinaryOp.Concat) {
      var other = b.Right is StringLiteralExpr { Value.Length: 0 } ? b.Left
        : b.Left is StringLiteralExpr { Value.Length: 0 } ? b.Right : null;
      if (other != null && model.TypeOf(other) is StringType) {
        this.EmitExpression(other);
        return;
      }
    }

    // pb36 O24 multi-concat: a chain/tree of three or more string concatenations builds with a
    // SINGLE heap allocation and one byte-copy per operand (rt_strcatn) instead of the pairwise
    // chain's N-1 allocations and O(n^2) copying. Operands are evaluated strictly left-to-right, so
    // every side effect (e.g. a function call returning a string) happens once, in PB's order. This
    // subsumes the two-operand O9 in-place paths below for qualifying chains; shorter chains and
    // shapes it declines still take those paths.
    if (this.Optimize && b.Op is BinaryOp.Add or BinaryOp.Concat
        && this.FlattenStringConcat(b) is { } leaves) {
      this.EmitMultiConcat(leaves);
      return;
    }

    // pb36 O9 concat-chain dead-temp reuse: in a left-associative chain `a$ + b$ + c$` the left
    // subexpression (`a$ + b$`) is a freshly-allocated, dead, topmost temp, so the next
    // barrier-free operand is appended to it IN PLACE (rt_strcatlit / rt_strcatvar) instead of
    // allocating a fresh StrCat result at every node - O(n) instead of O(n^2). Sound only because
    // the left is a temp (mutating a live variable would be wrong); the runtime checks topmost and
    // falls back to a copy otherwise, so the value is identical. Evaluating a literal / a variable's
    // raw handle allocates nothing, so the left temp stays topmost.
    if (this.Optimize && b.Op is BinaryOp.Add or BinaryOp.Concat
        && this.IsDeadStringTemp(b.Left)) {
      if (b.Right is StringLiteralExpr { Value: { Length: > 0 } litText }) {
        this.EmitExpression(b.Left);                          // AX = dead topmost temp
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(litText)));
        asm.Mov(Reg.CX, (Imm)litText.Length);
        asm.Call(this._rt.StrCatLit);
        return;
      }
      if (b.Right is NameExpr rightName
          && model.VariableBindings.TryGetValue(rightName, out var rightSym)
          && model.TypeOf(rightName) is StringType
          && this.TryDirectCell(rightSym) is { } rightCell) {
        this.EmitExpression(b.Left);                          // AX = dead topmost temp
        asm.Mov(Reg.DX, rightCell.WithSize(OperandSize.Word)); // DX = right operand's raw handle
        asm.Call(this._rt.StrCatVar);
        return;
      }
      // pb36 O9 (non-left-leaning): the RIGHT operand is itself a dead temp (e.g. (a$+b$)+(c$+d$)).
      // Evaluating left-then-right would leave the right temp topmost, so the left could not grow in
      // place. When BOTH operands are pure (side-effect-free), evaluate the RIGHT first, then the
      // LEFT - now the left dead temp is topmost - append the right temp's bytes in place, and free
      // the (now buried, dead) right temp. Reordering is sound only for pure operands; mutating the
      // left in place is sound because it is a dead temp; freeing the right matches what the plain
      // StrCat would have done. O(n) instead of O(n^2) for a balanced concat tree.
      if (this.IsDeadStringTemp(b.Right)
          && this.IsReorderableStringExpr(b.Left) && this.IsReorderableStringExpr(b.Right)) {
        this.EmitExpression(b.Right);                         // AX = right dead temp
        asm.Push(Reg.AX);
        this.EmitExpression(b.Left);                          // AX = left dead temp, now topmost
        asm.Pop(Reg.DX);
        asm.Push(Reg.DX);                                     // keep the right temp's handle to free
        asm.Call(this._rt.StrCatVar);                         // AX = left ++ right (in place)
        asm.Pop(Reg.DX);
        asm.Push(Reg.AX);                                     // save the result handle
        asm.Mov(Reg.AX, Reg.DX);
        asm.Call(this._rt.StrFree);                           // release the dead right temp
        asm.Pop(Reg.AX);
        return;
      }
    }

    // O0181: s$ = "" / s$ <> "" is emptiness, and a PB string is empty exactly when its handle is
    // zero (rt_stralloc returns 0 for length 0, so every empty string normalizes to handle 0). A
    // handle test replaces the whole rt_strcmp call - the commonest string comparison in DOS-era
    // code (every INPUT loop ends with one). Restricted to a string VARIABLE against the empty
    // literal: a variable read is just its handle with nothing to free, unlike a concat temp.
    if (this.Optimize && b.Op is BinaryOp.Equal or BinaryOp.NotEqual) {
      // dynamic StringType only - a FixedStringType/AsciizType is space/NUL-padded to its declared
      // length, so it compares by content (never handle-0) and must keep the StrCmp path
      var variable = b.Left is StringLiteralExpr { Value.Length: 0 } && b.Right is NameExpr && model.TypeOf(b.Right) is StringType ? b.Right
        : b.Right is StringLiteralExpr { Value.Length: 0 } && b.Left is NameExpr && model.TypeOf(b.Left) is StringType ? b.Left : null;
      if (variable != null) {
        this.EmitExpression(variable);           // AX = the string's handle
        asm.Or(Reg.AX, Reg.AX);                  // ZF set iff the handle is 0, i.e. the string is empty
        var (jump, condition) = b.Op == BinaryOp.Equal
          ? ((Func<Assembler, Action<Label>>)(a => a.Je), Condition.Equal)
          : (a => a.Jne, Condition.NotEqual);
        if (!this.TryEmitCompareAsBranch(b, condition))
          this.EmitInt16CompareResult(jump, condition);
        return;
      }
    }

    // O0297 char compare: `MID$(s$, i, 1) = "c"` (character matching) reads the byte directly
    // (rt_charat) and compares it to the literal's byte - no substring, no StrDup, no literal alloc,
    // no StrCmp. Only for a single non-NUL char literal (a NUL would alias the past-the-end 0).
    if (this.Optimize && b.Op is BinaryOp.Equal or BinaryOp.NotEqual
        && this.TryCharCompareOperands(b, out var chStr, out var chIdx, out var chByte)) {
      this.EmitExpression(chStr);
      asm.Push(Reg.AX);
      if (chIdx != null)
        this.EmitInt16Argument(chIdx);
      else
        asm.Mov(Reg.AX, 1);                       // LEFT$(s$, 1): the index is 1
      asm.Mov(Reg.CX, Reg.AX);
      asm.Pop(Reg.AX);
      asm.Call(this._rt.CharAt);                 // AX = the i-th byte (0 past the end)
      asm.Mov(Reg.BX, (Imm)chByte);
    } else {
      this.EmitExpression(b.Left);
      asm.Push(Reg.AX);
      this.EmitExpression(b.Right);
      asm.Mov(Reg.DX, Reg.AX);
      asm.Pop(Reg.AX);

      if (b.Op is BinaryOp.Add or BinaryOp.Concat) {
        asm.Call(this._rt.StrCat);
        return;
      }

      // O0298: `=` / `<>` only need equality, so under --optimize use the length-guarded compare that
      // skips the byte scan when the lengths differ. It returns 0 (equal) / 1 (unequal), which the same
      // xor bx,bx / je-jne test reads. Ordering forms keep the full three-way StrCmp.
      asm.Call(this.Optimize && b.Op is BinaryOp.Equal or BinaryOp.NotEqual ? this._rt.StrCmpEq : this._rt.StrCmp);
      asm.Xor(Reg.BX, Reg.BX);
    }
    switch (b.Op) {
      case BinaryOp.Equal: this.EmitInt16Compare(b, asm => asm.Je, Condition.Equal); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(b, asm => asm.Jne, Condition.NotEqual); break;
      case BinaryOp.Less: this.EmitInt16Compare(b, asm => asm.Jl, Condition.Less); break;
      case BinaryOp.Greater: this.EmitInt16Compare(b, asm => asm.Jg, Condition.Greater); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(b, asm => asm.Jle, Condition.LessOrEqual); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(b, asm => asm.Jge, Condition.GreaterOrEqual); break;
      default:
        this.Unsupported(b, $"string {b.Op}");
        break;
    }
  }

  /// <summary>
  /// O0297: recognizes <c>MID$(s$, i, 1) = "c"</c> (or with the sides swapped) where the other operand
  /// is a single non-NUL character literal, so the comparison can be a direct byte read against the
  /// literal's byte. <c>ASC(MID$(s$, i, 1)) = ASC("c")</c> is exact for a non-NUL "c": a past-the-end
  /// MID$ is "" whose byte reads as 0, which a non-zero literal byte never matches, and an in-range
  /// byte matches iff the characters are equal. A NUL literal is excluded (it would alias that 0).
  /// </summary>
  private bool TryCharCompareOperands(BinaryExpr b, out Expression strExpr, out Expression? idxExpr, out int literalByte) {
    strExpr = null!; idxExpr = null; literalByte = 0;
    var matched = false;
    int? litByte = null;
    if (this.SingleCharSource(b.Left, out var ls, out var li) && ByteOf(b.Right) is { } rb) { matched = true; strExpr = ls; idxExpr = li; litByte = rb; }
    else if (this.SingleCharSource(b.Right, out var rs, out var ri) && ByteOf(b.Left) is { } lb) { matched = true; strExpr = rs; idxExpr = ri; litByte = lb; }
    if (!matched || litByte is not { } theByte)
      return false;
    literalByte = theByte;
    return true;                                    // SingleCharSource already checked the string type

    // the constant byte of a one-character comparand: a single-char string literal, or CHR$(const).
    // A zero byte is excluded either way - it would alias the 0 a past-the-end MID$ reads as.
    int? ByteOf(Expression e) {
      if (e is StringLiteralExpr { Value: { Length: 1 } text } && text[0] is not '\0' and <= (char)255)
        return (byte)text[0];
      if (e is CallOrIndexExpr chr && model.IntrinsicBindings.TryGetValue(chr, out var chrInfo)
          && chrInfo.Name.Equals("CHR$", StringComparison.OrdinalIgnoreCase) && chr.Arguments.Count == 1
          && this.OptFolder.TryFold(chr.Arguments[0]) is { Integer: { } n } && (n & 0xFF) != 0)
        return (int)(n & 0xFF);
      return null;
    }
  }

  /// <summary>left AX, right BX -> result AX.</summary>
  /// <summary>A zero divisor raises error 11 like genuine PBC (the bare IDIV would CPU-fault instead).</summary>
  private void EmitInt16DivideGuard() {
    var asm = this._asm;
    var ok = asm.DefineLabel();
    asm.Test(Reg.BX, Reg.BX);
    asm.Jnz(ok);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, 11);
    asm.Call(this._rt.Raise);
    asm.Pop(Reg.AX);
    asm.MarkLabel(ok);
  }

  /// <summary>
  /// The word memory cell for an operand that would emit as a plain `mov ax,[cell]` read of a
  /// same-width (2-byte) integer variable - so an ALU op can take it as a memory operand
  /// (ADD AX,[cell]) instead of staging it through BX. Returns null for anything that is not such
  /// a direct read: a register-resident variable, an IPCP/SCCP-folded constant, a CSE-cached node,
  /// a captured local (env-pointer load), a BYREF parameter (pointer load), or a wider/narrower type.
  /// </summary>
  /// <summary>
  /// pb36 O8: emits the address of a static array element into BX and hands back the
  /// <c>[BX+disp]</c> operand, so an ALU op can read the element directly. Evaluating the LEFT
  /// operand afterwards is unobservable - it is a constant or a plain variable read, so it cannot
  /// trap and nothing in an address computation writes it - and any bounds check the element needs
  /// is emitted here, before either. Restricted to a plain static array of 2-byte elements: that
  /// is the shape whose place is a near <c>[BX+disp]</c> by construction.
  /// </summary>
  private Mem? FuseArrayElementOperand(Expression right, Expression left, PbType opType) {
    if (!this.Optimize || KindOf(opType) != ValueKind.Int16)
      return null;
    if (right is not CallOrIndexExpr || this._cseMarks?.ContainsKey(right) == true)
      return null;
    if (!model.VariableBindings.TryGetValue(right, out var symbol) || symbol.Type is not ArrayType array)
      return null;
    // O6b: this loop already walks the array with a pointer in BX - the element is simply [BX],
    // with no address code at all (the gate that parked the pointer verified this exact shape)
    if (this._residentElementPtr is { } walk
        && ReferenceEquals(walk.Array, symbol)
        && right is CallOrIndexExpr { Arguments: [NameExpr walkIdx] }
        && model.VariableBindings.TryGetValue(walkIdx, out var walkSym)
        && ReferenceEquals(walkSym, walk.Counter))
      return Mem.Word(Reg.BX);
    if (array.IsDynamic || symbol.ArrayClass != ArrayClass.Default || symbol.IsShared && symbol.Storage == VariableStorage.Captured)
      return null;
    if (array.Element is not ScalarType { IsFloat: false, ByteSize: 2 }
        || model.TypeOf(right) is not ScalarType { IsFloat: false, ByteSize: 2 })
      return null;
    if (!this.IsReloadableAfterAddressCode(left))
      return null;
    return this.EmitPlace(right) is { Far: false } place ? place.Cell.WithSize(OperandSize.Word) : null;
  }

  /// <summary>
  /// True when an operand can be evaluated AFTER an address computation without changing what the
  /// program does: a compile-time constant, or a scalar variable read (a register or a memory
  /// cell - neither of which an address computation writes, and neither of which can trap).
  /// </summary>
  private bool IsReloadableAfterAddressCode(Expression e) =>
    this.OptFolder.TryFold(e) is { Integer: not null }
    || (e is NameExpr && model.VariableBindings.TryGetValue(e, out var symbol)
        && symbol.Type is ScalarType && !symbol.IsArray);

  private Mem? TryInt16MemOperand(Expression e, PbType opType) {
    if (KindOf(opType) != ValueKind.Int16)
      return null;
    if (e is not NameExpr n || this._cseMarks?.ContainsKey(n) == true)
      return null;
    if (this._provenReads?.ContainsKey(n) == true)
      return null;                                  // SCCP-proven constant: the cell may be a dead store - read it as the immediate, not from memory
    if (model.TypeOf(n) is not ScalarType { IsFloat: false, ByteSize: 2 })
      return null;                                  // same width as the op, so no coercion is needed
    if (!model.VariableBindings.TryGetValue(n, out var sym))
      return null;
    if (this.ResidentRegOf(sym) != null)
      return null;                                  // a resident variable lives in a register, not memory
    if (this._ipcp?.ContainsKey(sym) == true || sym.Storage == VariableStorage.Captured)
      return null;
    if (this._inlineParamSlots is null
        && this._copyReads is { } cr && cr.TryGetValue(n, out var src) && this.TryDirectCell(src) is { } srcCell)
      return srcCell.WithSize(OperandSize.Word);
    return this.InlineSlotCellOf(sym) is { } cell ? cell.WithSize(OperandSize.Word) : null;
  }

  /// <summary>
  /// The direct memory cell of a SINGLE/DOUBLE scalar usable as an x87
  /// arithmetic memory operand (<c>FADD/FSUB/FMUL/FDIV m32|m64</c>), so a float
  /// binary op reads its right operand straight from memory instead of FLD-ing
  /// it onto the stack and popping. The x87 always computes in 80-bit, so a
  /// narrower cell (SINGLE under a DOUBLE op) converts on load exactly as the
  /// FLD path would. EXTENDED (10-byte) is excluded - FADD has no tword form -
  /// as are register-resident, captured/BYREF and IPCP-substituted operands.
  /// </summary>
  private Mem? TryFloatMemOperand(Expression e) {
    if (e is not NameExpr n || this._cseMarks?.ContainsKey(n) == true)
      return null;
    if (this._provenReads?.ContainsKey(n) == true)
      return null;                                  // SCCP-proven constant: the cell may be a dead store - read it as the immediate, not from memory
    if (model.TypeOf(n) is not ScalarType { IsFloat: true, ByteSize: var bytes } || bytes is not (4 or 8))
      return null;
    if (!model.VariableBindings.TryGetValue(n, out var sym))
      return null;
    if (this.ResidentRegOf(sym) != null)
      return null;
    if (this._ipcp?.ContainsKey(sym) == true || sym.Storage == VariableStorage.Captured)
      return null;
    var size = bytes == 4 ? OperandSize.Dword : OperandSize.Qword;
    if (this._inlineParamSlots is null
        && this._copyReads is { } cr && cr.TryGetValue(n, out var src) && this.TryDirectCell(src) is { } srcCell)
      return srcCell.WithSize(size);
    return this.InlineSlotCellOf(sym) is { } cell ? cell.WithSize(size) : null;
  }

  /// <summary>
  /// The direct memory cell of a SIGNED INTEGER/LONG scalar usable as an x87
  /// integer arithmetic memory operand (<c>FIADD/FISUB/FIMUL/FIDIV m16|m32</c>),
  /// so a float op against an integer reads it straight from memory instead of
  /// loading it into AX and FILD-ing through a scratch slot. Unsigned WORD/DWORD
  /// is excluded - FILD/Fi* read the cell as signed; so are register-resident,
  /// captured/BYREF and IPCP-substituted operands.
  /// </summary>
  private Mem? TryFloatIntMemOperand(Expression e) {
    if (e is not NameExpr n || this._cseMarks?.ContainsKey(n) == true)
      return null;
    if (this._provenReads?.ContainsKey(n) == true)
      return null;                                  // SCCP-proven constant: the cell may be a dead store - read it as the immediate, not from memory
    if (model.TypeOf(n) is not ScalarType { IsFloat: false, Signed: true, ByteSize: var bytes } || bytes is not (2 or 4))
      return null;
    if (!model.VariableBindings.TryGetValue(n, out var sym))
      return null;
    if (this.ResidentRegOf(sym) != null)
      return null;
    if (this._ipcp?.ContainsKey(sym) == true || sym.Storage == VariableStorage.Captured)
      return null;
    var size = bytes == 4 ? OperandSize.Dword : OperandSize.Word;
    if (this._inlineParamSlots is null
        && this._copyReads is { } cr && cr.TryGetValue(n, out var src) && this.TryDirectCell(src) is { } srcCell)
      return srcCell.WithSize(size);
    return this.InlineSlotCellOf(sym) is { } cell ? cell.WithSize(size) : null;
  }

  /// <summary>
  /// The low-word direct cell of a 4-byte LONG/DWORD scalar whose right-operand
  /// value can be loaded into BX:CX (low [cell], high [cell+2]) without the
  /// push/pop staging of the left operand - the cell has no side effects and is
  /// read after the left exactly as the staged path would, so it is order-safe
  /// for EVERY 32-bit op (compare/bitwise/div/mod). Sign-agnostic (bitwise is
  /// bit-identical, the divide variant carries the sign). Same exclusions as the
  /// other memory-operand helpers (proven-const dead store, register residency,
  /// inline-frame slots, captured/BYREF, IPCP, CSE marks).
  /// </summary>
  private Mem? TryInt32MemOperand(Expression e) {
    if (e is not NameExpr n || this._cseMarks?.ContainsKey(n) == true)
      return null;
    if (this._provenReads?.ContainsKey(n) == true)
      return null;
    if (model.TypeOf(n) is not ScalarType { IsFloat: false, ByteSize: 4 })
      return null;
    if (!model.VariableBindings.TryGetValue(n, out var sym))
      return null;
    if (this.ResidentRegOf(sym) != null)
      return null;
    if (this._ipcp?.ContainsKey(sym) == true || sym.Storage == VariableStorage.Captured)
      return null;
    if (this._inlineParamSlots is null
        && this._copyReads is { } cr && cr.TryGetValue(n, out var src) && this.TryDirectCell(src) is { } srcCell)
      return srcCell.WithSize(OperandSize.Word);
    return this.InlineSlotCellOf(sym) is { } cell ? cell.WithSize(OperandSize.Word) : null;
  }

  /// <summary>
  /// The data-segment QWORD constant cell of a float literal operand, usable as an
  /// x87 memory operand. Mirrors the FLD-const path (EmitExpression's FloatLiteral /
  /// float-typed integer-literal cases) exactly - same FloatConstOf value and SINGLE
  /// quantization - so only the FLD+pop is saved. An integer literal coerced into a
  /// float op (its own type still integral) stays on the FILD path (returns null).
  /// </summary>
  private Mem? TryFloatConstMemOperand(Expression e) => e switch {
    FloatLiteralExpr f => Mem.Qword(this.FloatConstOf(model.TypeOf(f) is ScalarType { Kind: ScalarKind.Single } ? (float)f.Value : f.Value)),
    IntegerLiteralExpr i when model.TypeOf(i) is ScalarType { IsFloat: true } => Mem.Qword(this.FloatConstOf(i.Value)),
    _ => null,
  };

  private void EmitInt16Op(BinaryExpr b, bool unsignedCompare = false) {
    var asm = this._asm;
    if (unsignedCompare) {
      switch (b.Op) {
        case BinaryOp.Equal: this.EmitInt16Compare(b, asm => asm.Je, Condition.Equal); return;
        case BinaryOp.NotEqual: this.EmitInt16Compare(b, asm => asm.Jne, Condition.NotEqual); return;
        case BinaryOp.Less: this.EmitInt16Compare(b, asm => asm.Jb, Condition.Below); return;
        case BinaryOp.Greater: this.EmitInt16Compare(b, asm => asm.Ja, Condition.Above); return;
        case BinaryOp.LessEqual: this.EmitInt16Compare(b, asm => asm.Jbe, Condition.BelowOrEqual); return;
        case BinaryOp.GreaterEqual: this.EmitInt16Compare(b, asm => asm.Jae, Condition.AboveOrEqual); return;
      }
    }
    switch (b.Op) {
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        // pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no overflow
        if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Multiply:
        asm.Imul(Reg.BX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.IntegerDivide:
        // pb36 O16: drop the divide-by-zero guard when the divisor range excludes zero
        if (!this.DivisorNonZero(b))
          this.EmitInt16DivideGuard();
        asm.Cwd();
        asm.Idiv(Reg.BX);
        break;
      case BinaryOp.Modulo:
        if (!this.DivisorNonZero(b))
          this.EmitInt16DivideGuard();
        asm.Cwd();
        asm.Idiv(Reg.BX);
        // O0079 reversed: this IDIV produced a quotient a later q = n \ d wants, and the next
        // instruction is about to overwrite AX with the remainder
        if (this._stashQuotientSlot is { } quotientSlot)
          asm.Mov(this.CseSlot(quotientSlot), Reg.AX);
        asm.Mov(Reg.AX, Reg.DX);
        break;
      case BinaryOp.And: asm.And(Reg.AX, Reg.BX); break;
      case BinaryOp.Or: asm.Or(Reg.AX, Reg.BX); break;
      case BinaryOp.Xor: asm.Xor(Reg.AX, Reg.BX); break;
      case BinaryOp.Eqv:
        asm.Xor(Reg.AX, Reg.BX);
        asm.Not(Reg.AX);
        break;
      case BinaryOp.Imp:
        asm.Not(Reg.AX);
        asm.Or(Reg.AX, Reg.BX);
        break;
      // pb36 shift/rotate: AX = left value, BL = count (8086 shifts/rotates by CL)
      case BinaryOp.ShiftLeft: asm.Mov(Reg.CL, Reg.BL); asm.Shl(Reg.AX, Reg.CL); break;
      case BinaryOp.ShiftRightArith: asm.Mov(Reg.CL, Reg.BL); asm.Sar(Reg.AX, Reg.CL); break;
      case BinaryOp.ShiftRightLogical: asm.Mov(Reg.CL, Reg.BL); asm.Shr(Reg.AX, Reg.CL); break;
      case BinaryOp.RotateLeft: asm.Mov(Reg.CL, Reg.BL); asm.Rol(Reg.AX, Reg.CL); break;
      case BinaryOp.RotateRight: asm.Mov(Reg.CL, Reg.BL); asm.Ror(Reg.AX, Reg.CL); break;
      case BinaryOp.Equal: this.EmitInt16Compare(b, asm => asm.Je, Condition.Equal); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(b, asm => asm.Jne, Condition.NotEqual); break;
      case BinaryOp.Less: this.EmitInt16Compare(b, asm => asm.Jl, Condition.Less); break;
      case BinaryOp.Greater: this.EmitInt16Compare(b, asm => asm.Jg, Condition.Greater); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(b, asm => asm.Jle, Condition.LessOrEqual); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(b, asm => asm.Jge, Condition.GreaterOrEqual); break;
      default:
        this.Unsupported(b, $"int16 {b.Op}");
        break;
    }
  }

  /// <summary>
  /// pb36 O4: <c>(x MOD 2^j) = 0</c> / <c>&lt;&gt; 0</c> becomes <c>(x AND (2^j-1)) = 0</c>. Sound for
  /// every sign - the modulo's sign fixup changes the result's value but not whether it is zero,
  /// which is exactly the low j bits either way. int16 only (the common even/odd test); the
  /// masked AND sets ZF just like a CMP against zero, so the ordinary compare-result / branch-fusion
  /// path drives the outcome. Off under checked arithmetic, whose divide-by-zero-less MOD by a
  /// constant still cannot trap here but where the plain path already carries the sign fixup.
  /// </summary>
  private bool TryEmitModuloZeroTest(BinaryExpr b) {
    if (!this.Optimize || b.Op is not (BinaryOp.Equal or BinaryOp.NotEqual))
      return false;
    // one side is a modulo by a power of two, the other folds to zero
    var (modExpr, other) = b.Left is BinaryExpr { Op: BinaryOp.Modulo } ? (b.Left, b.Right)
      : b.Right is BinaryExpr { Op: BinaryOp.Modulo } ? (b.Right, b.Left)
      : (null, null);
    if (modExpr is not BinaryExpr { Op: BinaryOp.Modulo, Left: { } dividend, Right: { } divisor })
      return false;
    if (this.OptFolder.TryFold(other) is not { Integer: 0 })
      return false;
    if (this.OptFolder.TryFold(divisor) is not { Integer: { } m } || m <= 0 || (m & (m - 1)) != 0)
      return false;
    if (KindOf(model.TypeOf(modExpr)) != ValueKind.Int16 || KindOf(model.TypeOf(dividend)) != ValueKind.Int16)
      return false;

    this.EmitExpression(dividend);
    this.Coerce(model.TypeOf(dividend), PbType.Integer, dividend);
    this._asm.And(Reg.AX, (Imm)(int)(m - 1));   // sets ZF from the low j bits, exactly like CMP AX,0
    var (jump, condition) = b.Op == BinaryOp.Equal
      ? ((Func<Assembler, Action<Label>>)(a => a.Je), Condition.Equal)
      : (a => a.Jne, Condition.NotEqual);
    if (!this.TryEmitCompareAsBranch(b, condition))
      this.EmitInt16CompareResult(jump, condition);
    return true;
  }

  private void EmitInt16Compare(BinaryExpr b, Func<Assembler, Action<Label>> jump, Condition condition) {
    this._asm.Cmp(Reg.AX, Reg.BX);
    if (!this.TryEmitCompareAsBranch(b, condition))
      this.EmitInt16CompareResult(jump, condition);
  }

  /// <summary>
  /// pb36 O8: the flags of a 16-bit CMP drive a branch directly when the comparison IS the whole
  /// condition of an IF/WHILE/UNTIL. Both the -1/0 truth value the expression path materializes
  /// (<c>MOV AX,-1 / Jcc / MOV AX,0</c>) and the <c>TEST AX,AX</c> that immediately consumes it
  /// are then dead - five instructions per conditional, in the shape almost every conditional has.
  ///
  /// Matched by node identity, so a comparison nested inside a larger expression (or emitted from
  /// an inlined callee body) can never be mistaken for the condition itself; the arming site falls
  /// back to the value path whenever this did not fire.
  /// </summary>
  private bool TryEmitCompareAsBranch(BinaryExpr b, Condition condition) {
    if (this._compareBranch is not { } branch || !ReferenceEquals(branch.Node, b))
      return false;
    this._compareBranch = null;
    this._compareBranchTaken = true;
    // the x86 condition encoding pairs each condition with its negation in the low bit
    this._asm.J(branch.WhenFalse ? (Condition)((byte)condition ^ 1) : condition, branch.Target);
    return true;
  }

  /// <summary>Turns the flags of a preceding 16-bit CMP into AX = -1/0 (the PB truth value).</summary>
  private void EmitInt16CompareResult(Func<Assembler, Action<Label>> jump, Condition condition) {
    var asm = this._asm;
    // pb36 C1 ($CPU 80386): branchless SETcc - AL = 0/1, widen, negate to 0/-1
    if (this.Optimize && this.Cpu386) {
      asm.Setcc(condition, Reg.AL);
      asm.Mov(Reg.AH, (Imm)0);  // MOV leaves the SETcc result intact
      asm.Neg(Reg.AX);
      return;
    }
    // O0088 8086 branchless truth for the carry-only conditions: SBB AX,AX = -CF, so an unsigned
    // < materializes as two bytes (SBB) instead of eight (MOV -1 / Jcc / MOV 0); the >= complement
    // negates it. SBB reads AX but AX-AX cancels, leaving just -CF, so the prior value is irrelevant.
    // Only unsigned </>= are pure-carry; the signed and ZF-dependent conditions keep the branch form.
    if (this.Optimize && condition is Condition.Below or Condition.AboveOrEqual) {
      asm.Sbb(Reg.AX, Reg.AX);                 // AX = -1 when CF (below), 0 when above-or-equal
      if (condition == Condition.AboveOrEqual)
        asm.Not(Reg.AX);                       // invert: -1 iff >=
      return;
    }
    var done = asm.DefineLabel();
    asm.Mov(Reg.AX, -1);    // MOV leaves flags intact
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>
  /// pb36 O8: a 16-bit binary op with a compile-time constant operand folds the
  /// constant into an immediate ALU instruction (AND/OR/XOR/ADD/SUB AX,imm or
  /// CMP AX,imm) instead of loading it into BX and pushing/popping the other
  /// operand - smaller and faster. Add/Sub keep their JNO overflow trap under
  /// <c>$ERROR OVERFLOW</c> (an immediate ALU sets OF exactly like the register
  /// form). Bitwise/equality constants may sit on either side (commutative);
  /// an ordering compare with the constant on the left mirrors the operator.
  /// The constant is taken modulo 2^16 - the same low word the generic path
  /// would coerce into BX - so behavior is bit-identical.
  /// </summary>
  private bool TryEmitInt16ConstBinary(BinaryExpr b, PbType opType, bool unsignedCompare) {
    if (!this.Optimize)
      return false;
    var asm = this._asm;

    void EmitOperand(Expression e) {
      this.EmitExpression(e);
      this.Coerce(model.TypeOf(e), opType, e);
    }
    Imm Imm16(long c) => (Imm)(int)(short)(c & 0xFFFF);

    switch (b.Op) {
      case BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Add: {
        Expression variable;
        long c;
        if (this.TryModularFoldConst(b.Right, out c))
          variable = b.Left;
        else if (this.TryModularFoldConst(b.Left, out c))
          variable = b.Right;
        else
          return false;
        EmitOperand(variable);
        switch (b.Op) {
          case BinaryOp.And: asm.And(Reg.AX, Imm16(c)); break;
          case BinaryOp.Or: asm.Or(Reg.AX, Imm16(c)); break;
          case BinaryOp.Xor: asm.Xor(Reg.AX, Imm16(c)); break;
          default:
            this.EmitAddImm16((short)(c & 0xFFFF));
            // pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no overflow
            if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
              this.EmitRaiseWhen(asm.Jno, 6);
            break;
        }
        return true;
      }

      case BinaryOp.Subtract: {
        if (!this.TryModularFoldConst(b.Right, out var c)) // c - v is not an immediate form
          return false;
        EmitOperand(b.Left);
        this.EmitSubImm16((short)(c & 0xFFFF));
        if (this.CheckOverflow && !this.ProvablyNoOverflow(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        return true;
      }

      case BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
        or BinaryOp.LessEqual or BinaryOp.GreaterEqual: {
        BinaryOp op;
        Expression variable;
        long c;
        if (this.TryModularFoldConst(b.Right, out c)) {
          variable = b.Left;
          op = b.Op;
        } else if (this.TryModularFoldConst(b.Left, out c)) {
          variable = b.Right;
          op = MirrorCompare(b.Op); // const on the left: v <op'> c
        } else
          return false;
        EmitOperand(variable);
        // pb36 O8: compare against zero is OR AX,AX (2 bytes, same ZF/SF; OF is
        // cleared, which every signed/unsigned condition below tolerates because
        // their OF-dependent forms reduce to SF/CF tests when OF = 0)
        if ((c & 0xFFFF) == 0)
          asm.Or(Reg.AX, Reg.AX);
        else
          asm.Cmp(Reg.AX, Imm16(c));
        var (jump, condition) = Int16CompareSelector(op, unsignedCompare);
        if (!this.TryEmitCompareAsBranch(b, condition))
          this.EmitInt16CompareResult(jump, condition);
        return true;
      }

      default:
        return false; // multiply / divide / modulo / eqv / imp keep the generic path
    }
  }

  /// <summary>
  /// pb36 O8: a 32-bit AND/OR/XOR/ADD/SUB/=/&lt;&gt; with a compile-time constant
  /// operand folds the constant into immediate pair ops (low word into AX, high
  /// word into DX) instead of loading it into CX:BX with the push/pop dance.
  /// ADD/SUB keep their JNO overflow trap under <c>$ERROR OVERFLOW</c> (the carry
  /// chains through ADC/SBB exactly like the register form, and OF after the
  /// high-word op is the 32-bit signed overflow); =/&lt;&gt; subtract the halves
  /// to test for zero (and against 0 skip the subtract entirely - the operand's
  /// own AX|DX already decides). Bitwise/add/equality are commutative; subtract
  /// only folds a right-hand constant. The constant is split into its 16-bit
  /// halves, the same words the register path would load.
  /// </summary>
  private bool TryEmitInt32ConstBinary(BinaryExpr b, PbType opType) {
    if (!this.Optimize)
      return false;
    if (b.Op is not (BinaryOp.And or BinaryOp.Or or BinaryOp.Xor or BinaryOp.Add or BinaryOp.Subtract
        or BinaryOp.Equal or BinaryOp.NotEqual))
      return false;
    var asm = this._asm;

    Expression variable;
    long c;
    if (this.TryModularFoldConst(b.Right, out c))
      variable = b.Left;
    else if (b.Op != BinaryOp.Subtract && this.TryModularFoldConst(b.Left, out c))
      variable = b.Right;
    else
      return false;

    this.EmitExpression(variable);
    this.Coerce(model.TypeOf(variable), opType, variable); // DX:AX = variable
    var lo = (Imm)(int)(short)(c & 0xFFFF);
    var hi = (Imm)(int)(short)((c >> 16) & 0xFFFF);
    switch (b.Op) {
      case BinaryOp.And: asm.And(Reg.AX, lo); asm.And(Reg.DX, hi); break;
      case BinaryOp.Or: asm.Or(Reg.AX, lo); asm.Or(Reg.DX, hi); break;
      case BinaryOp.Xor: asm.Xor(Reg.AX, lo); asm.Xor(Reg.DX, hi); break;
      case BinaryOp.Add:
        asm.Add(Reg.AX, lo);
        asm.Adc(Reg.DX, hi);
        // pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no 32-bit overflow
        if (this.CheckOverflow && !this.ProvablyNoOverflow32(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, lo);
        asm.Sbb(Reg.DX, hi);
        if (this.CheckOverflow && !this.ProvablyNoOverflow32(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      default: { // Equal / NotEqual: difference is zero iff equal, then -1/0
        if ((c & 0xFFFFFFFFL) != 0) { // against 0 the operand's own AX|DX already decides
          asm.Sub(Reg.AX, lo);
          asm.Sbb(Reg.DX, hi);
        }
        asm.Or(Reg.AX, Reg.DX);  // zero iff the operand equalled the constant
        var done = asm.DefineLabel();
        asm.Mov(Reg.DX, Reg.AX);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? -1 : 0);
        asm.Test(Reg.DX, Reg.DX);
        asm.Jz(done);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? 0 : -1);
        asm.MarkLabel(done);
        asm.Cwd();
        break;
      }
    }
    return true;
  }

  /// <summary>The comparison that holds for swapped operands (<c>a &lt; b</c> becomes <c>b &gt; a</c>).</summary>
  private static BinaryOp MirrorCompare(BinaryOp op) => op switch {
    BinaryOp.Less => BinaryOp.Greater,
    BinaryOp.Greater => BinaryOp.Less,
    BinaryOp.LessEqual => BinaryOp.GreaterEqual,
    BinaryOp.GreaterEqual => BinaryOp.LessEqual,
    _ => op, // Equal / NotEqual are symmetric
  };

  /// <summary>The (jump, condition) pair realizing a 16-bit signed/unsigned compare, matching <see cref="EmitInt16Op"/>.</summary>
  private static (Func<Assembler, Action<Label>>, Condition) Int16CompareSelector(BinaryOp op, bool unsignedCompare) => op switch {
    BinaryOp.Equal => (asm => asm.Je, Condition.Equal),
    BinaryOp.NotEqual => (asm => asm.Jne, Condition.NotEqual),
    BinaryOp.Less when unsignedCompare => (asm => asm.Jb, Condition.Below),
    BinaryOp.Greater when unsignedCompare => (asm => asm.Ja, Condition.Above),
    BinaryOp.LessEqual when unsignedCompare => (asm => asm.Jbe, Condition.BelowOrEqual),
    BinaryOp.GreaterEqual when unsignedCompare => (asm => asm.Jae, Condition.AboveOrEqual),
    BinaryOp.Less => (asm => asm.Jl, Condition.Less),
    BinaryOp.Greater => (asm => asm.Jg, Condition.Greater),
    BinaryOp.LessEqual => (asm => asm.Jle, Condition.LessOrEqual),
    _ => (asm => asm.Jge, Condition.GreaterOrEqual),
  };

  /// <summary>left DX:AX, right CX:BX -> result DX:AX.</summary>
  /// <summary>
  /// pb36 32-bit shift/rotate of DX:AX by the count in BL (no single 8086
  /// instruction does a 32-bit shift, so it runs a per-bit loop).
  /// </summary>
  private void EmitInt32ShiftRotate(BinaryOp op) {
    var asm = this._asm;
    asm.Mov(Reg.CL, Reg.BL);
    var done = asm.DefineLabel();
    var loop = asm.DefineLabel();
    asm.Test(Reg.CL, Reg.CL);
    asm.Jz(done);
    asm.MarkLabel(loop);
    switch (op) {
      case BinaryOp.ShiftLeft:
        asm.Shl(Reg.AX, 1); asm.Rcl(Reg.DX, 1); break;
      case BinaryOp.ShiftRightArith:
        asm.Sar(Reg.DX, 1); asm.Rcr(Reg.AX, 1); break;
      case BinaryOp.ShiftRightLogical:
        asm.Shr(Reg.DX, 1); asm.Rcr(Reg.AX, 1); break;
      case BinaryOp.RotateLeft:
        asm.Shl(Reg.AX, 1); asm.Rcl(Reg.DX, 1); asm.Adc(Reg.AX, (Imm)0); break;
      case BinaryOp.RotateRight:
        var skip = asm.DefineLabel();
        asm.Shr(Reg.DX, 1); asm.Rcr(Reg.AX, 1); asm.Jnc(skip); asm.Or(Reg.DX, (Imm)0x8000); asm.MarkLabel(skip); break;
    }
    asm.Dec(Reg.CL);
    asm.Jnz(loop);
    asm.MarkLabel(done);
  }

  /// <summary>
  /// left DX:AX, right CX:BX -> result DX:AX. <paramref name="unsignedType"/> is the operation
  /// type's unsignedness (DWORD): it selects the unsigned divide helpers and the unsigned 16-bit
  /// narrowing forms.
  /// </summary>
  private void EmitInt32Op(BinaryExpr b, bool unsignedCompare = false, bool unsignedType = false) {
    var asm = this._asm;

    // pb36 O16 type narrowing: a comparison whose operands the interval lattice proves both fit
    // one 16-bit word is decided entirely by the low words - the high halves are only their sign
    // (or zero) extension, so they compare equal and cannot change the ordering. One CMP AX,BX
    // plus the ordinary -1/0 materialization replaces the nine-instruction 32-bit sequence; CWD
    // re-widens the result to DX:AX exactly as the wide paths do.
    if (b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater
            or BinaryOp.LessEqual or BinaryOp.GreaterEqual
        && this.BothOperandsNarrow16(b, unsignedCompare)) {
      var (narrowJump, narrowCondition) = Int16CompareSelector(b.Op, unsignedCompare);
      var pending = this._compareBranch;
      this._compareBranch = null;   // this path still owes the caller a CWD-widened value
      this.EmitInt16Compare(b, narrowJump, narrowCondition);
      this._compareBranch = pending;
      asm.Cwd();
      return;
    }

    if (unsignedCompare && b.Op is BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual) {
      // borrow of (left - right) decides; zero via OR of the difference
      var done = asm.DefineLabel();
      asm.Sub(Reg.AX, Reg.BX);
      asm.Sbb(Reg.DX, Reg.CX);            // CF = left < right
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AX, -1);
      switch (b.Op) {
        case BinaryOp.Less:
          asm.Jc(done);
          break;
        case BinaryOp.GreaterEqual:
          asm.Jnc(done);
          break;
        case BinaryOp.Greater: {
          var no = asm.DefineLabel();
          asm.Jc(no);
          asm.Or(Reg.BX, Reg.DX);
          asm.Jnz(done);
          asm.MarkLabel(no);
          break;
        }
        case BinaryOp.LessEqual:
          asm.Jc(done);
          asm.Or(Reg.BX, Reg.DX);
          asm.Jz(done);
          break;
      }
      asm.Mov(Reg.AX, (Imm)0);
      asm.MarkLabel(done);
      asm.Cwd();
      return;
    }
    switch (b.Op) {
      case BinaryOp.ShiftLeft or BinaryOp.ShiftRightArith or BinaryOp.ShiftRightLogical or BinaryOp.RotateLeft or BinaryOp.RotateRight:
        this.EmitInt32ShiftRotate(b.Op);
        break;
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        asm.Adc(Reg.DX, Reg.CX);
        // pb36 O16: drop the Error-6 check when the affine FOR-counter range proves no 32-bit overflow
        if (this.CheckOverflow && !this.ProvablyNoOverflow32(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        if (this.CheckOverflow && !this.ProvablyNoOverflow32(b))
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Multiply:
        // pb36 O16 type narrowing: when both operands provably fit one 16-bit word, the 8086's
        // own 16x16->32 multiply already produces the whole product in DX:AX - one IMUL/MUL BX
        // instead of the three-MUL rt_lmul call (or the 386 register shuffle below). It can
        // never overflow the result type: |int16 * int16| <= 2^30 and uint16 * uint16 < 2^32,
        // so the narrowed product is bit-identical to the wide one.
        if (this.BothOperandsNarrow16(b, unsignedType)) {
          if (unsignedType)
            asm.Mul(Reg.BX);
          else
            asm.Imul(Reg.BX);
          break;
        }
        // pb36 C1 ($CPU 80386): low-32-bit product via one IMUL EAX, EBX -
        // identical to rt_lmul's result, dropping the runtime helper
        if (this.Optimize && this.Cpu386) {
          var sc = this._scratch;
          asm.Mov(Mem.Word(sc), Reg.AX);
          asm.Mov(Mem.Word(sc, 2), Reg.DX);
          asm.Mov(Reg.EAX, Mem.Dword(sc));
          asm.Mov(Mem.Word(sc), Reg.BX);
          asm.Mov(Mem.Word(sc, 2), Reg.CX);
          asm.Mov(Reg.EBX, Mem.Dword(sc));
          asm.Imul(Reg.EAX, Reg.EBX);
          asm.Mov(Mem.Dword(sc), Reg.EAX);
          asm.Mov(Reg.AX, Mem.Word(sc));
          asm.Mov(Reg.DX, Mem.Word(sc, 2));
        } else
          asm.Call(this._rt.LongMul);
        break;
      case BinaryOp.IntegerDivide or BinaryOp.Modulo:
        // pb36 O16: a signed LONG \ / MOD by a compile-time-constant divisor of
        // magnitude 2..32767 whose dividend the interval lattice proves fits int16
        // runs as ONE 16-bit IDIV (DX:AX / BX -> AX quotient, DX remainder) instead
        // of the 32-bit LongDiv/LongMod runtime call - no $CPU needed (8086 IDIV).
        // The dividend is already sign-extended into DX:AX (its value fits int16) and
        // |quotient| < |dividend| <= 32767 fits int16, so the divide never overflows
        // (#DE); |divisor| >= 2 also rules out divide-by-zero (error 11) and the
        // MININT \ -1 trap. x86 truncates toward zero and the remainder takes the
        // dividend's sign - exactly PB's \ and MOD. CWD re-widens the result to LONG.
        // The dividend's range is taken from NarrowRangeOf, not IndexRangeOf: replacing a
        // 32-bit operation needs every intermediate to have stayed inside a word, otherwise
        // a subexpression that wrapped at 32 bits would make the range a fiction and the
        // 16-bit quotient wrong (bounding an index only needs the composed range).
        if (this.Optimize && !unsignedType
            && this.OptFolder.TryFold(b.Right) is { Integer: { } d16 }
            && d16 is >= -32768 and <= 32767 && Math.Abs(d16) >= 2
            && this.NarrowRangeOf(b.Left, unsigned: false) != null) {
          asm.Idiv(Reg.BX);                    // DX:AX / BX -> AX = quotient, DX = remainder
          if (b.Op == BinaryOp.Modulo)
            asm.Mov(Reg.AX, Reg.DX);
          asm.Cwd();                           // re-widen the 16-bit result to LONG DX:AX
          break;
        }
        // pb36 C1 ($CPU 80386): divide by a compile-time-constant divisor of
        // magnitude >= 2 with the exact hardware IDIV/DIV (EAX=quotient, EDX=
        // remainder), dropping the LongDiv/LongMod runtime call. x86 truncates
        // toward zero and the remainder takes the dividend's sign - exactly PB's
        // \ and MOD. The constant >= 2 gate rules out divide-by-zero (error 11)
        // and the MININT \ -1 overflow, so no trap path is lost.
        if (this.Optimize && this.Cpu386
            && this.OptFolder.TryFold(b.Right) is { Integer: { } divisor } && Math.Abs(divisor) >= 2) {
          var sc = this._scratch;
          asm.Mov(Mem.Word(sc), Reg.AX);
          asm.Mov(Mem.Word(sc, 2), Reg.DX);
          asm.Mov(Reg.EAX, Mem.Dword(sc));      // EAX = dividend
          asm.Mov(Mem.Word(sc), Reg.BX);
          asm.Mov(Mem.Word(sc, 2), Reg.CX);
          asm.Mov(Reg.EBX, Mem.Dword(sc));      // EBX = divisor
          if (unsignedType) {
            asm.Xor(Reg.EDX, Reg.EDX);
            asm.Div(Reg.EBX);
          } else {
            asm.Cdq();
            asm.Idiv(Reg.EBX);
          }
          asm.Mov(Mem.Dword(sc), b.Op == BinaryOp.Modulo ? Reg.EDX : Reg.EAX);
          asm.Mov(Reg.AX, Mem.Word(sc));
          asm.Mov(Reg.DX, Mem.Word(sc, 2));
        } else if (b.Op == BinaryOp.IntegerDivide)
          asm.Call(unsignedType ? this._rt.LongDivU : this._rt.LongDiv);
        else
          asm.Call(unsignedType ? this._rt.LongModU : this._rt.LongMod);
        break;
      case BinaryOp.And:
        asm.And(Reg.AX, Reg.BX);
        asm.And(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Or:
        asm.Or(Reg.AX, Reg.BX);
        asm.Or(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Xor:
        asm.Xor(Reg.AX, Reg.BX);
        asm.Xor(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Equal or BinaryOp.NotEqual: {
        var done = asm.DefineLabel();
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        asm.Or(Reg.AX, Reg.DX);    // zero iff equal
        asm.Mov(Reg.DX, Reg.AX);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? -1 : 0);
        asm.Test(Reg.DX, Reg.DX);
        asm.Jz(done);
        asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? 0 : -1);
        asm.MarkLabel(done);
        asm.Cwd();
        break;
      }
      case BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual: {
        // sign of (left - right); fine for in-range operands (full backend adds overflow-safe compare)
        var jump = b.Op;
        var done = asm.DefineLabel();
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        asm.Or(Reg.AX, Reg.DX);    // combine for zero detection
        asm.Mov(Reg.BX, Reg.AX);
        asm.Mov(Reg.AX, -1);
        switch (jump) {
          case BinaryOp.Less:
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(done);
            break;
          case BinaryOp.GreaterEqual:
            asm.Test(Reg.DX, Reg.DX);
            asm.Jns(done);
            break;
          case BinaryOp.Greater: {
            var no = asm.DefineLabel();
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(no);
            asm.Test(Reg.BX, Reg.BX);
            asm.Jnz(done);
            asm.MarkLabel(no);
            break;
          }
          case BinaryOp.LessEqual: {
            asm.Test(Reg.DX, Reg.DX);
            asm.Js(done);
            asm.Test(Reg.BX, Reg.BX);
            asm.Jz(done);
            break;
          }
        }
        asm.Mov(Reg.AX, (Imm)0);
        asm.MarkLabel(done);
        asm.Cwd();
        break;
      }
      default:
        this.Unsupported(b, $"int32 {b.Op}");
        break;
    }
  }

  /// <summary>left ST(1), right ST(0) -> result ST(0).</summary>
  private void EmitFloatOp(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add: asm.Faddp(); break;
      case BinaryOp.Subtract: asm.Fsubp(); break;
      case BinaryOp.Multiply: asm.Fmulp(); break;
      case BinaryOp.Divide: asm.Fdivp(); break;
      case BinaryOp.Power: asm.Call(this._rt.Pow); break;
      case BinaryOp.Equal: this.EmitFloatCompare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitFloatCompare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitFloatCompare(asm => asm.Jb); break;
      case BinaryOp.Greater: this.EmitFloatCompare(asm => asm.Ja); break;
      case BinaryOp.LessEqual: this.EmitFloatCompare(asm => asm.Jbe); break;
      case BinaryOp.GreaterEqual: this.EmitFloatCompare(asm => asm.Jae); break;
      default:
        this.Unsupported(b, $"float {b.Op}");
        break;
    }
  }

  private void EmitFloatCompare(Func<Assembler, Action<Label>> jump) {
    var asm = this._asm;
    var done = asm.DefineLabel();
    asm.Fxch();              // FCOMPP compares ST0 with ST1: want left in ST0
    asm.Fcompp();
    asm.FstswAx();
    asm.Sahf();              // CF/ZF now mirror the (unsigned-style) FPU compare
    asm.Mov(Reg.AX, -1);
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>
  /// pb36 float memory-operand emit: left is already in ST0, so the op reads its
  /// right operand straight from memory. Returns false for Power (^), which keeps
  /// its runtime call (both operands on the stack).
  /// </summary>
  private bool TryEmitFloatMemOp(BinaryOp op, Mem right) {
    var asm = this._asm;
    switch (op) {
      case BinaryOp.Add: asm.Fadd(right); return true;
      case BinaryOp.Subtract: asm.Fsub(right); return true;
      case BinaryOp.Multiply: asm.Fmul(right); return true;
      case BinaryOp.Divide: asm.Fdiv(right); return true;
      case BinaryOp.Equal: this.EmitFloatCompareMem(right, a => a.Je); return true;
      case BinaryOp.NotEqual: this.EmitFloatCompareMem(right, a => a.Jne); return true;
      case BinaryOp.Less: this.EmitFloatCompareMem(right, a => a.Jb); return true;
      case BinaryOp.Greater: this.EmitFloatCompareMem(right, a => a.Ja); return true;
      case BinaryOp.LessEqual: this.EmitFloatCompareMem(right, a => a.Jbe); return true;
      case BinaryOp.GreaterEqual: this.EmitFloatCompareMem(right, a => a.Jae); return true;
      default: return false;
    }
  }

  /// <summary>
  /// pb36 float op against an integer memory operand: left is already in ST0, so
  /// the op reads its signed-integer right operand from memory with FIADD/FISUB/
  /// FIMUL/FIDIV. Compares and Power fall back to the staged path (false) - the
  /// x87 has no popping integer compare (FICOM keeps both) and Power is a call.
  /// </summary>
  private bool TryEmitFloatIntMemOp(BinaryOp op, Mem right) {
    var asm = this._asm;
    switch (op) {
      case BinaryOp.Add: asm.Fiadd(right); return true;
      case BinaryOp.Subtract: asm.Fisub(right); return true;
      case BinaryOp.Multiply: asm.Fimul(right); return true;
      case BinaryOp.Divide: asm.Fidiv(right); return true;
      default: return false;
    }
  }

  /// <summary>
  /// Float compare against a memory operand: FCOMP [right] compares ST0 (left)
  /// with the cell and pops, so no second FLD and no FXCH - the C0/C3 flags after
  /// FSTSW/SAHF mirror left-vs-right exactly as the FXCH;FCOMPP path produces.
  /// </summary>
  private void EmitFloatCompareMem(Mem right, Func<Assembler, Action<Label>> jump) {
    var asm = this._asm;
    var done = asm.DefineLabel();
    asm.Fcomp(right);
    asm.FstswAx();
    asm.Sahf();
    asm.Mov(Reg.AX, -1);
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>Converts the current value (registers/FPU per <paramref name="from"/>) into <paramref name="to"/>'s category.</summary>
  /// <summary>
  /// The BASCOM lineage (QB 1.0-3.0) rounds float-to-integer half AWAY from zero
  /// (CINT(2.5) = 3, CINT(-2.5) = -3, oracle-verified); QB 4.x and PB use the
  /// FPU's round-to-nearest-even. Biases ST(0) by +-0.5 and truncates so the
  /// following FISTP (nearest-even of an integral value) is exact.
  /// </summary>
  private void EmitDialectRounding() {
    // EffectiveDialect honours a $COMPAT override, so a transpiled-to-pb35 program rounds
    // float-to-integer the way its source dialect did (BASCOM rounds half away from zero).
    if (!model.EffectiveDialect.IsBascomRuntime())
      return;
    var asm = this._asm;
    var negative = asm.DefineLabel();
    var biased = asm.DefineLabel();
    asm.Ftst();
    asm.FstswAx();
    asm.Sahf();
    asm.Jc(negative);
    asm.Fadd(Mem.Qword(asm.Lbl("rt_const_half_m64")));
    asm.Jmp(biased);
    asm.MarkLabel(negative);
    asm.Fsub(Mem.Qword(asm.Lbl("rt_const_half_m64")));
    asm.MarkLabel(biased);
    asm.Call(asm.Lbl("rt_trunc"));
  }

  private void Coerce(PbType from, PbType to, Expression at) {
    var asm = this._asm;
    var src = KindOf(from);
    var dst = KindOf(to);
    if (src == dst)
      return;

    var unsignedSource = from is ScalarType { IsFloat: false, Signed: false, ByteSize: 2 };
    switch (src, dst) {
      case (ValueKind.Int16, ValueKind.Int32) when unsignedSource:
        asm.Xor(Reg.DX, Reg.DX);   // WORD widens zero-extended
        break;

      case (ValueKind.Int16, ValueKind.Int32):
        asm.Cwd();
        break;

      case (ValueKind.Int32, ValueKind.Int16):
        break; // keep AX (range checking is the full backend's job)

      case (ValueKind.Int16, ValueKind.Float) when unsignedSource:
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), (Imm)0);
        asm.Fild(Mem.Dword(this._scratch));
        break;

      case (ValueKind.Int16, ValueKind.Float):
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Fild(Mem.Word(this._scratch));
        break;

      case (ValueKind.Int32, ValueKind.Float) when from is ScalarType { IsFloat: false, Signed: false } or PointerType:
        // a DWORD is unsigned: zero-extend through 64 bits so FILD reads it as a
        // positive value (4000000000.0, not the signed -294967296.0)
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
        asm.Mov(Mem.Word(this._scratch, 4), (Imm)0);
        asm.Mov(Mem.Word(this._scratch, 6), (Imm)0);
        asm.Fild(Mem.Qword(this._scratch));
        break;

      case (ValueKind.Int32, ValueKind.Float):
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
        asm.Fild(Mem.Dword(this._scratch));
        break;

      case (ValueKind.Float, ValueKind.Int16):
        this.EmitDialectRounding();
        // store through 32 bits so out-of-range values wrap like a genuine
        // 16-bit store (C% = A% + B% = -5536), not FISTP's 8000h indefinite
        asm.Fistp(Mem.Dword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        break;

      case (ValueKind.Float, ValueKind.Int32) when to is ScalarType { IsFloat: false, Signed: false } or PointerType:
        // a DWORD target holds values up to 2^32, beyond signed FISTP's range: store
        // through 64 bits and keep the low dword (4000000005.0 -> 4000000005, not the
        // 80000000h indefinite a signed FISTP DWORD would give)
        this.EmitDialectRounding();
        asm.Fistp(Mem.Qword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
        break;

      case (ValueKind.Float, ValueKind.Int32):
        this.EmitDialectRounding();
        if (this.CheckOverflow) {
          // $ERROR OVERFLOW: narrowing a wide value into a signed LONG traps error 6
          // when it is out of range (e.g. the wide product of a& * b&). FISTP of an
          // out-of-range value stores 8000_0000h and sets the x87 Invalid-Operation
          // flag (IE, status-word bit 0); clear stale flags first, then test it.
          asm.Fnclex();
          asm.Fistp(Mem.Dword(this._scratch));
          asm.FstswAx();
          asm.Test(Reg.AL, (Imm)1);            // IE set => the value did not fit a signed LONG
          this.EmitRaiseWhen(asm.Jz, 6);
        } else
          asm.Fistp(Mem.Dword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
        break;

      // QUAD travels on the x87 stack: int16/int32 enter via FILD, leave via FISTP
      case (ValueKind.Int16, ValueKind.Int64) when unsignedSource:
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), (Imm)0);
        asm.Fild(Mem.Dword(this._scratch));
        break;

      case (ValueKind.Int16, ValueKind.Int64):
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Fild(Mem.Word(this._scratch));
        break;

      case (ValueKind.Int32, ValueKind.Int64) when from is ScalarType { IsFloat: false, Signed: false } or PointerType:
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
        asm.Mov(Mem.Word(this._scratch, 4), (Imm)0);
        asm.Mov(Mem.Word(this._scratch, 6), (Imm)0);
        asm.Fild(Mem.Qword(this._scratch));
        break;

      case (ValueKind.Int32, ValueKind.Int64):
        asm.Mov(Mem.Word(this._scratch), Reg.AX);
        asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
        asm.Fild(Mem.Dword(this._scratch));
        break;

      // narrowing goes through the full 64-bit store and takes the low bits:
      // PB wraps silently (FISTP into a narrower cell would saturate instead)
      case (ValueKind.Int64, ValueKind.Int16):
        asm.Fistp(Mem.Qword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        break;

      case (ValueKind.Int64, ValueKind.Int32):
        asm.Fistp(Mem.Qword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
        break;

      case (ValueKind.Int64, ValueKind.Float):
        break; // same representation (x87 stack)

      case (ValueKind.Float, ValueKind.Int64):
        // round to integer so subsequent integer stores/prints are exact
        asm.Fistp(Mem.Qword(this._scratch));
        asm.Fild(Mem.Qword(this._scratch));
        break;

      default:
        this.Unsupported(at, $"conversion {from} -> {to}");
        break;
    }
  }

  private static PbType WidestOf(PbType a, PbType b) {
    if (a is BcdType)
      a = PbType.Ext;
    if (b is BcdType)
      b = PbType.Ext;
    if (a is ScalarType { IsFloat: true } || b is ScalarType { IsFloat: true })
      return PbType.Double;
    if (a is ScalarType { IsFloat: false, ByteSize: 8 } || b is ScalarType { IsFloat: false, ByteSize: 8 })
      return PbType.Quad;
    if (a is ScalarType { ByteSize: > 2 } || b is ScalarType { ByteSize: > 2 })
      return PbType.Long;
    return PbType.Integer;
  }
}
