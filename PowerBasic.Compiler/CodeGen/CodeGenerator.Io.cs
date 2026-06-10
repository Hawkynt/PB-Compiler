using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private static Expression UnwrapFileNumber(Expression e) => e is FileNumberExpr f ? f.Number : e;

  private void EmitPrint(PrintStmt p) {
    var asm = this._asm;
    if (p.IsLPrint || p.UsingFormat != null) {
      this.Unsupported(p);
      return;
    }

    if (p.FileNumber != null) {
      this.EmitInt16Argument(UnwrapFileNumber(p.FileNumber));
      asm.Call(this._rt.FSelect);
    }

    foreach (var item in p.Items) {
      if (item.Value is StringLiteralExpr lit) {
        if (lit.Value.Length > 0) {
          asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(lit.Value)));
          asm.Mov(Reg.CX, lit.Value.Length);
          asm.Call(this._rt.PrintStr);
        }
      } else if (item.Value != null) {
        this.EmitExpression(item.Value);
        switch (KindOf(model.TypeOf(item.Value))) {
          case ValueKind.Int16:
            asm.Call(this._rt.PrintInt16);
            break;
          case ValueKind.Int32:
            asm.Call(this._rt.PrintInt32);
            break;
          case ValueKind.Float when model.TypeOf(item.Value).Size == 4:
            asm.Call(this._rt.PrintSingle);
            break;
          case ValueKind.Float:
            asm.Call(this._rt.PrintDouble);
            break;
          case ValueKind.Str:
            asm.Call(this._rt.StrPrint);
            break;
          default:
            this.Unsupported(item.Value, "PRINT of this type");
            break;
        }
      }

      if (item.Separator == PrintSeparator.Comma)
        asm.Call(this._rt.PrintZone);
    }

    var lastSeparator = p.Items.Count == 0 ? PrintSeparator.Newline : p.Items[^1].Separator;
    if (lastSeparator == PrintSeparator.Newline)
      asm.Call(this._rt.PrintNewLine);

    if (p.FileNumber != null)
      asm.Mov(Mem.Word(this._asm.Lbl("rt_curout")), 1);
  }

  private void EmitOpen(OpenStmt open) {
    var asm = this._asm;
    var mode = open.Mode switch {
      Syntax.Ast.FileMode.Input => 0,
      Syntax.Ast.FileMode.Output => 1,
      Syntax.Ast.FileMode.Append => 2,
      _ => -1,
    };
    if (mode < 0) {
      this.Unsupported(open);   // RANDOM/BINARY need record I/O
      return;
    }

    this.EmitExpression(open.FileName);
    if (KindOf(model.TypeOf(open.FileName)) != ValueKind.Str) {
      this.Unsupported(open);
      return;
    }
    asm.Push(Reg.AX);
    this.EmitInt16Argument(UnwrapFileNumber(open.FileNumber));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Mov(Reg.CX, mode);
    asm.Call(this._rt.FOpen);
  }

  private void EmitClose(CloseStmt close) {
    var asm = this._asm;
    if (close.FileNumbers.Count == 0) {
      asm.Call(this._rt.FCloseAll);
      return;
    }
    foreach (var number in close.FileNumbers) {
      this.EmitInt16Argument(UnwrapFileNumber(number));
      asm.Call(this._rt.FClose);
    }
  }

  private void EmitInput(InputStmt input) {
    var asm = this._asm;
    if (!input.IsLineInput || input.FileNumber == null || input.Targets is not [{ } target]
        || model.TypeOf(target) is not StringType) {
      this.Unsupported(input);   // console INPUT and field lists are out of scope
      return;
    }

    this.EmitInt16Argument(UnwrapFileNumber(input.FileNumber));
    asm.Call(this._rt.LInput);
    asm.Push(Reg.AX);
    if (this.EmitPlace(target) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }
    asm.Pop(Reg.AX);
    this.EmitStorePlace(place, PbType.String, target);
  }
}
