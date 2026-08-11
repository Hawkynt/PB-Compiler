using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Low-level services. Register conventions (everything not returned is preserved):
///   Interrupt:  AL=vector - loads AX..DI/DS/ES from the REG buffer rt_regs
///               (word index = PB REG number: 0=FLAGS 1=AX 2=BX 3=CX 4=DX 5=SI
///               6=DI 7=BP 8=DS 9=ES), executes INT, stores all registers and
///               the flags back. BP stays the program's.
///   StrPtr:     AX=raw string handle -> AX=data offset in the string heap
///               (not consumed; 0 for the empty handle)
///   Raise:      AX=error code - sets ERR; jumps to the armed ON ERROR handler
///               (restoring its BP/SP) or terminates with a fatal message.
/// Error-handling cells: rt_err (ERR), rt_onerr/rt_onerr_bp/rt_onerr_sp (the
/// armed handler), rt_resume/rt_resumenext (statement bookkeeping for RESUME,
/// maintained only inside scopes that contain ON ERROR/RESUME).
/// DEF SEG: rt_defseg holds the current PEEK/POKE segment (DS initially).
/// </summary>
public sealed partial class DosRuntime {

  public Label Interrupt { get; private set; } = null!;
  public Label StrPtr { get; private set; } = null!;
  public Label Raise { get; private set; } = null!;
  public Label Swap { get; private set; } = null!;
  public Label ResumeNextHandler { get; private set; } = null!;

  /// <summary>CSRLIN: the cursor row, 1-based (BIOS reports it 0-based).</summary>
  public Label CsrLin { get; private set; } = null!;

  /// <summary>CONSIN / CONSOUT: -1 when the handle is a console, 0 when it is redirected.</summary>
  public Label ConsIn { get; private set; } = null!;
  public Label ConsOut { get; private set; } = null!;

  /// <summary>DEF SEG with no argument: the default segment goes back to DS.</summary>
  public Label DefSegReset { get; private set; } = null!;

  /// <summary>REG n: AX = the PB register number -> AX = the word held for it in <c>rt_regs</c>.</summary>
  public Label RegGet { get; private set; } = null!;

  /// <summary>REG n, v: AX = the PB register number, DX = the word to hold for it.</summary>
  public Label RegSet { get; private set; } = null!;

  /// <summary>PEEK(offset): AX = offset -> AX = the byte there, zero-extended, from DEF SEG's segment.</summary>
  public Label Peek { get; private set; } = null!;

  /// <summary>POKE offset, value: AX = offset, DL = the byte, written into DEF SEG's segment.</summary>
  public Label Poke { get; private set; } = null!;

  private void EmitLowLevelProcedures(Assembler asm) {
    var regs = asm.Lbl("rt_regs");

    // Three sequences the direct emitter writes INLINE, given labels so the IR path can reach them
    // too. It cannot inline them: an INT is not something the IR can say, and the C and LLVM back
    // ends need a name to bind their own answer to. The direct emitter keeps writing them inline -
    // this changes no existing output, it only adds somewhere to call.
    this.CsrLin = asm.MarkLabel("rt_csrlin");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.AH, (Imm)3);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Int(0x10);
      asm.Mov(Reg.AL, Reg.DH);          // DH = row, 0-based
      asm.Xor(Reg.AH, Reg.AH);
      asm.Inc(Reg.AX);                  // ...and PB counts from 1
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // IOCTL get-device-info on handle 0 or 1: bit 7 of DL set means a character device
    var consoleShared = asm.DefineLabel();
    this.ConsIn = asm.MarkLabel("rt_consin");
    asm.Xor(Reg.BX, Reg.BX);
    asm.Jmp(consoleShared);
    this.ConsOut = asm.MarkLabel("rt_consout");
    asm.Mov(Reg.BX, 1);
    asm.MarkLabel(consoleShared);
    {
      var isConsole = asm.DefineLabel();
      asm.Push(Reg.DX);
      asm.Mov(Reg.AX, 0x4400);
      asm.Int(0x21);
      asm.Mov(Reg.AX, -1);
      asm.Test(Reg.DL, (Imm)0x80);
      asm.Jnz(isConsole);
      asm.Xor(Reg.AX, Reg.AX);
      asm.MarkLabel(isConsole);
      asm.Pop(Reg.DX);
      asm.Ret();
    }

