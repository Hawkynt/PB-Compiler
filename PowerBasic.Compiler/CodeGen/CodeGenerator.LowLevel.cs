using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  #region SWAP

  /// <summary>SWAP a, b - byte-wise exchange of two equally sized lvalues via rt_swap (DX:SI &lt;-&gt; ES:DI, CX bytes).</summary>
  private void EmitSwap(SwapStmt sw) {
    var asm = this._asm;
    var type = model.TypeOf(sw.Left);
    if (this.EmitPlace(sw.Left) is not { } left) {
      this.Unsupported(sw);
      return;
    }
    asm.Lea(Reg.SI, left.Cell);
    asm.Mov(Reg.DX, left.Far ? Reg.ES : Reg.DS);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);

    if (this.EmitPlace(sw.Right) is not { } right) {
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      return;
    }
    asm.Lea(Reg.DI, right.Cell);
    if (!right.Far) {
      asm.Push(Reg.DS);
      asm.Pop(Reg.ES);
    }
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Mov(Reg.CX, Math.Max(type.Size, 1));
    asm.Call(this._rt.Swap);
  }

  #endregion

  #region SHIFT / ROTATE

  /// <summary>SHIFT/ROTATE LEFT|RIGHT lvalue, count - logical shifts and rotates on 1/2/4-byte cells.</summary>
  private void EmitShiftRotate(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments is not [{ } target, { } count]) {
      this.Unsupported(cmd);
      return;
    }
    var size = model.TypeOf(target).Size;
    if (size is not (1 or 2 or 4 or 8)) {
      this.Unsupported(cmd);
      return;
    }

    this.EmitInt16Argument(count);
    asm.Push(Reg.AX);
    if (this.EmitPlace(target) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }
    asm.Pop(Reg.CX);

    var rotate = cmd.Keyword.StartsWith("ROTATE", StringComparison.Ordinal);
    var left = cmd.Keyword.EndsWith("LEFT", StringComparison.Ordinal);
    if (size is 1 or 2) {
      var cell = Adjust(place.Cell, 0, size == 1 ? OperandSize.Byte : OperandSize.Word);
      switch (rotate, left) {
        case (false, true): asm.Shl(cell, Reg.CL); break;
        case (false, false): asm.Shr(cell, Reg.CL); break;
        case (true, true): asm.Rol(cell, Reg.CL); break;
        case (true, false): asm.Ror(cell, Reg.CL); break;
      }
      return;
    }

    // pb36 C1 ($CPU 80386): a 32-bit cell with a compile-time-constant count in 1..31
    // shifts/rotates the dword in place with a single 386 instruction, replacing the
    // CX-times per-word loop. The count must be a constant in range because the 386
    // masks it to 5 bits (a count >= 32 would differ from the unmasked loop). SHIFT
    // RIGHT here is logical (the loop uses SHR), matching genuine PBC.
    if (size == 4 && this.Optimize && this.Cpu386
        && this.Pb36Folder.TryFold(count) is { Integer: { } cnt } && cnt is >= 1 and <= 31) {
      var dword = Adjust(place.Cell, 0, OperandSize.Dword);
      switch (rotate, left) {
        case (false, true): asm.Shl(dword, (int)cnt); break;
        case (false, false): asm.Shr(dword, (int)cnt); break;
        case (true, true): asm.Rol(dword, (int)cnt); break;
        case (true, false): asm.Ror(dword, (int)cnt); break;
      }
      return;
    }

    // 32/64-bit: one-bit steps through the word chain, CX times
    var words = size / 2;
    var lo = Adjust(place.Cell, 0, OperandSize.Word);
    var hi = Adjust(place.Cell, size - 2, OperandSize.Word);
    var loop = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Jcxz(done);
    asm.MarkLabel(loop);
    switch (rotate, left) {
      case (false, true):
        asm.Shl(lo, 1);
        for (var w = 1; w < words; ++w)
          asm.Rcl(Adjust(place.Cell, w * 2, OperandSize.Word), 1);
        break;
      case (false, false):
        asm.Shr(hi, 1);
        for (var w = words - 2; w >= 0; --w)
          asm.Rcr(Adjust(place.Cell, w * 2, OperandSize.Word), 1);
        break;
      case (true, true):
        asm.Shl(lo, 1);
        for (var w = 1; w < words; ++w)
          asm.Rcl(Adjust(place.Cell, w * 2, OperandSize.Word), 1);
        asm.Adc(lo, (Imm)0);          // carry = old top bit -> bit 0
        break;
      case (true, false): {
        var skip = asm.DefineLabel();
        asm.Shr(hi, 1);
        for (var w = words - 2; w >= 0; --w)
          asm.Rcr(Adjust(place.Cell, w * 2, OperandSize.Word), 1);
        asm.Jnc(skip);                // carry = old bit 0 -> top bit
        asm.Or(hi, 0x8000);
        asm.MarkLabel(skip);
        break;
      }
    }
    asm.Loop(loop);
    asm.MarkLabel(done);
  }

  #endregion

  #region DEF SEG / PEEK / POKE / ports

  private void EmitDefSeg(DefSegStmt seg) {
    var asm = this._asm;
    if (seg.Segment is { } segment)
      this.EmitInt16Argument(segment);
    else
      asm.Mov(Reg.AX, Reg.DS);
    asm.Mov(Mem.Word(asm.Lbl("rt_defseg")), Reg.AX);
  }

  /// <summary>PEEK/PEEKI/PEEKL - read from rt_defseg:offset.</summary>
  private void EmitPeek(IReadOnlyList<Expression> args, int bytes) {
    var asm = this._asm;
    this.EmitInt16Argument(args[0]);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_defseg")));
    switch (bytes) {
      case 1:
        asm.Mov(Reg.AL, Mem.Byte(Reg.BX).Es());
        asm.Xor(Reg.AH, Reg.AH);
        break;
      case 2:
        asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());
        break;
      default:
        asm.Mov(Reg.AX, Mem.Word(Reg.BX).Es());
        asm.Mov(Reg.DX, Mem.Word(Reg.BX, 2).Es());
        break;
    }
  }

  private void EmitPoke(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments is not [{ } address, { } value]) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(address);
    asm.Push(Reg.AX);
    this.EmitInt16Argument(value);
    asm.Pop(Reg.BX);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_defseg")));
    asm.Mov(Mem.Byte(Reg.BX).Es(), Reg.AL);
  }

  private void EmitOut(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments is not [{ } port, { } value]) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(port);
    asm.Push(Reg.AX);
    this.EmitInt16Argument(value);
    asm.Pop(Reg.DX);
    asm.Out(Reg.DX, Reg.AL);
  }

  /// <summary>WAIT port, and [, xor] - poll until (INP(port) XOR x) AND a is nonzero.</summary>
  private void EmitWait(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments.Count is not (2 or 3) || cmd.Arguments[0] is not { } port || cmd.Arguments[1] is not { } mask) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(port);
    asm.Push(Reg.AX);
    this.EmitInt16Argument(mask);
    asm.Push(Reg.AX);
    if (cmd.Arguments.Count > 2 && cmd.Arguments[2] is { } flip)
      this.EmitInt16Argument(flip);
    else
      asm.Xor(Reg.AX, Reg.AX);
    asm.Mov(Reg.CH, Reg.AL);           // CH = xor value
    asm.Pop(Reg.AX);
    asm.Mov(Reg.CL, Reg.AL);           // CL = and mask
    asm.Pop(Reg.DX);
    var poll = asm.DefineLabel();
    asm.MarkLabel(poll);
    asm.In(Reg.AL, Reg.DX);
    asm.Xor(Reg.AL, Reg.CH);
    asm.Test(Reg.AL, Reg.CL);
    asm.Jz(poll);
  }

  #endregion

  #region VARPTR family

  private void EmitVarPtrSeg(Expression call, IReadOnlyList<Expression> args, bool wantSegment) {
    var asm = this._asm;
    if (this.EmitPlace(args[0]) is not { } place) {
      this.Unsupported(call, "VARPTR/VARSEG argument");
      return;
    }
    if (wantSegment)
      asm.Mov(Reg.AX, place.Far ? Reg.ES : Reg.DS);
    else
      asm.Lea(Reg.AX, place.Cell);
  }

  private void EmitStrPtrSeg(Expression call, IReadOnlyList<Expression> args, bool wantSegment) {
    var asm = this._asm;
    if (model.TypeOf(args[0]) is not (StringType or FlexType) || this.EmitPlace(args[0]) is not { } place) {
      this.Unsupported(call, "STRPTR/STRSEG argument");
      return;
    }
    if (wantSegment) {
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_strseg")));
      return;
    }
    asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word));
    asm.Call(this._rt.StrPtr);
  }

  #endregion

  #region CODEPTR family & CALL DWORD

  private readonly Dictionary<ProcedureSymbol, Label> _farThunks = new(ReferenceEqualityComparer.Instance);

  private Label ThunkOf(ProcedureSymbol proc) {
    if (!this._farThunks.TryGetValue(proc, out var label))
      this._farThunks[proc] = label = this._asm.DefineLabel($"thk_{proc.Name}");
    return label;
  }

  /// <summary>
  /// Far entry thunks for CODEPTR32-referenced near procedures. The program is a
  /// single segment, so the far caller's CS equals ours: the thunk rewrites the
  /// far return address into a near one and tail-jumps into the procedure, whose
  /// normal RET n then returns straight to the far call site.
  /// </summary>
  private void EmitFarThunks() {
    var asm = this._asm;
    foreach (var (proc, label) in this._farThunks) {
      asm.MarkLabel(label);
      asm.Pop(Reg.AX);                 // far return offset
      asm.Pop(Reg.DX);                 // far return segment == our CS - drop it
      asm.Push(Reg.AX);
      asm.Jmp(this.ProcLabelOf(proc));
    }
  }

  private void EmitCodePtr(Expression call, IReadOnlyList<Expression> args, string name) {
    var asm = this._asm;
    if (args is not [NameExpr procRef]) {
      this.Unsupported(call, $"{name} argument");
      return;
    }

    // label reference (GOTO/GOSUB DWORD targets): same segment, no thunk needed
    if (model.LabelBindings.TryGetValue(procRef, out var labelName)) {
      if (name == "CODESEG") {
        asm.Mov(Reg.AX, Reg.CS);
        return;
      }
      asm.Mov(Reg.AX, Imm.OffsetOf(this.UserLabel(labelName)));
      if (name == "CODEPTR32")
        asm.Mov(Reg.DX, Reg.CS);
      return;
    }

    if (!model.CallBindings.TryGetValue(procRef, out var proc)) {
      this.Unsupported(call, $"{name} argument");
      return;
    }
    switch (name) {
      case "CODEPTR":
        asm.Mov(Reg.AX, Imm.OffsetOf(this.ProcLabelOf(proc)));
        break;
      case "CODESEG":
        asm.Mov(Reg.AX, Reg.CS);
        break;
      default: // CODEPTR32 -> DX:AX = far thunk
        asm.Mov(Reg.AX, Imm.OffsetOf(this.ThunkOf(proc)));
        asm.Mov(Reg.DX, Reg.CS);
        break;
    }
  }

  /// <summary>
  /// CALL DWORD ptr BDECL (args) - far call through a 32-bit pointer. Arguments
  /// are pushed BYREF (near pointers), exactly like a normal SUB call - the
  /// targets are CODEPTR32 thunks of normal SUBs which clean their stack.
  /// </summary>
  private void EmitCallPtr(CallPtrStmt cp) {
    var asm = this._asm;
    var tempBytesUsed = 0;
    var stringTemps = new List<Mem>();

    foreach (var arg in cp.Arguments) {
      var argType = model.TypeOf(arg);
      if (this.IsNearLValue(arg) && this.EmitPlace(arg) is { } place) {
        asm.Lea(Reg.BX, place.Cell);
        asm.Push(Reg.BX);
        continue;
      }
      var slotBytes = Math.Max(2, (argType.Size + 1) & ~1);
      var temp = this.AllocTemp(slotBytes);
      tempBytesUsed += slotBytes;
      this.EmitExpression(arg);
      this.EmitStoreTempArgument(temp, argType, arg, stringTemps);
      asm.Lea(Reg.BX, temp);
      asm.Push(Reg.BX);
    }

    var pointer = this.AllocTemp(4);
    tempBytesUsed += 4;
    this.EmitExpression(cp.Pointer);
    this.Coerce(model.TypeOf(cp.Pointer), PbType.Dword, cp.Pointer);
    asm.Mov(pointer.WithSize(OperandSize.Word), Reg.AX);
    asm.Mov(Adjust(pointer, 2, OperandSize.Word), Reg.DX);
    asm.CallFar(pointer.WithSize(OperandSize.Dword));

    foreach (var cell in stringTemps) {
      asm.Mov(Reg.AX, cell.WithSize(OperandSize.Word));
      asm.Call(this._rt.StrFree);
    }
    this.ReleaseTemp(tempBytesUsed);
  }

  #endregion

  #region REG / CALL INTERRUPT

  private void EmitRegStatement(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments is not [{ } index, { } value]) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(index);
    asm.Push(Reg.AX);
    this.EmitInt16Argument(value);
    asm.Pop(Reg.BX);
    asm.Shl(Reg.BX, 1);
    asm.Mov(Mem.Word(Reg.BX, asm.Lbl("rt_regs")), Reg.AX);
  }

  private void EmitRegFunction(IReadOnlyList<Expression> args) {
    var asm = this._asm;
    this.EmitInt16Argument(args[0]);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 1);
    asm.Mov(Reg.AX, Mem.Word(Reg.BX, asm.Lbl("rt_regs")));
  }

  private void EmitInterrupt(CommandStmt cmd) {
    var asm = this._asm;
    if (cmd.Arguments is not [{ } vector]) {
      this.Unsupported(cmd);
      return;
    }
    this.EmitInt16Argument(vector);
    asm.Call(this._rt.Interrupt);
  }

  #endregion

  #region ON ERROR / RESUME / ERROR

  /// <summary>True when a statement list (recursively) contains error-handling statements - those scopes maintain RESUME bookkeeping.</summary>
  private static bool ContainsErrorHandling(IEnumerable<Statement> statements) {
    foreach (var statement in statements) {
      switch (statement) {
        case OnErrorStmt or ResumeStmt:
          return true;
        case SubDecl or FunctionDecl or DefFnDecl:
          continue; // nested procs have their own scope
      }
      if (ChildStatementBlocks(statement).Any(ContainsErrorHandling))
        return true;
    }
    return false;
  }

  private static IEnumerable<IReadOnlyList<Statement>> ChildStatementBlocks(Statement s) {
    switch (s) {
      case IfStmt i:
        yield return i.Then;
        foreach (var (_, body) in i.ElseIfs)
          yield return body;
        if (i.Else != null)
          yield return i.Else;
        break;
      case SelectStmt sel:
        foreach (var arm in sel.Arms)
          yield return arm.Body;
        break;
      case ForStmt f:
        yield return f.Body;
        break;
      case DoLoopStmt d:
        yield return d.Body;
        break;
    }
  }

  private void EmitOnError(OnErrorStmt oe) {
    var asm = this._asm;
    if (oe.ResumeNext) {
      this.Unsupported(oe.Position, "ON ERROR RESUME NEXT");
      return;
    }
    if (oe.Target is null or "0") {
      asm.Mov(Mem.Word(asm.Lbl("rt_onerr")), (Imm)0);
      return;
    }
    asm.Mov(Mem.Word(asm.Lbl("rt_onerr")), Imm.OffsetOf(this.UserLabel(oe.Target)));
    asm.Mov(Mem.Word(asm.Lbl("rt_onerr_bp")), Reg.BP);
    asm.Mov(Mem.Word(asm.Lbl("rt_onerr_sp")), Reg.SP);
    asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
  }

  private void EmitResume(ResumeStmt rs) {
    var asm = this._asm;
    switch (rs.Kind) {
      case ResumeKind.Label when rs.Target != null:
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        asm.Jmp(this.UserLabel(rs.Target));
        break;
      case ResumeKind.SameStatement:
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        asm.Jmp(Mem.Word(asm.Lbl("rt_eresume")));
        break;
      default:
        asm.Mov(Mem.Word(asm.Lbl("rt_err")), (Imm)0);
        asm.Jmp(Mem.Word(asm.Lbl("rt_eresumenext")));
        break;
    }
  }

  private void EmitError(ErrorStmt err) {
    this.EmitInt16Argument(err.Code);
    this._asm.Call(this._rt.Raise);
  }

  #endregion
}
