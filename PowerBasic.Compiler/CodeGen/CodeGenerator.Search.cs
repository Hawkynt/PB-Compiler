using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Semantics;
using PowerBasic.Compiler.Syntax.Ast;

namespace PowerBasic.Compiler.CodeGen;

public sealed partial class CodeGenerator {

  private const int _INSTR_HORSPOOL_THRESHOLD = 5;

  /// <summary>
  /// O0302: emits a specialized INSTR for a compile-time byte string of length at least two.
  /// Short needles use REPNE SCASB to locate candidate first bytes followed by a REPE CMPSB verify;
  /// longer needles use Boyer-Moore-Horspool with a compile-time generated 256-byte bad-character
  /// table. The needle is read directly from the literal pool, so neither path allocates it.
  /// </summary>
  private bool TryEmitConstantInstr(IReadOnlyList<Expression> args) {
    var hasStart = args.Count > 2;
    var haystack = args[hasStart ? 1 : 0];
    var needleExpr = args[hasStart ? 2 : 1];
    if (model.TypeOf(haystack) is not (StringType or FlexType)
        || this.OptFolder.TryFold(needleExpr) is not { Text: { Length: >= 2 } needle }
        || !IsByteString(needle))
      return false;

    if (hasStart) {
      this.EmitInt16Argument(args[0]);
      this._asm.Push(Reg.AX);
    }
    this.EmitExpression(haystack);
    if (hasStart)
      this._asm.Pop(Reg.CX);
    else
      this._asm.Mov(Reg.CX, 1);

    if (needle.Length < _INSTR_HORSPOOL_THRESHOLD)
      this.EmitShortConstantInstr(needle);
    else
      this.EmitHorspoolConstantInstr(needle);
    return true;
  }

  private static bool IsByteString(string text) {
    foreach (var c in text)
      if (c > byte.MaxValue)
        return false;
    return true;
  }

