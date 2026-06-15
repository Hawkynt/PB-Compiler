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
      => this.Pb36Folder.TryFold(x) is { Integer: { } ix }
        && this.Pb36Folder.TryFold(y) is { Integer: { } iy } && ix == iy;
  }

  private void EmitExpressionCore(Expression expression) {
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
          this.EmitLoadPlace(new(slot.Cell, Far: false), slot.Type, n);
          break;
        }
        // pb36 O5: a variable resident in a register this loop (FOR counter in
        // SI, accumulator in DI) reads straight from the register
        if (this.ResidentRegOf(symbol) is { } residentReg) {
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
        if (model.IntrinsicBindings.TryGetValue(call, out var intrinsic))
          this.EmitIntrinsic(call, call.Arguments, intrinsic);
        else if (model.VariableBindings.TryGetValue(call, out var array)) {
          if (call.Arguments.Count == 0) {
            this.Unsupported(call, "whole-array reference");
            break;
          }
          if (this.EmitPlace(call) is { } place)
            this.EmitLoadPlace(place, ((ArrayType)array.Type).Element, call);
        } else if (model.CallBindings.TryGetValue(call, out var proc))
          this.EmitCall(proc, call.Arguments, wantResult: true, call.Position);
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

      case IfExpr ternary:
        this.EmitTernaryIf(ternary);
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

    this.EmitCondition(t.Condition);
    asm.Jz(elseLabel);
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
    var opType = isComparison ? WidestOf(leftType, rightType) : resultType;
    // WORD/DWORD/BYTE pairs compare unsigned
    var unsignedCompare = isComparison
      && leftType is ScalarType { IsFloat: false, Signed: false }
      && rightType is ScalarType { IsFloat: false, Signed: false };
    if (unsignedCompare)
      opType = leftType.Size > 2 || rightType.Size > 2 ? PbType.Dword : PbType.Word;

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
  /// comparisons are exact within 64-bit range; \, MOD and the bitwise
  /// operators come with a later wave.
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
      case BinaryOp.And: this.EmitQuadMemoryOp(this._rt.QuadAnd); break;
      case BinaryOp.Or: this.EmitQuadMemoryOp(this._rt.QuadOr); break;
      case BinaryOp.Xor: this.EmitQuadMemoryOp(this._rt.QuadXor); break;
      case BinaryOp.Eqv: this.EmitQuadMemoryOp(this._rt.QuadEqv); break;
      case BinaryOp.Imp: this.EmitQuadMemoryOp(this._rt.QuadImp); break;
      default:
        asm.Fstp(St.St0);
        asm.Fstp(St.St0);
        this.Unsupported(b, $"QUAD {b.Op} (comes with a later wave)");
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

  /// <summary>Concatenation and bytewise comparisons over string temporaries (both operands consumed).</summary>
  private void EmitStringBinary(BinaryExpr b) {
    var asm = this._asm;
    if (KindOf(model.TypeOf(b.Left)) != ValueKind.Str || KindOf(model.TypeOf(b.Right)) != ValueKind.Str) {
      this.Unsupported(b, "mixed string/numeric operands");
      return;
    }

    this.EmitExpression(b.Left);
    asm.Push(Reg.AX);
    this.EmitExpression(b.Right);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);

    if (b.Op is BinaryOp.Add or BinaryOp.Concat) {
      asm.Call(this._rt.StrCat);
      return;
    }

    asm.Call(this._rt.StrCmp);
    asm.Xor(Reg.BX, Reg.BX);
    switch (b.Op) {
      case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je, Condition.Equal); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne, Condition.NotEqual); break;
      case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jl, Condition.Less); break;
      case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Jg, Condition.Greater); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jle, Condition.LessOrEqual); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jge, Condition.GreaterOrEqual); break;
      default:
        this.Unsupported(b, $"string {b.Op}");
        break;
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

  private void EmitInt16Op(BinaryExpr b, bool unsignedCompare = false) {
    var asm = this._asm;
    if (unsignedCompare) {
      switch (b.Op) {
        case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je, Condition.Equal); return;
        case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne, Condition.NotEqual); return;
        case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jb, Condition.Below); return;
        case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Ja, Condition.Above); return;
        case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jbe, Condition.BelowOrEqual); return;
        case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jae, Condition.AboveOrEqual); return;
      }
    }
    switch (b.Op) {
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Multiply:
        asm.Imul(Reg.BX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.IntegerDivide:
        this.EmitInt16DivideGuard();
        asm.Cwd();
        asm.Idiv(Reg.BX);
        break;
      case BinaryOp.Modulo:
        this.EmitInt16DivideGuard();
        asm.Cwd();
        asm.Idiv(Reg.BX);
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
      case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je, Condition.Equal); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne, Condition.NotEqual); break;
      case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jl, Condition.Less); break;
      case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Jg, Condition.Greater); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jle, Condition.LessOrEqual); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jge, Condition.GreaterOrEqual); break;
      default:
        this.Unsupported(b, $"int16 {b.Op}");
        break;
    }
  }

  private void EmitInt16Compare(Func<Assembler, Action<Label>> jump, Condition condition) {
    this._asm.Cmp(Reg.AX, Reg.BX);
    this.EmitInt16CompareResult(jump, condition);
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
            if (this.CheckOverflow)
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
        if (this.CheckOverflow)
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
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, lo);
        asm.Sbb(Reg.DX, hi);
        if (this.CheckOverflow)
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
  private void EmitInt32Op(BinaryExpr b, bool unsignedCompare = false, bool unsignedDivide = false) {
    var asm = this._asm;
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
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        asm.Adc(Reg.DX, Reg.CX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        if (this.CheckOverflow)
          this.EmitRaiseWhen(asm.Jno, 6);
        break;
      case BinaryOp.Multiply:
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
      case BinaryOp.IntegerDivide:
        asm.Call(unsignedDivide ? this._rt.LongDivU : this._rt.LongDiv);
        break;
      case BinaryOp.Modulo:
        asm.Call(unsignedDivide ? this._rt.LongModU : this._rt.LongMod);
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

  /// <summary>Converts the current value (registers/FPU per <paramref name="from"/>) into <paramref name="to"/>'s category.</summary>
  /// <summary>
  /// The BASCOM lineage (QB 1.0-3.0) rounds float-to-integer half AWAY from zero
  /// (CINT(2.5) = 3, CINT(-2.5) = -3, oracle-verified); QB 4.x and PB use the
  /// FPU's round-to-nearest-even. Biases ST(0) by +-0.5 and truncates so the
  /// following FISTP (nearest-even of an integral value) is exact.
  /// </summary>
  private void EmitDialectRounding() {
    if (!model.Dialect.IsBascomRuntime())
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

      case (ValueKind.Float, ValueKind.Int32):
        this.EmitDialectRounding();
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
