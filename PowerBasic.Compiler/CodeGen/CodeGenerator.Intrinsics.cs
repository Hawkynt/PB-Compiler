using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
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
          case AsciizType asciiz: // chars before the NUL
            if (this.EmitPlace(args[0]) is not { } azPlace) {
              this.Unsupported(call, "LEN of this ASCIIZ expression");
              break;
            }
            asm.Lea(Reg.SI, azPlace.Cell);
            asm.Mov(Reg.DX, azPlace.Far ? Reg.ES : Reg.DS);
            asm.Mov(Reg.CX, asciiz.Length);
            asm.Call(this._rt.AsciizLen);
            asm.Cwd();
            break;
          default:
            asm.Mov(Reg.AX, argType.Size);   // fixed strings, UDTs and scalars: compile-time size
            asm.Cwd();
            break;
        }
        break;
      }

      case "SIZEOF": {
        // storage size, compile-time; dynamic strings report their 2-byte handle
        var sizeofType = model.TypeOf(args[0]);
        asm.Mov(Reg.AX, Math.Max(sizeofType.Size, 1));
        asm.Cwd();
        break;
      }

      case "TRIM$":
        this.EmitExpression(args[0]);
        asm.Call(this._rt.LTrim);
        asm.Call(this._rt.RTrim);
        break;

      case "ERRCLEAR": // function form: yields the pending error code and clears it
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_err")));
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        break;

      case "CONSIN" or "CONSOUT": {
        // IOCTL get-device-info on handle 0/1: bit 7 set = console, clear = redirected
        var console = asm.DefineLabel();
        asm.Mov(Reg.AX, 0x4400);
        asm.Mov(Reg.BX, intrinsic.Name == "CONSIN" ? 0 : 1);
        asm.Int(0x21);
        asm.Mov(Reg.AX, -1);
        asm.Test(Reg.DL, (Imm)0x80);
        asm.Jnz(console);
        asm.Xor(Reg.AX, Reg.AX);
        asm.MarkLabel(console);
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

      case "INSTR" or "VERIFY": {
        var hasStart = args.Count > 2;
        var needle = args[hasStart ? 2 : 1];
        if (hasStart) {
          this.EmitInt16Argument(args[0]);
          asm.Push(Reg.AX);
        }
        this.EmitExpression(args[hasStart ? 1 : 0]);
        asm.Push(Reg.AX);
        this.EmitExpression(needle);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        if (hasStart)
          asm.Pop(Reg.CX);
        else
          asm.Mov(Reg.CX, 1);
        if (intrinsic.Name == "VERIFY") {
          asm.Mov(Reg.BX, 1);              // find the first NON-member
          asm.Call(this._rt.ScanSet);
        } else if (needle is AnyMatchExpr) {
          asm.Xor(Reg.BX, Reg.BX);         // INSTR ANY: first member of the set
          asm.Call(this._rt.ScanSet);
        } else
          asm.Call(this._rt.Instr);
        asm.Cwd();
        break;
      }

      case "EXTRACT$" or "TALLY": {
        this.EmitExpression(args[0]);
        asm.Push(Reg.AX);
        this.EmitExpression(args[1]);
        asm.Mov(Reg.DX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Mov(Reg.BX, args[1] is AnyMatchExpr ? 1 : 0);
        asm.Call(intrinsic.Name == "EXTRACT$" ? this._rt.Extract : this._rt.Tally);
        if (intrinsic.Name == "TALLY")
          asm.Cwd();
        break;
      }

      case "COMMAND$":
        asm.Call(this._rt.Command);
        break;

      case "USING$": { // PRINT USING into a string via capture mode
        if (args[0] is not StringLiteralExpr usingFormat) {
          // runtime format: single numeric field supported via rt_usingdyn
          if (args.Count != 2) {
            this.Unsupported(call, "non-literal USING$ format with multiple values");
            break;
          }
          this.EmitExpression(args[1]);
          this.Coerce(model.TypeOf(args[1]), PbType.Double, args[1]);
          this.EmitExpression(args[0]);   // format handle (string eval never touches the FPU)
          asm.Call(this._rt.UsingDyn);
          break;
        }
        asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
        asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
        this.EmitUsingBody(usingFormat.Value, args.Skip(1));
        asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
        asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_caplen")));
        asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_capbuf")));
        asm.Mov(Reg.DX, Reg.DS);
        asm.Call(this._rt.StrMem);
        break;
      }

      case "ENVIRON$":
        this.EmitExpression(args[0]);
        asm.Call(this._rt.Environ);
        break;

      case "TIME$":
        asm.Call(this._rt.TimeStr);
        break;

      case "DATE$":
        asm.Call(this._rt.DateStr);
        break;

      case "INPUT$": // INPUT$(n [, [#]f]) - file read or blocking keyboard read
        this.EmitInt16Argument(args[0]);
        if (args.Count > 1) {
          asm.Push(Reg.AX);
          this.EmitInt16Argument(UnwrapFileNumber(args[1]));
          asm.Pop(Reg.CX);
          asm.Call(this._rt.FGetStr);
        } else {
          asm.Mov(Reg.CX, Reg.AX);
          asm.Call(this._rt.KeyInput);
        }
        break;

      case "FILEATTR": {
        // only FILEATTR(n, 2) = DOS handle is meaningful on this runtime
        if (args[1] is not IntegerLiteralExpr { Value: 2 }) {
          this.Unsupported(call, "FILEATTR attribute (only 2 = DOS handle)");
          break;
        }
        this.EmitInt16Argument(UnwrapFileNumber(args[0]));
        asm.Call(this._rt.FHandle);
        asm.Mov(Reg.AX, Reg.BX);
        asm.Cwd();
        break;
      }

      case "CURDIR$":
        if (args.Count > 0) { // drive argument: only the default drive is modelled
          this.EmitExpression(args[0]);
          if (KindOf(model.TypeOf(args[0])) == ValueKind.Str)
            asm.Call(this._rt.StrFree);
        }
        asm.Call(this._rt.CurDir);
        break;

      case "DIR$": {
        if (args.Count >= 1) {
          this.EmitExpression(args[0]);
          if (args.Count > 1) {
            asm.Push(Reg.AX);
            this.EmitInt16Argument(args[1]);
            asm.Mov(Reg.CX, Reg.AX);
            asm.Pop(Reg.AX);
          } else
            asm.Xor(Reg.CX, Reg.CX);
        } else {
          asm.Xor(Reg.AX, Reg.AX);          // find-next form
          asm.Xor(Reg.CX, Reg.CX);
        }
        asm.Call(this._rt.Dir);
        break;
      }

      case "CHR$": // variadic: CHR$(a, b, c) concatenates the character codes
        this.EmitInt16Argument(args[0]);
        asm.Mov(Reg.DL, Reg.AL);
        asm.Call(this._rt.Chr);
        for (var i = 1; i < args.Count; ++i) {
          asm.Push(Reg.AX);
          this.EmitInt16Argument(args[i]);
          asm.Mov(Reg.DL, Reg.AL);
          asm.Call(this._rt.Chr);
          asm.Mov(Reg.DX, Reg.AX);
          asm.Pop(Reg.AX);
          asm.Call(this._rt.StrCat);
        }
        break;

      case "PEEK$": // PEEK$(offset, count) - bytes at DEF SEG:offset
        this.EmitInt16Argument(args[0]);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(args[1]);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.SI);
        asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_defseg")));
        asm.Call(this._rt.StrMem);
        break;

      case "INSTAT": { // keyboard status: -1 when a key is waiting
        var noKey = asm.DefineLabel();
        var doneInstat = asm.DefineLabel();
        asm.Mov(Reg.AH, (Imm)1);
        asm.Int(0x16);
        asm.Jz(noKey);
        asm.Mov(Reg.AX, -1);
        asm.Jmp(doneInstat);
        asm.MarkLabel(noKey);
        asm.Xor(Reg.AX, Reg.AX);
        asm.MarkLabel(doneInstat);
        break;
      }

      case "SETMEM": // memory management is not modelled: report a large stable figure
        this.EmitExpression(args[0]);
        if (KindOf(model.TypeOf(args[0])) == ValueKind.Float)
          asm.Fstp(St.St0);
        asm.Mov(Reg.AX, 0x7FFF);
        asm.Cwd();
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
          case ValueKind.Int32 when model.TypeOf(args[0]) is ScalarType { Signed: false }:
            // DWORD renders unsigned: zero-extend into the 64-bit formatter
            asm.Mov(Mem.Word(this.RtScratch), Reg.AX);
            asm.Mov(Mem.Word(this.RtScratch, 2), Reg.DX);
            asm.Mov(Mem.Word(this.RtScratch, 4), (Imm)0);
            asm.Mov(Mem.Word(this.RtScratch, 6), (Imm)0);
            asm.Fild(Mem.Qword(this.RtScratch));
            asm.Call(this._rt.StrI64);
            break;
          case ValueKind.Int32: asm.Call(this._rt.StrI32); break;
          case ValueKind.Int64: asm.Call(this._rt.StrF64); break; // QUAD mirrors PRINT (float formatter)
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
        if (model.Dialect >= Dialect.Pb31)
          this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
        else {
          // 32-bit LONG arguments arrived with 3.1; older dialects render
          // 16 bits (HEX$(-1) = "FFFF", not "FFFFFFFF")
          this.Coerce(model.TypeOf(args[0]), PbType.Integer, args[0]);
          asm.Xor(Reg.DX, Reg.DX);
        }
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
          case ValueKind.Float or ValueKind.Int64:
            asm.Fabs();
            break;
        }
        break;

      case "SGN": {
        this.EmitExpression(args[0]);
        var type = model.TypeOf(args[0]);
        var onFpu = KindOf(type) is ValueKind.Float or ValueKind.Int64;
        this.Coerce(type, onFpu ? PbType.Double : PbType.Long, args[0]);
        if (onFpu) {
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

      case "MIN" or "MAX" or "MIN%" or "MAX%": {
        // fold on the FPU: accumulator in ST1, candidate in ST0
        var wantMax = intrinsic.Name.StartsWith("MAX", StringComparison.Ordinal);
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        for (var i = 1; i < args.Count; ++i) {
          this.EmitExpression(args[i]);
          this.Coerce(model.TypeOf(args[i]), PbType.Double, args[i]);
          var keepNew = asm.DefineLabel();
          var next = asm.DefineLabel();
          asm.Fcom();                  // ST0 (new) vs ST1 (acc)
          asm.FstswAx();
          asm.Sahf();
          if (wantMax)
            asm.Ja(keepNew);
          else
            asm.Jb(keepNew);
          asm.Fstp(St.St0);            // drop the candidate
          asm.Jmp(next);
          asm.MarkLabel(keepNew);
          asm.Fstp(St.St1);            // candidate replaces the accumulator
          asm.MarkLabel(next);
        }
        this.Coerce(PbType.Double, model.TypeOf(call), call);
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

      case "CQUD": // round to the nearest 64-bit integer
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Quad, args[0]);
        break;

      case "CFIX": // round to pbvFixDigits decimals
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        asm.Call(asm.Lbl("rt_fixup"));
        asm.Call(asm.Lbl("rt_fixdn"));
        break;

      case "CBCD": // identity on the x87 stack (full source precision carried)
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
          asm.Call(intrinsic.Name == "INT" ? this._rt.Floor : this._rt.Trunc);
        break;

      case "PEEK":
        this.EmitPeek(args, 1);
        break;
      case "PEEKI":
        this.EmitPeek(args, 2);
        break;
      case "PEEKL":
        this.EmitPeek(args, 4);
        break;

      case "INP":
        this.EmitInt16Argument(args[0]);
        asm.Mov(Reg.DX, Reg.AX);
        asm.In(Reg.AL, Reg.DX);
        asm.Xor(Reg.AH, Reg.AH);
        break;

      case "VARPTR" or "VARSEG":
        this.EmitVarPtrSeg(call, args, intrinsic.Name == "VARSEG");
        break;

      case "VARPTR32": // DX:AX = seg:off of the variable's storage
        if (this.EmitPlace(args[0]) is { } vp32) {
          asm.Lea(Reg.AX, vp32.Cell);
          asm.Mov(Reg.DX, vp32.Far ? Reg.ES : Reg.DS);
        } else
          this.Unsupported(call, "VARPTR32 argument");
        break;

      case "STRPTR32": // DX:AX = seg:off of the string data in the heap
        if (model.TypeOf(args[0]) is StringType or FlexType && this.EmitPlace(args[0]) is { } sp32) {
          asm.Mov(Reg.AX, Adjust(sp32.Cell, 0, OperandSize.Word));
          asm.Call(this._rt.StrPtr);
          asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_strseg")));
        } else
          this.Unsupported(call, "STRPTR32 argument");
        break;

      case "STRPTR" or "STRSEG":
        this.EmitStrPtrSeg(call, args, intrinsic.Name == "STRSEG");
        break;

      case "CODEPTR" or "CODESEG" or "CODEPTR32":
        this.EmitCodePtr(call, args, intrinsic.Name);
        break;

      case "REG":
        this.EmitRegFunction(args);
        break;

      case "ERR":
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_err")));
        break;

      case "ERL": // last executed numeric line label (tracked in error-handling scopes)
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_erl")));
        asm.Cwd();
        break;

      case "ERDEV": // device-error stub
        asm.Xor(Reg.AX, Reg.AX);
        break;

      case "ERDEV$":
        asm.Xor(Reg.AX, Reg.AX);   // empty string handle
        break;

      case "BIT": {
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
        asm.Push(Reg.DX);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(args[1]);
        asm.Mov(Reg.CX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Pop(Reg.DX);
        var noShift = asm.DefineLabel();
        var shift = asm.DefineLabel();
        asm.Jcxz(noShift);
        asm.MarkLabel(shift);
        asm.Shr(Reg.DX, 1);
        asm.Rcr(Reg.AX, 1);
        asm.Loop(shift);
        asm.MarkLabel(noShift);
        asm.And(Reg.AX, 1);
        break;
      }

      case "LOF":
        this.EmitInt16Argument(UnwrapFileNumber(args[0]));
        asm.Call(this._rt.Lof);
        break;

      case "SEEK" or "LOC":
        this.EmitInt16Argument(UnwrapFileNumber(args[0]));
        asm.Call(this._rt.FPos);
        break;

      case "CVI" or "CVWRD":
        this.EmitCvSource(args, 2);
        asm.Mov(Reg.CX, 2);
        asm.Call(this._rt.Cv);
        asm.Mov(Reg.AX, Mem.Word(this.RtScratch));
        break;

      case "CVBYT":
        this.EmitCvSource(args, 1);
        asm.Mov(Reg.CX, 1);
        asm.Call(this._rt.Cv);
        asm.Mov(Reg.AL, Mem.Byte(this.RtScratch));
        asm.Xor(Reg.AH, Reg.AH);
        break;

      case "CVL" or "CVDWD":
        this.EmitCvSource(args, 4);
        asm.Mov(Reg.CX, 4);
        asm.Call(this._rt.Cv);
        asm.Mov(Reg.AX, Mem.Word(this.RtScratch));
        asm.Mov(Reg.DX, Mem.Word(this.RtScratch, 2));
        break;

      case "CVS":
        this.EmitCvSource(args, 4);
        asm.Mov(Reg.CX, 4);
        asm.Call(this._rt.Cv);
        asm.Fld(Mem.Dword(this.RtScratch));
        break;

      case "CVD" or "CVE":
        this.EmitCvSource(args, 8);
        asm.Mov(Reg.CX, 8);
        asm.Call(this._rt.Cv);
        asm.Fld(Mem.Qword(this.RtScratch));
        break;

      case "MKI$" or "MKWRD$":
        this.EmitInt16Argument(args[0]);
        asm.Mov(Mem.Word(this.RtScratch), Reg.AX);
        this.EmitScratchString(2);
        break;

      case "MKBYT$":
        this.EmitInt16Argument(args[0]);
        asm.Mov(Mem.Byte(this.RtScratch), Reg.AL);
        this.EmitScratchString(1);
        break;

      case "MKL$" or "MKDWD$":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
        asm.Mov(Mem.Word(this.RtScratch), Reg.AX);
        asm.Mov(Mem.Word(this.RtScratch, 2), Reg.DX);
        this.EmitScratchString(4);
        break;

      case "MKS$":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        asm.Fstp(Mem.Dword(this.RtScratch));
        this.EmitScratchString(4);
        break;

      case "MKD$" or "MKE$":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        asm.Fstp(Mem.Qword(this.RtScratch));
        this.EmitScratchString(8);
        break;

      case "RND":
        if (args.Count == 2) { // RND(a, z) -> LONG in [a, z] (PB 3.5)
          this.EmitExpression(args[0]);
          this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);
          asm.Push(Reg.DX);
          asm.Push(Reg.AX);
          this.EmitExpression(args[1]);
          this.Coerce(model.TypeOf(args[1]), PbType.Long, args[1]);
          asm.Mov(Reg.BX, Reg.AX);
          asm.Mov(Reg.CX, Reg.DX);
          asm.Pop(Reg.AX);
          asm.Pop(Reg.DX);
          asm.Call(this._rt.RndRange);
          break;
        }
        if (args.Count > 0) {
          this.EmitExpression(args[0]);    // RND(n) reseed semantics are not modelled
          if (KindOf(model.TypeOf(args[0])) == ValueKind.Float)
            asm.Fstp(St.St0);
        }
        asm.Call(this._rt.Rnd);
        break;

      case "TIMER":
        asm.Call(this._rt.Timer);
        break;

      case "INKEY$":
        asm.Call(this._rt.InKey);
        break;

      case "SIN" or "COS" or "TAN" or "ATN" or "LOG" or "LOG2" or "LOG10" or "EXP" or "EXP2" or "EXP10":
        this.EmitExpression(args[0]);
        this.Coerce(model.TypeOf(args[0]), PbType.Double, args[0]);
        switch (intrinsic.Name) {
          case "SIN":
            asm.Fsin();
            break;
          case "COS":
            asm.Fcos();
            break;
          case "TAN":
            asm.Fptan();
            asm.Fstp(St.St0);
            break;
          case "ATN":
            asm.Fld1();
            asm.Fpatan();
            break;
          case "LOG":
            asm.Fldln2();
            asm.Fxch();
            asm.Fyl2x();
            break;
          case "LOG2":
            asm.Fld1();
            asm.Fxch();
            asm.Fyl2x();
            break;
          case "LOG10":
            asm.Fldlg2();
            asm.Fxch();
            asm.Fyl2x();
            break;
          case "EXP":
            asm.Fldl2e();
            asm.Fmulp();
            asm.Call(asm.Lbl("rt_pow2"));
            break;
          case "EXP2":
            asm.Call(asm.Lbl("rt_pow2"));
            break;
          case "EXP10":
            asm.Fldl2t();
            asm.Fmulp();
            asm.Call(asm.Lbl("rt_pow2"));
            break;
        }
        break;

      case "FRE":
        if (args.Count > 0 && TryLiteralValue(args[0]) == -11) { // FRE(-11) = free EMS bytes
          asm.Call(this._rt.EmsFre);
          break;
        }
        if (args.Count > 0) {
          this.EmitExpression(args[0]);
          if (KindOf(model.TypeOf(args[0])) == ValueKind.Str)
            asm.Call(this._rt.StrFree);
          else if (KindOf(model.TypeOf(args[0])) == ValueKind.Float)
            asm.Fstp(St.St0);
        }
        asm.Mov(Reg.AX, 0x7FFF);           // advisory: plenty of room
        asm.Cwd();
        break;

      case "POS":
        if (args.Count > 0)
          this.EmitExpression(args[0]);
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_col")));
        asm.Inc(Reg.AX);
        break;

      case "CSRLIN":
        asm.Mov(Reg.AH, (Imm)3);
        asm.Xor(Reg.BH, Reg.BH);
        asm.Int(0x10);
        asm.Mov(Reg.AL, Reg.DH);
        asm.Xor(Reg.AH, Reg.AH);
        asm.Inc(Reg.AX);
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

  /// <summary>
  /// CVx source bytes in AX (handle): with the PB 3.5 start offset the relevant
  /// slice is cut first (CVL(x$, 3) reads 4 bytes starting at position 3).
  /// </summary>
  private void EmitCvSource(IReadOnlyList<Expression> args, int size) {
    var asm = this._asm;
    this.EmitExpression(args[0]);
    if (args.Count <= 1)
      return;
    asm.Push(Reg.AX);
    this.EmitInt16Argument(args[1]);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Mov(Reg.DX, size);
    asm.Call(this._rt.StrMid);
  }

  /// <summary>Evaluates an argument and coerces it to a 16-bit integer in AX.</summary>
  private void EmitInt16Argument(Expression e) {
    this.EmitExpression(e);
    this.Coerce(model.TypeOf(e), PbType.Integer, e);
  }

  private Label RtScratch => this._asm.Lbl("rt_scratch");

  /// <summary>Wraps the first <paramref name="length"/> bytes at rt_scratch into a new string (MKx$ family).</summary>
  private void EmitScratchString(int length) {
    var asm = this._asm;
    asm.Mov(Reg.SI, Imm.OffsetOf(this.RtScratch));
    asm.Mov(Reg.CX, length);
    asm.Mov(Reg.DX, Reg.DS);
    asm.Call(this._rt.StrMem);
  }
}
