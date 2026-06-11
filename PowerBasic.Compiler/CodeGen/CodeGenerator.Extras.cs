using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

/// <summary>PB 3.x surface added with the dialect wave: code pointers, ASC statement, STDIN/STDOUT, QUAD constants, UDT compare.</summary>
public sealed partial class CodeGenerator {

  #region QUAD literals

  private readonly List<(Label Slot, long Value)> _quadConstants = [];

  /// <summary>8-byte integer constant slot (loaded with FILD).</summary>
  private Label QuadConstOf(long value) {
    var slot = this._asm.DefineLabel($"q_{this._quadConstants.Count}");
    this._quadConstants.Add((slot, value));
    return slot;
  }

  #endregion

  #region GOTO / GOSUB DWORD

  /// <summary>
  /// GOTO/GOSUB DWORD ptr32 - far jump through a 32-bit code pointer. GOSUB
  /// pushes a NEAR continuation before the far jump so the target's plain
  /// RETURN (near RET) comes back cleanly - the program is one segment, and
  /// CODEPTR32 of labels/thunks always points into it.
  /// </summary>
  private void EmitGotoGosubPtr(Expression pointer, bool isGosub) {
    var asm = this._asm;
    this.EmitExpression(pointer);
    this.Coerce(model.TypeOf(pointer), PbType.Dword, pointer);
    asm.Mov(Mem.Word(this._scratch), Reg.AX);
    asm.Mov(Mem.Word(this._scratch, 2), Reg.DX);
    if (isGosub) {
      var continuation = asm.DefineLabel();
      asm.Mov(Reg.AX, Imm.OffsetOf(continuation));
      asm.Push(Reg.AX);
      asm.JmpFar(Mem.Dword(this._scratch));
      asm.MarkLabel(continuation);
    } else
      asm.JmpFar(Mem.Dword(this._scratch));
  }

  #endregion

  #region ASC statement

  /// <summary>ASC(s$ [, n]) = code - in-place byte poke (position defaults to 1).</summary>
  private void EmitAscAssign(AscAssignStmt asc) {
    var asm = this._asm;
    var targetType = model.TypeOf(asc.Target);

    this.EmitInt16Argument(asc.Value);
    asm.Push(Reg.AX);
    if (asc.Index != null)
      this.EmitInt16Argument(asc.Index);
    else
      asm.Mov(Reg.AX, 1);
    asm.Push(Reg.AX);

    if (this.EmitPlace(asc.Target) is not { } place) {
      asm.Pop(Reg.AX);
      asm.Pop(Reg.AX);
      return;
    }

    switch (targetType) {
      case StringType or FlexType:
        asm.Mov(Reg.AX, Adjust(place.Cell, 0, OperandSize.Word)); // raw handle
        asm.Pop(Reg.CX);                                          // position
        asm.Pop(Reg.DX);                                          // code in DL
        asm.Call(this._rt.AscSet);
        break;

      case FixedStringType or AsciizType:
        asm.Lea(Reg.BX, place.Cell);
        asm.Pop(Reg.CX);
        asm.Pop(Reg.DX);
        asm.Add(Reg.BX, Reg.CX);
        asm.Dec(Reg.BX);
        if (place.Far)
          asm.Mov(Mem.Byte(Reg.BX).Es(), Reg.DL);
        else
          asm.Mov(Mem.Byte(Reg.BX), Reg.DL);
        break;

      default:
        asm.Pop(Reg.AX);
        asm.Pop(Reg.AX);
        this.Unsupported(asc);
        break;
    }
  }

  #endregion

  #region STDOUT / STDIN

  /// <summary>STDOUT [expr] [;] - writes to DOS handle 1; ';' suppresses the newline.</summary>
  private void EmitStdOut(StdOutStmt stdOut) {
    var asm = this._asm;
    asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 1);
    if (stdOut.Value is { } value) {
      this.EmitExpression(value);
      this.EmitPrintValue(value);
    }
    if (!stdOut.NoNewline)
      asm.Call(this._rt.PrintNewLine);
  }

  /// <summary>STDIN n, s$ / STDIN LINE, s$ - reads from DOS handle 0 (PB file number 0).</summary>
  private void EmitStdIn(StdInStmt stdIn) {
    var asm = this._asm;
    if (stdIn.Line) {
      asm.Xor(Reg.AX, Reg.AX);
      asm.Call(this._rt.LInput);
    } else {
      this.EmitInt16Argument(stdIn.Count!);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Xor(Reg.AX, Reg.AX);
      asm.Call(this._rt.FGetStr);
    }

    asm.Push(Reg.AX);
    if (this.EmitPlace(stdIn.Target) is not { } place) {
      asm.Pop(Reg.AX);
      return;
    }
    asm.Pop(Reg.AX);
    this.EmitStorePlace(place, model.TypeOf(stdIn.Target), stdIn.Target);
  }

  #endregion

  #region TYPE/UNION whole-value comparison

  /// <summary>= / &lt;&gt; between TYPE/UNION values: a flat memcmp; result -1/0 in AX.</summary>
  private void EmitUdtCompare(BinaryExpr b, int byteCount) {
    var asm = this._asm;
    if (this.EmitPlace(b.Left) is not { } left) {
      this.Unsupported(b, "UDT compare operand");
      return;
    }
    asm.Lea(Reg.SI, left.Cell);
    asm.Mov(Reg.DX, left.Far ? Reg.ES : Reg.DS);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);

    if (this.EmitPlace(b.Right) is not { } right) {
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      this.Unsupported(b, "UDT compare operand");
      return;
    }
    asm.Lea(Reg.DI, right.Cell);
    if (!right.Far) {
      asm.Push(Reg.DS);
      asm.Pop(Reg.ES);
    }
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Push(Reg.DS);
    asm.Mov(Reg.DS, Reg.DX);
    if (this.OptimizePb36 && byteCount % 2 == 0 && byteCount >= 4) {
      // pb36 R3/C1: word-wide memcmp halves the iteration count; = / <> only
      // need equality per chunk, so chunk width is free to grow
      asm.Mov(Reg.CX, byteCount / 2);
      asm.Repe();
      asm.Cmpsw();
    } else {
      asm.Mov(Reg.CX, byteCount);
      asm.Repe();
      asm.Cmpsb();
    }
    asm.Pop(Reg.DS);

    var done = asm.DefineLabel();
    asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? -1 : 0);
    asm.Je(done);
    asm.Mov(Reg.AX, b.Op == BinaryOp.Equal ? 0 : -1);
    asm.MarkLabel(done);
  }

  #endregion
}
