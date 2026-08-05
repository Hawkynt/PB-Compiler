using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  /// <summary>
  /// O0266: is this a string-producing intrinsic whose result is provably the empty string - a
  /// zero length argument - with every other operand side-effect-free? The length is zero either as
  /// a literal or by the value-fact lattice (a provably-zero variable); the source string and any
  /// index are required side-effect-free so that folding away the call skips nothing observable.
  /// MID$(s$, i, 0) is "" for ANY start i (out-of-range included), matching rt_strmid.
  /// </summary>
  private bool IsZeroLengthStringIntrinsic(string name, IReadOnlyList<Expression> args) => name switch {
    "LEFT$" or "RIGHT$" => args.Count == 2 && this.IsProvablyZero(args[1]) && IsEffectFreeArg(args[0]),
    "MID$" => args.Count == 3 && this.IsProvablyZero(args[2]) && IsEffectFreeArg(args[0]) && IsEffectFreeArg(args[1]),
    "SPACE$" => args.Count == 1 && this.IsProvablyZero(args[0]),
    "STRING$" => args.Count == 2 && this.IsProvablyZero(args[0]) && IsEffectFreeArg(args[1]),
    _ => false,
  };

  /// <summary>The value-fact range pins the expression to exactly zero (a literal or a proven-0 variable).</summary>
  private bool IsProvablyZero(Expression e) => this.FactsOf(e).Range is { Lo: 0, Hi: 0 };

  /// <summary>A read that cannot trap or observe: a literal, a named constant, or a plain variable.</summary>
  private bool IsEffectFreeArg(Expression e) => e switch {
    IntegerLiteralExpr or FloatLiteralExpr or StringLiteralExpr or NamedConstantExpr => true,
    NameExpr n => model.VariableBindings.ContainsKey(n),
    _ => false,
  };

  /// <summary>
  /// O0297: a one-character substring of a string - <c>MID$(s$, i, 1)</c> (index <c>i</c>),
  /// <c>LEFT$(s$, 1)</c> (index 1, a null <paramref name="idx"/>), or <c>RIGHT$(s$, 1)</c> (the last
  /// character, <paramref name="isLast"/> set). Routes the read (ASC) and the compare through the
  /// direct byte paths <c>rt_charat</c> / <c>rt_lastchar</c>.
  /// </summary>
  private bool SingleCharSource(Expression e, out Expression str, out Expression? idx, out bool isLast) {
    str = null!; idx = null; isLast = false;
    if (e is not CallOrIndexExpr c || !model.IntrinsicBindings.TryGetValue(c, out var info))
      return false;
    if (info.Name.Equals("MID$", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count == 3
        && this.OptFolder.TryFold(c.Arguments[2]) is { Integer: 1 }) {
      str = c.Arguments[0]; idx = c.Arguments[1];
      return model.TypeOf(str) is StringType or FlexType;
    }
    if (info.Name.Equals("LEFT$", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count == 2
        && this.OptFolder.TryFold(c.Arguments[1]) is { Integer: 1 }) {
      str = c.Arguments[0];                          // LEFT$(s$, 1) is the character at index 1
      return model.TypeOf(str) is StringType or FlexType;
    }
    if (info.Name.Equals("RIGHT$", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count == 2
        && this.OptFolder.TryFold(c.Arguments[1]) is { Integer: 1 }) {
      str = c.Arguments[0]; isLast = true;           // RIGHT$(s$, 1) is the last character
      return model.TypeOf(str) is StringType or FlexType;
    }
    return false;
  }

  /// <summary>Emits the byte of the one-character source classified by <see cref="SingleCharSource"/> into AX, consuming the handle.</summary>
  private void EmitSingleCharByte(Expression str, Expression? idx, bool isLast) {
    var asm = this._asm;
    this.EmitExpression(str);                        // owned string handle in AX
    if (isLast) {
      asm.Call(this._rt.LastChar);
      return;
    }
    asm.Push(Reg.AX);
    if (idx != null)
      this.EmitInt16Argument(idx);
    else
      asm.Mov(Reg.AX, 1);                            // LEFT$(s$, 1): the index is 1
    asm.Mov(Reg.CX, Reg.AX);
    asm.Pop(Reg.AX);
    asm.Call(this._rt.CharAt);
  }

  /// <summary>
  /// O0108/O0248: an all-INTEGER MIN/MAX (or MIN%/MAX%) folds with signed integer compares instead of the x87
  /// round-trip. The accumulator lives in AX; each further argument is loaded into BX and a `cmp`/conditional
  /// keeps the larger (MAX) or smaller (MIN). On a tie the accumulator is kept, matching the FPU fold (Ja/Jb are
  /// strict). Preserves the accumulator across each argument's evaluation via the stack, since evaluating an
  /// argument may itself call a FUNCTION and clobber AX/BX.
  /// </summary>
  private void EmitIntegerMinMaxFold(IReadOnlyList<Expression> args, bool wantMax) {
    var asm = this._asm;
    this.EmitInt16Argument(args[0]);                   // accumulator in AX
    for (var i = 1; i < args.Count; ++i) {
      asm.Push(Reg.AX);
      this.EmitInt16Argument(args[i]);                 // candidate in AX
      asm.Mov(Reg.BX, Reg.AX);                         // candidate -> BX
      asm.Pop(Reg.AX);                                 // accumulator -> AX
      var keep = asm.DefineLabel();
      asm.Cmp(Reg.AX, Reg.BX);
      if (wantMax)
        asm.Jge(keep);                                 // acc >= cand: keep acc (tie keeps acc)
      else
        asm.Jle(keep);                                 // acc <= cand: keep acc (tie keeps acc)
      asm.Mov(Reg.AX, Reg.BX);                          // candidate wins
      asm.MarkLabel(keep);
    }
  }

  /// <summary>
  /// O0108/O0248: the LONG counterpart of <see cref="EmitIntegerMinMaxFold"/>. The accumulator lives in DX:AX,
  /// each candidate is brought into CX:BX, and a signed 32-bit compare (high word signed, low word unsigned on
  /// the tie) keeps the larger (MAX) or smaller (MIN). A numeric tie keeps the accumulator, matching the strict
  /// FPU fold. The 4-byte accumulator is preserved across each argument's evaluation on the stack.
  /// </summary>
  private void EmitLongMinMaxFold(IReadOnlyList<Expression> args, bool wantMax) {
    var asm = this._asm;
    this.EmitExpression(args[0]);
    this.Coerce(model.TypeOf(args[0]), PbType.Long, args[0]);     // accumulator in DX:AX
    for (var i = 1; i < args.Count; ++i) {
      asm.Push(Reg.DX);
      asm.Push(Reg.AX);
      this.EmitExpression(args[i]);
      this.Coerce(model.TypeOf(args[i]), PbType.Long, args[i]);   // candidate in DX:AX
      asm.Mov(Reg.CX, Reg.DX);                                     // candidate -> CX:BX
      asm.Mov(Reg.BX, Reg.AX);
      asm.Pop(Reg.AX);                                             // accumulator -> DX:AX
      asm.Pop(Reg.DX);
      var keep = asm.DefineLabel();
      var take = asm.DefineLabel();
      asm.Cmp(Reg.DX, Reg.CX);                                     // signed compare of the high words
      if (wantMax) {
        asm.Jg(keep);                                             // acc_hi > cand_hi: keep acc
        asm.Jl(take);                                             // acc_hi < cand_hi: take cand
        asm.Cmp(Reg.AX, Reg.BX);                                   // high words equal: low words unsigned
        asm.Jae(keep);                                            // acc_lo >= cand_lo: keep acc (tie keeps acc)
      } else {
        asm.Jl(keep);
        asm.Jg(take);
        asm.Cmp(Reg.AX, Reg.BX);
        asm.Jbe(keep);                                           // acc_lo <= cand_lo: keep acc (tie keeps acc)
      }
      asm.MarkLabel(take);
      asm.Mov(Reg.AX, Reg.BX);                                    // candidate wins
      asm.Mov(Reg.DX, Reg.CX);
      asm.MarkLabel(keep);
    }
  }

  /// <summary>O0302: the byte of a single-character constant needle - a 1-char string literal or CHR$(const). Any byte, 0 included.</summary>
  private int? SingleCharNeedleByte(Expression e) {
    if (e is StringLiteralExpr { Value: { Length: 1 } text } && text[0] <= (char)255)
      return (byte)text[0];
    if (e is CallOrIndexExpr c && model.IntrinsicBindings.TryGetValue(c, out var info)
        && info.Name.Equals("CHR$", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count == 1
        && this.OptFolder.TryFold(c.Arguments[0]) is { Integer: { } n })
      return (int)(n & 0xFF);
    return null;
  }

  private void EmitIntrinsic(Expression call, IReadOnlyList<Expression> args, IntrinsicInfo intrinsic) {
    var asm = this._asm;

    // O0266: a string intrinsic whose length is provably zero yields the empty string (handle 0),
    // with no runtime call - the same xor ax,ax an "" literal emits, so it composes with every
    // empty-handle path (assignment, concat, O0181 comparison). Folded only when the source/index
    // operands are side-effect-free, so nothing observable is skipped by not calling the intrinsic.
    if (this.Optimize && this.IsZeroLengthStringIntrinsic(intrinsic.Name, args)) {
      asm.Xor(Reg.AX, Reg.AX);
      return;
    }

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
        // O0302: INSTR([k,] s$, "c") with a single-character constant needle scans for the byte
        // (rt_scanchar / REPNE SCASB) instead of the general substring probe, and never allocates the
        // one-byte needle. The optional 1-based start is passed through. Any byte value is valid here
        // (unlike the char compare); the start-position form is the tokenizing loop's hot path.
        if (this.Optimize && intrinsic.Name == "INSTR" && needle is not AnyMatchExpr
            && this.SingleCharNeedleByte(needle) is { } scanByte
            && model.TypeOf(args[hasStart ? 1 : 0]) is StringType or FlexType) {
          if (hasStart) {
            this.EmitInt16Argument(args[0]);       // start -> AX
            asm.Push(Reg.AX);
          }
          this.EmitExpression(args[hasStart ? 1 : 0]);   // haystack handle -> AX
          asm.Mov(Reg.DX, (Imm)scanByte);
          if (hasStart)
            asm.Pop(Reg.CX);                       // CX = start
          else
            asm.Mov(Reg.CX, 1);
          asm.Call(this._rt.ScanChar);
          asm.Cwd();
          break;
        }
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

      case "ASC" or "ASCII": {
        // O0297: ASC(s$, i), ASC(MID$(s$, i, 1)) and ASC(LEFT$(s$, 1)) read the byte directly
        // (rt_charat) instead of allocating a one-character substring - same clamp-to-1 / 0-past-the-end
        // result, one fewer heap allocation per character in a scan loop. Gated on --optimize.
        Expression? strExpr = null, idxExpr = null;
        var haveSource = false;
        var ascLast = false;
        if (this.Optimize) {
          if (args.Count == 2) {
            strExpr = args[0]; idxExpr = args[1]; haveSource = model.TypeOf(strExpr) is StringType or FlexType;
          } else if (args.Count == 1 && this.SingleCharSource(args[0], out var src, out var idx, out ascLast)) {
            strExpr = src; idxExpr = idx; haveSource = true;   // MID$ / LEFT$ (index 1) / RIGHT$ (last)
          }
        }
        if (haveSource) {
          this.EmitSingleCharByte(strExpr!, idxExpr, ascLast);
          break;
        }
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
      }

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
          // STR$ keeps the argument's display precision: SINGLE renders 7
          // significant digits (STR$(2/3) = ".6666667" on genuine PBC 3.50)
          case ValueKind.Float when model.TypeOf(args[0]) is ScalarType { ByteSize: 4 }: asm.Call(this._rt.StrF32); break;
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
        if (model.Dialect.IsPbAtLeast(Dialect.Pb31))
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

      // R2 direct-video pixel read (mode 13h): POINT(x, y) -> LONG color
      case "POINT":
        this.EmitInt16Argument(args[0]);
        asm.Push(Reg.AX);
        this.EmitInt16Argument(args[1]);
        asm.Mov(Reg.BX, Reg.AX);
        asm.Pop(Reg.AX);
        asm.Call(this._rt.Point);
        break;

      case "ABS":
        this.EmitExpression(args[0]);
        switch (KindOf(model.TypeOf(args[0]))) {
          case ValueKind.Int16: {
            // O0249 branchless abs: y = (x XOR mask) - mask where mask = CWD (all-ones iff negative),
            // three instructions and no branch - bit-identical to the test/JNS/NEG form, MININT
            // wrap included (ABS(-32768) stays -32768, matching the branch form). Optimize-gated so
            // the faithful path stays byte-identical; and never under $ERROR OVERFLOW, where the
            // negation's trap must survive on the branching path.
            if (this.Optimize && !this.CheckOverflow) {
              asm.Cwd();
              asm.Xor(Reg.AX, Reg.DX);
              asm.Sub(Reg.AX, Reg.DX);
              break;
            }
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
        // O0108/O0249: branchless integer sign. cwd puts the sign mask (0 / -1) in DX; neg sets CF iff x != 0;
        // adc dx,dx forms 2*mask + CF = -1 (x<0) / 0 (x=0) / +1 (x>0) - no branch and no x87 round-trip, exact
        // for every int16 including MININT (cwd gives -1, neg wraps to itself with CF set, adc yields -1).
        if (this.Optimize && KindOf(type) == ValueKind.Int16) {
          asm.Cwd();
          asm.Neg(Reg.AX);
          asm.Adc(Reg.DX, Reg.DX);
          asm.Mov(Reg.AX, Reg.DX);
          break;
        }
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
        // O0108/O0248: when every argument and the result are INTEGER, fold with an integer compare instead of
        // the x87 round-trip (coerce-to-double, FCOM, coerce-back). The signed compare reproduces the FPU
        // fold's result exactly over the int16 range, ties included (both keep the earlier accumulator).
        if (this.Optimize && KindOf(model.TypeOf(call)) == ValueKind.Int16
            && args.All(a => KindOf(model.TypeOf(a)) == ValueKind.Int16)) {
          this.EmitIntegerMinMaxFold(args, wantMax);
          break;
        }
        // O0108/O0248: the same fold for all-LONG arguments, over DX:AX with a 32-bit signed compare.
        if (this.Optimize && KindOf(model.TypeOf(call)) == ValueKind.Int32
            && args.All(a => KindOf(model.TypeOf(a)) == ValueKind.Int32)) {
          this.EmitLongMinMaxFold(args, wantMax);
          break;
        }
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
        this.RoundFpuToIntrinsicType(call);
        break;

      case "INT" or "FIX" or "CEIL":
        this.EmitExpression(args[0]);
        // an integer is already whole, whichever way the rounding would have gone
        if (KindOf(model.TypeOf(args[0])) == ValueKind.Float)
          asm.Call(intrinsic.Name switch { "INT" => this._rt.Floor, "FIX" => this._rt.Trunc, _ => this._rt.Ceil });
        break;

      // FRAC is what FIX leaves behind: x - FIX(x), so it keeps the sign of x and an integer has
      // none of it. Computed on the stack rather than through a helper - duplicate, truncate the
      // copy, subtract it from the original.
      case "FRAC":
        this.EmitExpression(args[0]);
        if (KindOf(model.TypeOf(args[0])) != ValueKind.Float) {
          asm.Xor(Reg.AX, Reg.AX);
          asm.Xor(Reg.DX, Reg.DX);
          break;
        }
        asm.Fld(St.St0);
        asm.Call(this._rt.Trunc);
        asm.Fsubp(St.St1);
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
        this.RoundFpuToIntrinsicType(call);
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

      // LPOS is POS for the printer. The two columns are counted apart - a comma zone on one must
      // not move the other, which is why BASIC has both functions - so this reads the cell LPRINT
      // keeps rather than the screen's.
      case "LPOS":
        if (args.Count > 0)
          this.EmitExpression(args[0]);
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_lcol")));
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
    // PB 3.6 from-end index arr(^n): emit its bound rewrite UBOUND(arr) - n + 1
    if (model.RewrittenIndex.TryGetValue(e, out var rewritten))
      e = rewritten;
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

  /// <summary>
  /// Rounds the FPU result of a math intrinsic to its bound type. QB returns
  /// argument-typed results (SQR(2) is SINGLE, LOG(e#) the DOUBLE-rounded 1),
  /// so the 80-bit FPU value must lose precision exactly like the original.
  /// </summary>
  private void RoundFpuToIntrinsicType(Expression call) {
    // The Microsoft family has no 80-bit extended type: a math intrinsic's result is narrowed to its
    // declared precision (FSTP/FLD round-trip through memory), so PRINT sees the 64-/32-bit value
    // (LOG(e#) prints 1, not the 1-ULP-off extended .9999999999999999). EffectiveDialect honours
    // $COMPAT so a transpiled-to-pb35 program narrows identically to its source dialect.
    if (model.EffectiveDialect.Family() != DialectFamily.Microsoft)
      return;
    var asm = this._asm;
    if (model.TypeOf(call) is ScalarType { Kind: ScalarKind.Single }) {
      asm.Fstp(Mem.Dword(this.RtScratch));
      asm.Fld(Mem.Dword(this.RtScratch));
    } else {
      asm.Fstp(Mem.Qword(this.RtScratch));
      asm.Fld(Mem.Qword(this.RtScratch));
    }
  }
}
