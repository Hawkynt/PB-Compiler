using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private void EmitIntrinsic(Expression call, IReadOnlyList<Expression> args, IntrinsicInfo intrinsic) {
    var asm = this._asm;

    switch (intrinsic.Name) {
      case "LEN": {
        var argType = model.TypeOf(args[0]);
        switch (argType) {
          case StringType or FlexType:
            this.EmitExpression(args[0]);
            asm.Call(this._rt.Len);
            asm.Cwd();
            break;
          default:
            asm.Mov(Reg.AX, argType.Size);   // fixed strings, UDTs and scalars: compile-time size
            asm.Cwd();
            break;
        }
        break;
      }

      case "LEFT$" or "RIGHT$":
        this.EmitExpression(args[0]);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(args[1]);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(intrinsic.Name == "LEFT$" ? this._rt.StrLeft : this._rt.StrRight);
        break;

      case "MID$":
        this.EmitExpression(args[0]);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(args[1]);
        asm.Push(Reg.AX);
        if (args.Count > 2)
          this.EmitInt16Argument(args[2]);
        else
          asm.Mov(Reg.AX, 0x7FFF);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.CX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.StrMid);
        break;

      case "INSTR": {
        var hasStart = args.Count > 2;
        if (hasStart) {
          this.EmitInt16Argument(args[0]);
          asm.Push(Reg.AX);
        }
        this.EmitExpression(args[hasStart ? 1 : 0]);
        asm.Push(Reg.AX);
        this.EmitExpression(args[hasStart ? 2 : 1]);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        if (hasStart)
          asm.Pop(Reg.CX);
        else
          asm.Mov(Reg.CX, 1);
        asm.Call(this._rt.Instr);
        asm.Cwd();
        break;
      }

      case "CHR$":
        this.EmitInt16Argument(args[0]);
        asm.Mov(Reg.DL, Reg.AL);
        asm.Call(this._rt.Chr);
        break;

      case "ASC" or "ASCII":
        this.EmitExpression(args[0]);
        if (args.Count > 1) {
          asm.Push(Reg.AX);
          this.EmitInt16Argument(args[1]);
          asm.Mov(Reg.CX, Reg.AX);
          asm.Pop(Reg.AX);
          asm.Mov(Reg.DX, 1);
          asm.Call(this._rt.StrMid);
        }
        asm.Call(this._rt.Asc);
        break;

      case "STR$":
        this.EmitExpression(args[0]);
        switch (KindOf(model.TypeOf(args[0]))) {
          case ValueKind.Int16: asm.Call(this._rt.StrI16); break;
          case ValueKind.Int32: asm.Call(this._rt.StrI32); break;
          case ValueKind.Float: asm.Call(this._rt.StrF64); break;
          default:
            this.Unsupported(call, "STR$ argument");
            break;
        }
        break;

      case "VAL":
        this.EmitExpression(args[0]);
        asm.Call(this._rt.Val);
        break;

      case "STRING$":
        this.EmitInt16Argument(args[0]);
        asm.Push(Reg.AX);
        if (KindOf(model.TypeOf(args[1])) == ValueKind.Str) {
          this.EmitExpression(args[1]);
          asm.Call(this._rt.Asc);
        } else
          this.EmitInt16Argument(args[1]);
        asm.Mov(Reg.DL, Reg.AL);
        asm.Pop(Reg.CX);
        asm.Call(this._rt.StrFill);
        break;

      case "SPACE$":
        this.EmitInt16Argument(args[0]);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Mov(Reg.DL, (Imm)' ');
        asm.Call(this._rt.StrFill);
        break;

      case "UCASE$" or "LCASE$":
        this.EmitExpression(args[0]);
        asm.Call(intrinsic.Name == "UCASE$" ? this._rt.StrUpr : this._rt.StrLwr);
        break;

      case "LTRIM$" or "RTRIM$":
        this.EmitExpression(args[0]);
        asm.Call(intrinsic.Name == "LTRIM$" ? this._rt.LTrim : this._rt.RTrim);
        break;

      case "HEX$" or "OCT$" or "BIN$": {
        var bits = intrinsic.Name switch { "HEX$" => 4, "OCT$" => 3, _ => 1 };
        var digits = 1;
        if (args.Count > 1) {
          if (args[1] is IntegerLiteralExpr d)
            digits = (int)d.Value;
          else {
            this.Unsupported(call, $"{intrinsic.Name} with non-constant digit count");
            break;
          }
        }
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
        asm.Mov(Reg.CX, (Math.Clamp(digits, 1, 32) << 8) | bits);
        asm.Call(this._rt.Radix);
        break;
      }

      case "REPEAT$":
        this.EmitInt16Argument(args[0]);
        asm.Push(Reg.AX);
        this.EmitExpression(args[1]);
        asm.Pop(Reg.CX);
        asm.Call(this._rt.Repeat);
        break;

      case "EOF":
        this.EmitInt16Argument(args[0]);
        asm.Call(this._rt.Eof);
        break;

      case "FREEFILE":
        asm.Call(this._rt.FreeFile);
        break;

      case "UBOUND" or "LBOUND":
        this.EmitBound(call, args, intrinsic.Name == "UBOUND");
        break;

      case "ABS":
        this.EmitExpression(args[0]);
        switch (KindOf(model.TypeOf(args[0]))) {
          case ValueKind.Int16: {
            var done = asm.DefineLabel();
            asm.Test(Reg.AX, Reg.AX);
            asm.Jns(done);
            asm.Neg(Reg.AX);
            asm.MarkLabel(done);
            break;
          }
          case ValueKind.Int32: {
            var done = asm.DefineLabel();
            asm.Test(Reg.DX, Reg.DX);
            asm.Jns(done);
            asm.Not(Reg.DX);
            asm.Neg(Reg.AX);
            asm.Sbb(Reg.DX, -1);
            asm.MarkLabel(done);
            break;
          }
          case ValueKind.Float:
            asm.Fabs();
            break;
        }
        break;

      case "SGN": {
        this.EmitExpression(args[0]);
        var type = model.TypeOf(args[0]);
        this.Coerce(type, KindOf(type) == ValueKind.Float ? PbType.Double : PbType.Long, args[0]);
        if (KindOf(type) == ValueKind.Float) {
          asm.Ftst();
          asm.FstswAx();
          asm.Fstp(St.St0);
          asm.Sahf();
          var negative = asm.DefineLabel();
          var zero = asm.DefineLabel();
          var done = asm.DefineLabel();
          asm.Jz(zero);
          asm.Jb(negative);
          asm.Mov(Reg.AX, 1);
          asm.Jmp(done);
          asm.MarkLabel(negative);
          asm.Mov(Reg.AX, -1);
          asm.Jmp(done);
          asm.MarkLabel(zero);
          asm.Xor(Reg.AX, Reg.AX);
          asm.MarkLabel(done);
        } else {
          var negative = asm.DefineLabel();
          var done = asm.DefineLabel();
          var zero = asm.DefineLabel();
          asm.Test(Reg.DX, Reg.DX);
          asm.Js(negative);
          asm.Or(Reg.AX, Reg.DX);
          asm.Jz(zero);
          asm.Mov(Reg.AX, 1);
          asm.Jmp(done);
          asm.MarkLabel(negative);
          asm.Mov(Reg.AX, -1);
          asm.Jmp(done);
          asm.MarkLabel(zero);
          asm.Xor(Reg.AX, Reg.AX);
          asm.MarkLabel(done);
        }
        break;
      }

      case "CINT" or "CBYT" or "CWRD":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Integer, args[0]);
        break;

      case "CLNG" or "CDWD":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
        break;

      case "CSNG" or "CDBL" or "CEXT":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        break;

      case "SQR":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        asm.Fsqrt();
        break;

      case "INT" or "FIX":
        this.EmitExpression(args[0]);
        if (KindOf(model.TypeOf(args[0])) == ValueKind.Float)
          asm.Frndint();   // rounding mode caveat: nearest-even, not floor
        break;

      case "ISTRUE" or "ISFALSE": {
        this.EmitCondition(args[0]);
        var done = asm.DefineLabel();
        var isTrue = intrinsic.Name == "ISTRUE";
        asm.Mov(Reg.AX, isTrue ? 0 : -1);
        asm.Jz(done);
        asm.Mov(Reg.AX, isTrue ? -1 : 0);
        asm.MarkLabel(done);
        break;
      }

      default:
        this.Unsupported(call, $"intrinsic {intrinsic.Name}");
        break;
    }
  }

  /// <summary>Evaluates an argument and coerces it to a 16-bit integer in AX.</summary>
  private void EmitInt16Argument(Expression e) {
    this.EmitExpression(e);
    this.Coerce(model.TypeOf(e), PbType.Integer, e);
  }
}
