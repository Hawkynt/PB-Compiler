using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private void EmitExpression(Expression expression) {
    var asm = this._asm;
    switch (expression) {
      case IntegerLiteralExpr i:
        if (KindOf(model.TypeOf(i)) == ValueKind.Int16)
          asm.Mov(Reg.AX, (int)i.Value);
        else {
          asm.Mov(Reg.AX, (int)(i.Value & 0xFFFF));
          asm.Mov(Reg.DX, (int)((i.Value >> 16) & 0xFFFF));
        }
        break;

      case FloatLiteralExpr f:
        asm.Fld(Mem.Qword(this.FloatConstOf(f.Value)));
        break;

      case StringLiteralExpr s:
        this.EmitStringLiteral(s.Value);
        break;

      case NamedConstantExpr c: {
        if (model.Equates.TryGetValue(c.Name, out var v) && v.Text is { } text) {
          this.EmitStringLiteral(text);
          break;
        }
        var value = v.AsInteger;
        if (KindOf(model.TypeOf(c)) == ValueKind.Int16)
          asm.Mov(Reg.AX, (int)value);
        else {
          asm.Mov(Reg.AX, (int)(value & 0xFFFF));
          asm.Mov(Reg.DX, (int)((value >> 16) & 0xFFFF));
        }
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

      case UnaryExpr u:
        this.EmitUnary(u);
        break;

      case BinaryExpr b:
        this.EmitBinary(b);
        break;

      case FileNumberExpr fn:
        this.EmitExpression(fn.Number);
        this.Coerce(model.TypeOf(fn.Number), PbType.Integer, fn.Number);
        break;

      default:
        this.Unsupported(expression, expression.GetType().Name);
        break;
    }
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
      case (UnaryOp.Negate, ValueKind.Int16):
        asm.Neg(Reg.AX);
        break;
      case (UnaryOp.Negate, ValueKind.Int32):
        asm.Not(Reg.DX);
        asm.Neg(Reg.AX);
        asm.Sbb(Reg.DX, -1);
        break;
      case (UnaryOp.Negate, ValueKind.Float):
        asm.Fchs();
        break;
      case (UnaryOp.Not, ValueKind.Int16):
        asm.Not(Reg.AX);
        break;
      case (UnaryOp.Not, ValueKind.Int32):
        asm.Not(Reg.AX);
        asm.Not(Reg.DX);
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

    if (KindOf(leftType) == ValueKind.Str || KindOf(rightType) == ValueKind.Str) {
      this.EmitStringBinary(b);
      return;
    }

    // arithmetic runs in the result type; comparisons in the widest operand type
    var opType = b.Op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual
      ? WidestOf(leftType, rightType)
      : resultType;

    switch (KindOf(opType)) {
      case ValueKind.Int16:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        asm.Push(Reg.AX);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Pop(Reg.AX);
        this.EmitInt16Op(b);
        break;

      case ValueKind.Int32:
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
        this.EmitInt32Op(b);
        break;

      case ValueKind.Float:
        this.EmitExpression(b.Left);
        this.Coerce(leftType, opType, b.Left);
        this.EmitExpression(b.Right);
        this.Coerce(rightType, opType, b.Right);
        this.EmitFloatOp(b);
        break;

      default:
        this.Unsupported(b, "binary op on this type");
        break;
    }
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

    if (b.Op == BinaryOp.Add) {
      asm.Call(this._rt.StrCat);
      return;
    }

    asm.Call(this._rt.StrCmp);
    asm.Xor(Reg.BX, Reg.BX);
    switch (b.Op) {
      case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jl); break;
      case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Jg); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jle); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jge); break;
      default:
        this.Unsupported(b, $"string {b.Op}");
        break;
    }
  }

  /// <summary>left AX, right BX -> result AX.</summary>
  private void EmitInt16Op(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add: asm.Add(Reg.AX, Reg.BX); break;
      case BinaryOp.Subtract: asm.Sub(Reg.AX, Reg.BX); break;
      case BinaryOp.Multiply: asm.Imul(Reg.BX); break;
      case BinaryOp.IntegerDivide:
        asm.Cwd();
        asm.Idiv(Reg.BX);
        break;
      case BinaryOp.Modulo:
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
      case BinaryOp.Equal: this.EmitInt16Compare(asm => asm.Je); break;
      case BinaryOp.NotEqual: this.EmitInt16Compare(asm => asm.Jne); break;
      case BinaryOp.Less: this.EmitInt16Compare(asm => asm.Jl); break;
      case BinaryOp.Greater: this.EmitInt16Compare(asm => asm.Jg); break;
      case BinaryOp.LessEqual: this.EmitInt16Compare(asm => asm.Jle); break;
      case BinaryOp.GreaterEqual: this.EmitInt16Compare(asm => asm.Jge); break;
      default:
        this.Unsupported(b, $"int16 {b.Op}");
        break;
    }
  }

  private void EmitInt16Compare(Func<Assembler, Action<Label>> jump) {
    var asm = this._asm;
    var done = asm.DefineLabel();
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Mov(Reg.AX, -1);    // MOV leaves flags intact
    jump(asm)(done);
    asm.Mov(Reg.AX, (Imm)0);
    asm.MarkLabel(done);
  }

  /// <summary>left DX:AX, right CX:BX -> result DX:AX.</summary>
  private void EmitInt32Op(BinaryExpr b) {
    var asm = this._asm;
    switch (b.Op) {
      case BinaryOp.Add:
        asm.Add(Reg.AX, Reg.BX);
        asm.Adc(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Subtract:
        asm.Sub(Reg.AX, Reg.BX);
        asm.Sbb(Reg.DX, Reg.CX);
        break;
      case BinaryOp.Multiply:
        asm.Call(this._rt.LongMul);
        break;
      case BinaryOp.IntegerDivide:
        asm.Call(this._rt.LongDiv);
        break;
      case BinaryOp.Modulo:
        asm.Call(this._rt.LongMod);
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
  private void Coerce(PbType from, PbType to, Expression at) {
    var asm = this._asm;
    var src = KindOf(from);
    var dst = KindOf(to);
    if (src == dst)
      return;

    switch (src, dst) {
      case (ValueKind.Int16, ValueKind.Int32):
        asm.Cwd();
        break;

      case (ValueKind.Int32, ValueKind.Int16):
        break; // keep AX (range checking is the full backend's job)

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
        asm.Fistp(Mem.Word(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        break;

      case (ValueKind.Float, ValueKind.Int32):
        asm.Fistp(Mem.Dword(this._scratch));
        asm.Mov(Reg.AX, Mem.Word(this._scratch));
        asm.Mov(Reg.DX, Mem.Word(this._scratch, 2));
        break;

      default:
        this.Unsupported(at, $"conversion {from} -> {to}");
        break;
    }
  }

  private static PbType WidestOf(PbType a, PbType b) {
    if (a is ScalarType { IsFloat: true } || b is ScalarType { IsFloat: true })
      return PbType.Double;
    if (a is ScalarType { ByteSize: > 2 } || b is ScalarType { ByteSize: > 2 })
      return PbType.Long;
    return PbType.Integer;
  }
}
