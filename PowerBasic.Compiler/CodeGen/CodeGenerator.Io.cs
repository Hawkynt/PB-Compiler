using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private static Expression UnwrapFileNumber(Expression e) => e is FileNumberExpr f ? f.Number : e;

  private void EmitPrint(PrintStmt p) {
    var asm = this._asm;
    if (p.IsLPrint) {
      this.Unsupported(p);
      return;
    }

    if (p.FileNumber != null) {
      this.EmitInt16Argument(UnwrapFileNumber(p.FileNumber));
      asm.Call(this._rt.FSelect);
    }

    if (p.UsingFormat != null) {
      this.EmitPrintUsing(p);
      if (p.FileNumber != null)
        asm.Mov(Mem.Word(this._asm.Lbl("rt_curout")), 1);
      return;
    }

    foreach (var item in p.Items) {
      if (item.Value is CallOrIndexExpr spcTab
          && model.IntrinsicBindings.TryGetValue(spcTab, out var printIntrinsic)
          && printIntrinsic.Name is "SPC" or "TAB") {
        this.EmitInt16Argument(spcTab.Arguments[0]);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Call(printIntrinsic.Name == "SPC" ? this._rt.Spc : this._rt.Tab);
        if (item.Separator == PrintSeparator.Comma)
          asm.Call(this._rt.PrintZone);
        continue;
      }
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

  /// <summary>
  /// PRINT USING with a literal format: '#'-runs (optionally '.' + '#'-runs)
  /// are numeric fields rendered fixed-point right-aligned; everything else
  /// prints verbatim. Non-literal formats and string fields stay unsupported.
  /// </summary>
  private void EmitPrintUsing(PrintStmt p) {
    var asm = this._asm;
    if (p.UsingFormat is not StringLiteralExpr formatLiteral) {
      this.Unsupported(p.UsingFormat!, "non-literal PRINT USING format");
      return;
    }

    var segments = ParseUsingFormat(formatLiteral.Value);
    var fieldIndex = 0;
    foreach (var item in p.Items) {
      if (item.Value == null)
        continue;
      // print literal text up to and including the next numeric field
      while (fieldIndex < segments.Count && segments[fieldIndex].Field == null) {
        this.EmitPrintLiteral(segments[fieldIndex].Literal!);
        ++fieldIndex;
      }
      if (fieldIndex >= segments.Count) {
        this.Unsupported(item.Value, "more PRINT USING values than fields");
        return;
      }
      var (width, decimals) = segments[fieldIndex].Field!.Value;
      ++fieldIndex;

      this.EmitExpression(item.Value);
      var itemType = model.TypeOf(item.Value);
      if (KindOf(itemType) == ValueKind.Str) {
        asm.Call(this._rt.StrPrint);   // string into a numeric field: print as-is (PB '&' approximation)
        continue;
      }
      this.Coerce(itemType, PbType.Double, item.Value);
      if (decimals > 0)
        asm.Fmul(Mem.Qword(this.FloatConstOf(Math.Pow(10, decimals))));
      asm.Fistp(Mem.Dword(this.RtScratch));
      asm.Mov(Reg.AX, Mem.Word(this.RtScratch));
      asm.Mov(Reg.DX, Mem.Word(this.RtScratch, 2));
      asm.Mov(Reg.CX, (width << 8) | decimals);
      asm.Call(this._rt.UseFmt);
    }

    // trailing literal text after the last consumed field
    while (fieldIndex < segments.Count && segments[fieldIndex].Field == null) {
      this.EmitPrintLiteral(segments[fieldIndex].Literal!);
      ++fieldIndex;
    }

    if (p.Items.Count == 0 || p.Items[^1].Separator == PrintSeparator.Newline)
      asm.Call(this._rt.PrintNewLine);
  }

  private void EmitPrintLiteral(string text) {
    if (text.Length == 0)
      return;
    var asm = this._asm;
    asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(text)));
    asm.Mov(Reg.CX, text.Length);
    asm.Call(this._rt.PrintStr);
  }

  private static List<(string? Literal, (int Width, int Decimals)? Field)> ParseUsingFormat(string format) {
    var segments = new List<(string?, (int, int)?)>();
    var literal = "";
    for (var i = 0; i < format.Length;) {
      if (format[i] != '#') {
        literal += format[i++];
        continue;
      }
      if (literal.Length > 0) {
        segments.Add((literal, null));
        literal = "";
      }
      var digits = 0;
      while (i < format.Length && format[i] == '#') {
        ++digits;
        ++i;
      }
      var decimals = 0;
      if (i < format.Length && format[i] == '.') {
        ++i;
        while (i < format.Length && format[i] == '#') {
          ++decimals;
          ++i;
        }
      }
      var width = digits + (decimals > 0 ? decimals + 1 : 0);
      segments.Add((null, (width, decimals)));
    }
    if (literal.Length > 0)
      segments.Add((literal, null));
    return segments;
  }

  private void EmitOpen(OpenStmt open) {
    var asm = this._asm;
    var mode = open.Mode switch {
      Syntax.Ast.FileMode.Input => 0,
      Syntax.Ast.FileMode.Output => 1,
      Syntax.Ast.FileMode.Append => 2,
      Syntax.Ast.FileMode.Random => 3,
      _ => 4,
    };

    this.EmitExpression(open.FileName);
    if (KindOf(model.TypeOf(open.FileName)) != ValueKind.Str) {
      this.Unsupported(open);
      return;
    }
    asm.Push(Reg.AX);
    this.EmitInt16Argument(UnwrapFileNumber(open.FileNumber));
    asm.Push(Reg.AX);
    if (open.RecordLength != null)
      this.EmitInt16Argument(open.RecordLength);
    else
      asm.Xor(Reg.AX, Reg.AX);
    asm.Mov(Reg.SI, Reg.AX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Mov(Reg.CX, mode);
    asm.Call(this._rt.FOpen);
  }

  /// <summary>
  /// GET/PUT #n [, record [, var]] - record I/O straight into the variable's
  /// storage; dynamic strings transfer their current LEN bytes in place.
  /// </summary>
  private void EmitGetPutFile(GetPutFileStmt gp) {
    var asm = this._asm;
    if (gp.Variable is not { } variable) {
      this.Unsupported(gp);
      return;
    }

    this.EmitInt16Argument(UnwrapFileNumber(gp.FileNumber));
    asm.Push(Reg.AX);
    if (gp.RecordNumber is { } record) {
      this.EmitExpression(record);
      this.Coerce(model.TypeOf(record), PbType.Long, record);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Pop(Reg.AX);
      asm.Push(Reg.AX);
      asm.Call(this._rt.FSetPos);
    }

    if (this.EmitPlace(variable) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }

    if (model.TypeOf(variable) is StringType or FlexType) {
      asm.Mov(Reg.DX, Adjust(place.Cell, 0, OperandSize.Word));   // raw handle
      asm.Pop(Reg.AX);
      asm.Call(gp.IsGet ? this._rt.FGetInto : this._rt.FPutRaw);
      return;
    }

    asm.Lea(Reg.DX, place.Cell);
    asm.Mov(Reg.SI, place.Far ? Reg.ES : Reg.DS);
    asm.Mov(Reg.CX, Math.Max(model.TypeOf(variable).Size, 1));
    asm.Pop(Reg.AX);
    asm.Call(this._rt.FHandle);
    asm.Call(gp.IsGet ? this._rt.FRead : this._rt.FWrite);
  }

  private void EmitSeekStatement(SeekStmt seek) {
    var asm = this._asm;
    this.EmitInt16Argument(UnwrapFileNumber(seek.FileNumber));
    asm.Push(Reg.AX);
    this.EmitExpression(seek.Target);
    this.Coerce(model.TypeOf(seek.Target), PbType.Long, seek.Target);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Call(asm.Lbl("rt_fseekstmt"));   // PB SEEK: 0-based bytes (BINARY) / 1-based records (RANDOM)
  }

  /// <summary>GET$ fh, count, var$ / PUT$ fh, var$ (CommandStmt form).</summary>
  private void EmitGetPutString(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Keyword == "GET$") {
      if (cmd.Arguments is not [{ } file, { } count, { } target]) {
        this.Unsupported(cmd);
        return;
      }
      this.EmitInt16Argument(file);
      asm.Push(Reg.AX);
      this.EmitInt16Argument(count);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Pop(Reg.AX);
      asm.Call(this._rt.FGetStr);
      asm.Push(Reg.AX);
      if (this.EmitPlace(target) is not { } place) {
        asm.Pop(Reg.AX);
        return;
      }
      asm.Pop(Reg.AX);
      this.EmitStorePlace(place, PbType.String, target);
      return;
    }

    if (cmd.Arguments is not [{ } fileNo, { } value]) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(fileNo);
    asm.Push(Reg.AX);
    this.EmitExpression(value);
    asm.Mov(Reg.DX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Call(this._rt.FPutStr);
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

  /// <summary>
  /// INPUT / LINE INPUT, console and file form. The console is PB file number 0
  /// (stdin); INPUT items are comma-separated tokens converted per target type.
  /// </summary>
  private void EmitInput(InputStmt input) {
    var asm = this._asm;
    if (input.FileNumber == null) {
      if (input.Prompt is { } prompt && prompt.Length > 0) {
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf(prompt)));
        asm.Mov(Reg.CX, prompt.Length);
        asm.Call(this._rt.PrintStr);
      }
      if ((input.Prompt == null && !input.IsLineInput) || input.PromptSemicolon) {
        asm.Mov(Reg.SI, Imm.OffsetOf(this.LiteralOf("? ")));
        asm.Mov(Reg.CX, 2);
        asm.Call(this._rt.PrintStr);
      }
    }

    foreach (var target in input.Targets) {
      if (input.FileNumber is { } fileNumber)
        this.EmitInt16Argument(UnwrapFileNumber(fileNumber));
      else
        asm.Xor(Reg.AX, Reg.AX);
      asm.Call(input.IsLineInput ? this._rt.LInput : this._rt.FToken);
      this.EmitStoreReadValue(target);
    }
  }

  /// <summary>Stores the string handle in AX into <paramref name="target"/>, converting via VAL for numeric targets.</summary>
  private void EmitStoreReadValue(Expression target) {
    var asm = this._asm;
    var targetType = model.TypeOf(target);
    switch (KindOf(targetType)) {
      case ValueKind.Str:
        asm.Push(Reg.AX);
        if (this.EmitPlace(target) is not { } strPlace) {
          asm.Pop(Reg.AX);
          return;
        }
        asm.Pop(Reg.AX);
        this.EmitStorePlace(strPlace, targetType, target);
        break;

      default:
        asm.Call(this._rt.Val);                       // handle -> ST0 (consumed)
        this.Coerce(PbType.Double, targetType, target);
        var kind = KindOf(targetType);
        if (kind == ValueKind.Int32)
          asm.Push(Reg.DX);
        if (kind != ValueKind.Float)
          asm.Push(Reg.AX);
        if (this.EmitPlace(target) is not { } place) {
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
        this.EmitStorePlace(place, targetType, target);
        break;
    }
  }
}