  /// <summary>
  /// AX = owned haystack handle, CX = 1-based start. Returns AX = first 1-based match or zero and
  /// consumes the haystack. REPNE SCASB skips directly between candidate first bytes; REPE CMPSB
  /// verifies only the remaining bytes, so two-to-four-byte needles avoid a general probe at every
  /// text position while keeping the processor's dedicated string instructions in the hot loop.
  /// </summary>
  private void EmitShortConstantInstr(string needle) {
    var asm = this._asm;
    var needleLabel = this.LiteralOf(needle);
    var startOk = asm.DefineLabel();
    var scan = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Push(Reg.AX);                                // owned haystack handle
    asm.Cmp(Reg.CX, 1);
    asm.Jge(startOk);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(startOk);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(none);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));       // haystack base
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));    // haystack length
    asm.Dec(Reg.CX);                                // zero-based start
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jae(none);                                   // start past the end
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);                         // first candidate address
    asm.Sub(Reg.DX, Reg.CX);                         // bytes remaining from start
    asm.Cmp(Reg.DX, needle.Length);
    asm.Jb(none);
    asm.Sub(Reg.DX, needle.Length - 1);              // candidate-start count
    asm.Mov(Reg.CX, Reg.DX);
    asm.Mov(Reg.BX, Reg.SI);                         // keep haystack base for result
    asm.Cld();

    asm.MarkLabel(scan);
    asm.Mov(Reg.AL, (Imm)(byte)needle[0]);
    asm.Repne();
    asm.Scasb();                                     // ES:DI -> one past candidate, CX -> starts left
    asm.Jne(none);
    asm.Push(Reg.CX);
    asm.Push(Reg.DI);                                // resume at candidate + 1 after failed verify
    asm.Mov(Reg.SI, Imm.OffsetOf(needleLabel));
    asm.Inc(Reg.SI);                                 // first byte already matched
    asm.Mov(Reg.CX, needle.Length - 1);
    asm.Repe();
    asm.Cmpsb();                                     // DS:needle[1..] vs ES:haystack[candidate+1..]
    asm.Pop(Reg.DI);
    asm.Pop(Reg.CX);
    asm.Je(found);
    asm.Jcxz(none);
    asm.Jmp(scan);

    asm.MarkLabel(found);
    asm.Mov(Reg.AX, Reg.DI);
    asm.Sub(Reg.AX, Reg.BX);                         // candidate+1 - base = 1-based position
    asm.Jmp(output);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Pop(Reg.BX);                                 // owned haystack handle
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Call(this._rt.StrFree);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
  }

  /// <summary>
  /// AX = owned haystack handle, CX = 1-based start. Returns AX = first 1-based match or zero and
  /// consumes the haystack. The 256-byte Horspool table is emitted as read-only inline data and
  /// skipped by an unconditional branch. Shifts larger than 255 saturate to 255; using a smaller
  /// shift than the mathematical bad-character distance is conservative, so very long BASIC
  /// strings stay correct without doubling the table to 512 bytes.
  /// </summary>
  private void EmitHorspoolConstantInstr(string needle) {
    var asm = this._asm;
    var needleLabel = this.LiteralOf(needle);
    var skipLabel = asm.DefineLabel();
    var searchCode = asm.DefineLabel();
    var startOk = asm.DefineLabel();
    var loop = asm.DefineLabel();
    var shift = asm.DefineLabel();
    var none = asm.DefineLabel();
    var found = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Jmp(searchCode);
    asm.MarkLabel(skipLabel);
    asm.Db(BuildHorspoolSkipTable(needle));
    asm.MarkLabel(searchCode);

    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Push(Reg.AX);                                // owned haystack handle
    asm.Cmp(Reg.CX, 1);
    asm.Jge(startOk);
    asm.Mov(Reg.CX, 1);
    asm.MarkLabel(startOk);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(none);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, Mem.Word(Reg.BX, asm.Lbl("rt_strtab")));       // haystack base
    asm.Mov(Reg.DX, Mem.Word(Reg.BX, asm.Lbl("rt_strtab"), 2));    // haystack length
    asm.Cmp(Reg.DX, needle.Length);
    asm.Jb(none);
    asm.Sub(Reg.DX, needle.Length);                  // last valid zero-based start
    asm.Dec(Reg.CX);                                 // zero-based current start
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Ja(none);
    asm.Cld();

    asm.MarkLabel(loop);
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);
    asm.Add(Reg.DI, needle.Length - 1);              // candidate's last text byte
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Es());
    asm.Cmp(Reg.AL, (Imm)(byte)needle[^1]);
    asm.Jne(shift);

    // Last byte matched. Verify the prefix; CX/DX/SI are the loop state and must survive CMPSB.
    asm.Mov(Reg.DI, Reg.SI);
    asm.Add(Reg.DI, Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.SI, Imm.OffsetOf(needleLabel));
    asm.Mov(Reg.CX, needle.Length - 1);
    asm.Repe();
    asm.Cmpsb();
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Je(found);

    asm.MarkLabel(shift);
    // AL still contains the candidate's last text byte; XLAT maps it to the precomputed safe shift.
    asm.Mov(Reg.BX, Imm.OffsetOf(skipLabel));
    asm.Xlat();
    asm.Xor(Reg.AH, Reg.AH);
    asm.Add(Reg.CX, Reg.AX);
    asm.Cmp(Reg.CX, Reg.DX);
    asm.Jbe(loop);
    asm.Jmp(none);

    asm.MarkLabel(found);
    asm.Mov(Reg.AX, Reg.CX);
    asm.Inc(Reg.AX);
    asm.Jmp(output);
    asm.MarkLabel(none);
    asm.Xor(Reg.AX, Reg.AX);
    asm.MarkLabel(output);
    asm.Pop(Reg.BX);                                 // owned haystack handle
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, Reg.BX);
    asm.Call(this._rt.StrFree);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
  }

  private static byte[] BuildHorspoolSkipTable(string needle) {
    var defaultShift = (byte)Math.Min(needle.Length, byte.MaxValue);
    var result = new byte[byte.MaxValue + 1];
    Array.Fill(result, defaultShift);
    for (var i = 0; i < needle.Length - 1; ++i)
      result[(byte)needle[i]] = (byte)Math.Min(needle.Length - 1 - i, byte.MaxValue);
    return result;
  }
}