    // REG n and REG n, v: read and write the register buffer INT/INTERRUPT loads from. The direct
    // emitter indexes rt_regs inline; the IR has no way to name a scaled index into a runtime table,
    // so the scaling moves in here. Both touch the SAME buffer the interrupt entry reads, which is
    // what lets a routed REG set up a directly-emitted INTERRUPT.
    this.RegGet = asm.MarkLabel("rt_regget");
    {
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.AX, Mem.Word(Reg.BX, regs));
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.RegSet = asm.MarkLabel("rt_regset");
    {
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);          // AX = the PB register number, DX = the value
      asm.Shl(Reg.BX, 1);
      asm.Mov(Mem.Word(Reg.BX, regs), Reg.DX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // PEEK and POKE, for the same reason as the three above: the direct emitter writes them inline,
    // as MOV ES, [rt_defseg] and a segment-overridden byte access, and a segment override is not
    // something the IR can say. Both read the SAME rt_defseg cell the inline form does, so a program
    // whose DEF SEG was set by a directly-emitted statement and read by a routed one agrees with
    // itself. Byte-wide only: PEEK's 2- and 4-byte forms and POKE$ still take the inline path.
    this.Peek = asm.MarkLabel("rt_peek");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.ES);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_defseg")));
      asm.Mov(Reg.AL, Mem.Byte(Reg.BX).Es());
      asm.Xor(Reg.AH, Reg.AH);          // PB reports a byte as an unsigned INTEGER
      asm.Pop(Reg.ES);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.Poke = asm.MarkLabel("rt_poke");
    {
      asm.Push(Reg.BX);
      asm.Push(Reg.ES);
      asm.Mov(Reg.BX, Reg.AX);          // AX = offset, DL = the byte to write
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_defseg")));
      asm.Mov(Mem.Byte(Reg.BX).Es(), Reg.DL);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.DefSegReset = asm.MarkLabel("rt_defsegreset");
    {
      asm.Push(Reg.AX);
      asm.Mov(Reg.AX, Reg.DS);
      asm.Mov(Mem.Word(asm.Lbl("rt_defseg")), Reg.AX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Swap = asm.MarkLabel("rt_swap");
    {
      // DX:SI <-> ES:DI, CX bytes
      var loop = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.DS);
      asm.Jcxz(done);
      asm.Mov(Reg.DS, Reg.DX);
      asm.MarkLabel(loop);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI));
      asm.Xchg(Reg.AL, Mem.Byte(Reg.DI).Es());
      asm.Mov(Mem.Byte(Reg.SI), Reg.AL);
      asm.Inc(Reg.SI);
      asm.Inc(Reg.DI);
      asm.Loop(loop);
      asm.MarkLabel(done);
      asm.Pop(Reg.DS);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Interrupt = asm.MarkLabel("rt_interrupt");
    {
      asm.Mov(Mem.Byte(asm.Lbl("rt_int_op"), 1), Reg.AL);     // patch the INT vector (CS=DS)
      asm.Push(Reg.BP);
      asm.Push(Reg.DS);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Mem.Word(regs, 18));
      asm.Mov(Reg.AX, Mem.Word(regs, 2));
      asm.Mov(Reg.BX, Mem.Word(regs, 4));
      asm.Mov(Reg.CX, Mem.Word(regs, 6));
      asm.Mov(Reg.DX, Mem.Word(regs, 8));
      asm.Mov(Reg.SI, Mem.Word(regs, 10));
      asm.Mov(Reg.DI, Mem.Word(regs, 12));
      asm.Mov(Reg.DS, Mem.Word(regs, 16));
      asm.MarkLabel("rt_int_op");
      asm.Int(0);
      asm.Mov(Mem.Word(regs, 2).Cs(), Reg.AX);
      asm.Mov(Mem.Word(regs, 4).Cs(), Reg.BX);
      asm.Mov(Mem.Word(regs, 6).Cs(), Reg.CX);
      asm.Mov(Mem.Word(regs, 8).Cs(), Reg.DX);
      asm.Mov(Mem.Word(regs, 10).Cs(), Reg.SI);
      asm.Mov(Mem.Word(regs, 12).Cs(), Reg.DI);
      asm.Mov(Reg.AX, Reg.DS);
      asm.Mov(Mem.Word(regs, 16).Cs(), Reg.AX);
      asm.Mov(Reg.AX, Reg.ES);
      asm.Mov(Mem.Word(regs, 18).Cs(), Reg.AX);
      asm.Pushf();
      asm.Pop(Reg.AX);
      asm.Mov(Mem.Word(regs, 0).Cs(), Reg.AX);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DS);
      asm.Pop(Reg.BP);
      asm.Ret();
    }

    this.StrPtr = asm.MarkLabel("rt_strptr");
    {
      asm.Push(Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.AX, this.Descriptor(Reg.BX));
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.Raise = asm.MarkLabel("rt_raise");
    {
      var fatal = asm.DefineLabel("rt_raise_fatal");
      asm.Mov(Mem.Word(asm.Lbl("rt_err")), Reg.AX);
      asm.Cmp(Mem.Word(asm.Lbl("rt_onerr")), (Imm)0);
      asm.Je(fatal);
      // latch the failing statement's resume targets - handler statements keep
      // updating the live cells, RESUME reads the latched copies
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_resume")));
      asm.Mov(Mem.Word(asm.Lbl("rt_eresume")), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_resumenext")));
      asm.Mov(Mem.Word(asm.Lbl("rt_eresumenext")), Reg.AX);
      asm.Mov(Reg.SP, Mem.Word(asm.Lbl("rt_onerr_sp")));
      asm.Mov(Reg.BP, Mem.Word(asm.Lbl("rt_onerr_bp")));
      asm.Jmp(Mem.Word(asm.Lbl("rt_onerr")));

      asm.MarkLabel(fatal);
      asm.Mov(Reg.DX, Imm.OffsetOf(asm.Lbl("rt_err_run_msg")));
      asm.Mov(Reg.CX, 15);
      asm.Mov(Reg.BX, 1);
      asm.Mov(Reg.AH, 0x40);
      asm.Int(0x21);
      asm.Mov(Reg.AL, (Imm)3);
      asm.Jmp(this.Exit);
    }

    // ON ERROR RESUME NEXT (inline mode): rt_onerr points here, so rt_raise
    // dispatches straight to it after restoring SP/BP. Unlike the RESUME NEXT
    // statement (which clears ERR), inline mode leaves ERR set until the next
    // fault or ERRCLEAR - so we just jump to the latched successor offset.
    this.ResumeNextHandler = asm.MarkLabel("rt_resumenext_handler");
    asm.Jmp(Mem.Word(asm.Lbl("rt_eresumenext")));
  }

  private void EmitLowLevelData(Assembler asm) {
    asm.Align(2);
    this.ZeroBlob(asm, "rt_regs", 20);
    asm.MarkLabel("rt_defseg");
    asm.Dw(0);
    asm.MarkLabel("rt_err");
    asm.Dw(0);
    asm.MarkLabel("rt_onerr");
    asm.Dw(0);
    asm.MarkLabel("rt_onerr_bp");
    asm.Dw(0);
    asm.MarkLabel("rt_onerr_sp");
    asm.Dw(0);
    asm.MarkLabel("rt_resume");
    asm.Dw(0);
    asm.MarkLabel("rt_resumenext");
    asm.Dw(0);
    asm.MarkLabel("rt_eresume");
    asm.Dw(0);
    asm.MarkLabel("rt_eresumenext");
    asm.Dw(0);
    // LINE parameter block (DosRuntime.Graphics.cs). rt_gx1/rt_gy1 is also PB's "last point
    // referenced", which is what makes LINE -(x, y) mean anything - with no start point the segment
    // begins wherever the previous graphics statement finished.
    foreach (var cell in new[] {
      "rt_gx1", "rt_gy1", "rt_gx2", "rt_gy2", "rt_gcolor", "rt_gstyle",
      "rt_gerr", "rt_gsx", "rt_gsy", "rt_gdx", "rt_gdy",
      "rt_gbx1", "rt_gby1", "rt_gbx2", "rt_gby2",
      "rt_gcx", "rt_gcy", "rt_gr",
    }) {
      asm.MarkLabel(cell);
      asm.Dw(0);
    }
    // EMS page-frame mapping cache: which handle/logical-page pair is mapped at physical 0/1.
    // GLOBAL, not per-array - every EMS/XMS array shares the one frame, so a remap by any of
    // them must invalidate the others' idea of the window. 0xFFFF = nothing mapped.
    asm.MarkLabel("rt_ems_curhnd");
    asm.Dw(0xFFFF);
    asm.MarkLabel("rt_ems_curpage");
    asm.Dw(0xFFFF);
    // C6 UMB bookkeeping: saved DOS UMB-link state and allocation strategy + an
    // "we changed it" latch consulted by the exit restore
    asm.MarkLabel("rt_umb_oldlink");
    asm.Dw(0);
    asm.MarkLabel("rt_umb_oldstrat");
    asm.Dw(0);
    asm.MarkLabel("rt_umb_active");
    asm.Dw(0);
  }
}
