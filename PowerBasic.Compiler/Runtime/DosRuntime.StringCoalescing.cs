using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// O0289's DOS runtime half: one worst-case heap preflight followed by cheap carving of ordinary
/// string blocks. The representation deliberately does not change — every result still has the same
/// descriptor and <c>[handle,length]</c> heap header as a normal <see cref="StrAlloc"/> result — so
/// every existing consumer, <see cref="StrFree"/> and the compacting collector remain valid.
/// </summary>
public sealed partial class DosRuntime {

  public Label StrCoalesceBegin { get; private set; } = null!;
  public Label StrCoalesceEnd { get; private set; } = null!;

  private const int _STR_COALESCE_STATE_BYTES = 4; // active word, next-descriptor byte offset word

  /// <summary>
  /// Emits the O0289 entries. These live in the existing <c>extras</c> runtime section: referencing a
  /// coalesced IR call keeps this section, and its calls into the ordinary string section keep the
  /// canonical allocator/free implementation reachable for fallback and ownership operations.
  /// </summary>
  private void EmitStringCoalescingProcedures(Assembler asm) {
    this.EmitStrCoalesceBeginEnd(asm);
    this.EmitStrCoalescedAlloc(asm);
    this.EmitStrMemCoalesced(asm);
    this.EmitStrMidCoalesced(asm);
    this.EmitStrFillCoalesced(asm);
    this.ZeroBlob(asm, "rt_strcoal_state", _STR_COALESCE_STATE_BYTES);
  }

  /// <summary>
  /// CX = worst-case bytes for the region. A successful begin compacts at most once and arms the
  /// fast allocator only when the complete region fits. Failure to reserve is NOT an error: the
  /// coalesced entries transparently fall back to the ordinary allocator, preserving the program's
  /// original allocation/error behaviour rather than making an optimization introduce OOM.
  /// </summary>
  private void EmitStrCoalesceBeginEnd(Assembler asm) {
    this.StrCoalesceBegin = asm.MarkLabel("rt_strcoal_begin");
    var retry = asm.DefineLabel();
    var arm = asm.DefineLabel();
    var done = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.DX);
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state")), (Imm)0);      // inactive until the preflight succeeds
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state"), 2), (Imm)4);  // descriptor 1, byte offset in rt_strtab
    asm.Test(Reg.CX, Reg.CX);
    asm.Jz(done);

    // First try the current tail. If it does not fit, compact ONCE and retry. The pass guarantees
    // every allocator inside the region has a conservative upper bound included in CX.
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Mov(Reg.DX, Reg.AX);
    asm.Add(Reg.DX, Reg.CX);
    asm.Jc(retry);
    asm.Cmp(Reg.DX, 0xFFF0);
    asm.Jbe(arm);

    asm.MarkLabel(retry);
    asm.Call(asm.Lbl("rt_strcompact"));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Mov(Reg.DX, Reg.AX);
    asm.Add(Reg.DX, Reg.CX);
    asm.Jc(done);
    asm.Cmp(Reg.DX, 0xFFF0);
    asm.Ja(done);

    asm.MarkLabel(arm);
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state")), (Imm)1);
    asm.MarkLabel(done);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.AX);
    asm.Ret();

    this.StrCoalesceEnd = asm.MarkLabel("rt_strcoal_end");
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state")), (Imm)0);
    asm.Ret();
  }

  /// <summary>
  /// CX = requested payload bytes -> AX = ordinary string handle. While a preflighted region is
  /// active, this is StrAlloc with the repeated heap-space/compaction test removed and with a
  /// descriptor scan cursor carried from the preceding allocation. A defensive bounds check remains:
  /// if an analysis bug or a future runtime change violates the preflight, the region is disarmed and
  /// the canonical StrAlloc takes over instead of corrupting the heap.
  /// </summary>
  private void EmitStrCoalescedAlloc(Assembler asm) {
    asm.MarkLabel("rt_strcoal_alloc");
    var fallback = asm.DefineLabel();
    var empty = asm.DefineLabel();
    var haveStart = asm.DefineLabel();
    var scan = asm.DefineLabel();
    var wrap = asm.DefineLabel();
    var found = asm.DefineLabel();
    var noFastPath = asm.DefineLabel();
    var nextReady = asm.DefineLabel();

    asm.Cmp(Mem.Word(asm.Lbl("rt_strcoal_state")), (Imm)0);
    asm.Je(fallback);
    asm.Test(Reg.CX, Reg.CX);
    asm.Jz(empty);
    // Let the canonical allocator raise the exact "String too long" error when necessary.
    asm.Cmp(Reg.CX, Mem.Word(asm.Lbl("rt_strmaxlen")));
    asm.Ja(fallback);

    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));

    // Continue the descriptor-table scan where the previous coalesced allocation stopped. This is
    // the descriptor analogue of carving one arena rather than restarting at handle 1 N times.
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_strcoal_state"), 2));
    asm.Cmp(Reg.BX, 4);
    asm.Jae(haveStart);
    asm.Mov(Reg.BX, 4);
    asm.MarkLabel(haveStart);
    asm.Mov(Reg.DX, Reg.BX);                         // first descriptor offset, for one wrap only

    asm.MarkLabel(scan);
    asm.Cmp(this.Descriptor(Reg.BX, 2), (Imm)0);
    asm.Je(found);
    asm.Add(Reg.BX, 4);
    asm.Cmp(Reg.BX, _STRING_HANDLES * 4);
    asm.Jb(scan);
    asm.Mov(Reg.BX, 4);

    asm.MarkLabel(wrap);
    asm.Cmp(Reg.BX, Reg.DX);
    asm.Jae(noFastPath);                             // every descriptor was live
    asm.Cmp(this.Descriptor(Reg.BX, 2), (Imm)0);
    asm.Je(found);
    asm.Add(Reg.BX, 4);
    asm.Jmp(wrap);

    asm.MarkLabel(found);
    // Defensive fit check. A successful begin proves this for the entire region, but keeping the
    // fallback makes the runtime robust against a bad/missing bound rather than trusting optimizer IR.
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_strtop")));
    asm.Mov(Reg.DX, Reg.DI);
    asm.Add(Reg.DX, Reg.CX);
    asm.Jc(noFastPath);
    asm.Add(Reg.DX, 4);
    asm.Jc(noFastPath);
    asm.Cmp(Reg.DX, 0xFFF0);
    asm.Ja(noFastPath);

    asm.Mov(Reg.AX, Reg.BX);
    asm.Shr(Reg.AX, 2);                              // descriptor byte offset -> handle
    asm.Mov(Mem.Word(Reg.DI).Es(), Reg.AX);
    asm.Mov(Mem.Word(Reg.DI, 2).Es(), Reg.CX);
    asm.Add(Reg.DI, 4);
    asm.Mov(this.Descriptor(Reg.BX), Reg.DI);
    asm.Mov(this.Descriptor(Reg.BX, 2), Reg.CX);
    asm.Mov(Mem.Word(asm.Lbl("rt_strtop")), Reg.DX);

    asm.Add(Reg.BX, 4);
    asm.Cmp(Reg.BX, _STRING_HANDLES * 4);
    asm.Jb(nextReady);
    asm.Mov(Reg.BX, 4);
    asm.MarkLabel(nextReady);
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state"), 2), Reg.BX);

    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Ret();

    asm.MarkLabel(noFastPath);
    // Restore the caller-visible register convention before delegating. Once one allocation falls
    // back, later coalesced entries in the same region do too; a canonical allocation may compact.
    asm.Mov(Mem.Word(asm.Lbl("rt_strcoal_state")), (Imm)0);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Jmp(this.StrAlloc);

    asm.MarkLabel(fallback);
    asm.Jmp(this.StrAlloc);

    asm.MarkLabel(empty);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
  }

  /// <summary>DX:SI source bytes, CX length -> AX handle, using the coalesced allocator when armed.</summary>
  private void EmitStrMemCoalesced(Assembler asm) {
    asm.MarkLabel("rt_strmem_coal");
    var done = asm.DefineLabel();
    asm.Call(asm.Lbl("rt_strcoal_alloc"));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Mov(Reg.DS, Reg.DX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
    asm.Ret();
  }

  /// <summary>
  /// Emits consuming and borrowed LEFT$/RIGHT$/MID$ variants. Borrowed entries are the ownership
  /// cancellation used when the middle end removes a single-use rt_str_dup of a variable read.
  /// </summary>
  private void EmitStrMidCoalesced(Assembler asm) {
    EmitMid("rt_strmid_coal", consume: true);
    EmitMid("rt_strmid_bcoal", consume: false);

    asm.MarkLabel("rt_strleft_coal");
    asm.Mov(Reg.DX, Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Jmp(asm.Lbl("rt_strmid_coal"));

    asm.MarkLabel("rt_strleft_bcoal");
    asm.Mov(Reg.DX, Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Jmp(asm.Lbl("rt_strmid_bcoal"));

    EmitRight("rt_strright_coal", "rt_strmid_coal");
    EmitRight("rt_strright_bcoal", "rt_strmid_bcoal");
    return;

    void EmitRight(string label, string mid) {
      asm.MarkLabel(label);
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.DX, this.Descriptor(Reg.BX, 2));
      asm.Pop(Reg.BX);
      asm.Sub(Reg.DX, Reg.CX);
      asm.Inc(Reg.DX);                               // start = len-count+1; MID$ clamps below 1
      asm.Xchg(Reg.CX, Reg.DX);
      asm.Jmp(asm.Lbl(mid));
    }

    void EmitMid(string label, bool consume) {
      asm.MarkLabel(label);
      var startOk = asm.DefineLabel();
      var lenPos = asm.DefineLabel();
      var lenOk = asm.DefineLabel();
      var empty = asm.DefineLabel();
      var output = asm.DefineLabel();

      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX); // source handle
      asm.Cmp(Reg.CX, 1);
      asm.Jge(startOk);
      asm.Mov(Reg.CX, 1);
      asm.MarkLabel(startOk);
      asm.Test(Reg.DX, Reg.DX);
      asm.Jns(lenPos);
      asm.Xor(Reg.DX, Reg.DX);
      asm.MarkLabel(lenPos);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));
      asm.Sub(Reg.AX, Reg.CX);
      asm.Js(empty);
      asm.Inc(Reg.AX);                              // bytes available from the clamped start
      asm.Cmp(Reg.DX, Reg.AX);
      asm.Jbe(lenOk);
      asm.Mov(Reg.DX, Reg.AX);
      asm.MarkLabel(lenOk);
      asm.Test(Reg.DX, Reg.DX);
      asm.Jz(empty);
      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX); // start survives allocation/fallback
      asm.Mov(Reg.CX, Reg.DX);
      asm.Call(asm.Lbl("rt_strcoal_alloc"));
      asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.AX); // result

      // The defensive fallback above may have compacted, so always re-fetch the source descriptor.
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));
      asm.Add(Reg.SI, Mem.Word(asm.Lbl("rt_st1")));
      asm.Dec(Reg.SI);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
      asm.Push(Reg.DS);
      asm.Mov(Reg.BX, Reg.ES);
      asm.Mov(Reg.DS, Reg.BX);
      asm.Rep();
      asm.Movsb();
      asm.Pop(Reg.DS);

      if (consume) {
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
        asm.Call(this.StrFree);
      }
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st2")));
      asm.Jmp(output);

      asm.MarkLabel(empty);
      if (consume) {
        asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
        asm.Call(this.StrFree);
      }
      asm.Xor(Reg.AX, Reg.AX);
      asm.MarkLabel(output);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }
  }

  /// <summary>STRING$/SPACE$/CHR$ result allocation through the preflighted region.</summary>
  private void EmitStrFillCoalesced(Assembler asm) {
    asm.MarkLabel("rt_strfill_coal");
    var go = asm.DefineLabel();
    asm.Test(Reg.CX, Reg.CX);
    asm.Jg(go);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Ret();
    asm.MarkLabel(go);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(asm.Lbl("rt_strcoal_alloc"));
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.DI, this.Descriptor(Reg.BX));
    asm.Push(Reg.AX);
    asm.Mov(Reg.AL, Reg.DL);
    asm.Rep();
    asm.Stosb();
    asm.Pop(Reg.AX);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();

    asm.MarkLabel("rt_chr_coal");
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, 1);
    asm.Call(asm.Lbl("rt_strfill_coal"));
    asm.Pop(Reg.CX);
    asm.Ret();
  }
}
