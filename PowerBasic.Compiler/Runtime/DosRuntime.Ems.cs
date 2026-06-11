using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// HUGE (DOS 48h conventional memory) and VIRTUAL (EMS, int 67h) array support.
/// Conventions:
///   HugeAlloc:  DX:AX = byte count -> AX = segment (raises out-of-array-space)
///   HugeFree:   AX = segment (0 ok)
///   HugeZero:   AX = segment, DX:AX..: DX:CX? - see comments
///   EmsAlloc:   DX:AX = byte count -> AX = EMS handle (pages + 1 spare for straddling)
///   EmsFree:    DX = EMS handle
///   EmsFrame:   -> AX = page-frame segment (raises when no EMM present)
///   EmsFre:     -> DX:AX = free EMS bytes (FRE(-11); 0 without EMM)
///   EmsMap2:    DX = handle, BX = logical page - maps BX/BX+1 at physical 0/1
/// </summary>
public sealed partial class DosRuntime {

  public Label HugeAlloc { get; private set; } = null!;
  public Label HugeFree { get; private set; } = null!;
  public Label HugeZero { get; private set; } = null!;
  public Label EmsAlloc { get; private set; } = null!;
  public Label EmsFree { get; private set; } = null!;
  public Label EmsFrame { get; private set; } = null!;
  public Label EmsFre { get; private set; } = null!;
  public Label EmsMap2 { get; private set; } = null!;

  private void EmitEmsProcedures(Assembler asm) {
    // ---- HUGE: conventional memory via DOS 48h/49h ---------------------------
    this.HugeAlloc = asm.MarkLabel("rt_hugealloc");
    {
      var ok = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Add(Reg.AX, 15);                        // paragraphs = (bytes + 15) >> 4
      asm.Adc(Reg.DX, (Imm)0);
      asm.Mov(Reg.CL, (Imm)4);
      asm.Shr(Reg.AX, Reg.CL);
      asm.Mov(Reg.CL, (Imm)12);
      asm.Shl(Reg.DX, Reg.CL);
      asm.Or(Reg.AX, Reg.DX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AH, 0x48);
      asm.Int(0x21);
      asm.Jnc(ok);
      asm.Jmp(asm.Lbl("rt_err_arr"));             // out of array space
      asm.MarkLabel(ok);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.HugeFree = asm.MarkLabel("rt_hugefree");
    {
      var done = asm.DefineLabel();
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(done);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Push(Reg.AX);
      asm.Mov(Reg.AH, 0x49);
      asm.Int(0x21);
      asm.Pop(Reg.AX);
      asm.Pop(Reg.ES);
      asm.MarkLabel(done);
      asm.Ret();
    }

    // HugeZero: AX = segment, CX:BX = byte count (zeroes in 32 KiB strides)
    this.HugeZero = asm.MarkLabel("rt_hugezero");
    {
      var loop = asm.DefineLabel();
      var small = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.DX, Reg.AX);                    // DX = current segment
      asm.MarkLabel(loop);
      asm.Mov(Reg.AX, Reg.CX);
      asm.Or(Reg.AX, Reg.BX);
      asm.Jz(done);
      // chunk = min(remaining, 0x8000)
      asm.Test(Reg.CX, Reg.CX);
      asm.Jnz(asm.Lbl("rt_hz_big"));
      asm.Cmp(Reg.BX, 0x8000);
      asm.Jbe(small);
      asm.MarkLabel("rt_hz_big");
      asm.Mov(Reg.AX, 0x8000);
      asm.Jmp(asm.Lbl("rt_hz_chunk"));
      asm.MarkLabel(small);
      asm.Mov(Reg.AX, Reg.BX);
      asm.MarkLabel("rt_hz_chunk");
      asm.Sub(Reg.BX, Reg.AX);                    // remaining -= chunk
      asm.Sbb(Reg.CX, (Imm)0);
      asm.Push(Reg.CX);
      asm.Mov(Reg.ES, Reg.DX);
      asm.Xor(Reg.DI, Reg.DI);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Shr(Reg.CX, 1);
      asm.Xor(Reg.AX, Reg.AX);
      asm.Rep();
      asm.Stosw();
      asm.Pop(Reg.CX);
      asm.Add(Reg.DX, 0x800);                     // advance 32 KiB (0x800 paragraphs)
      asm.Jmp(loop);
      asm.MarkLabel(done);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    // ---- VIRTUAL: EMS ---------------------------------------------------------
    // page-frame segment, cached after the first query (0 = not yet queried)
    this.EmsFrame = asm.MarkLabel("rt_emsframe");
    {
      var cached = asm.DefineLabel();
      var ok = asm.DefineLabel();
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_emsseg")));
      asm.Test(Reg.AX, Reg.AX);
      asm.Jnz(cached);
      asm.Push(Reg.BX);
      asm.Mov(Reg.AH, 0x41);                      // get page frame address
      asm.Int(0x67);
      asm.Test(Reg.AH, Reg.AH);
      asm.Jz(ok);
      asm.Pop(Reg.BX);
      asm.Jmp(asm.Lbl("rt_err_arr"));             // no EMM / EMS error
      asm.MarkLabel(ok);
      asm.Mov(Mem.Word(asm.Lbl("rt_emsseg")), Reg.BX);
      asm.Mov(Reg.AX, Reg.BX);
      asm.Pop(Reg.BX);
      asm.MarkLabel(cached);
      asm.Ret();
    }

    this.EmsAlloc = asm.MarkLabel("rt_emsalloc");
    {
      var ok = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      // pages = (bytes >> 14) + 2 (one spare so two consecutive pages always map)
      asm.Mov(Reg.CL, (Imm)14);
      asm.Shr(Reg.AX, Reg.CL);
      asm.Mov(Reg.CL, (Imm)2);
      asm.Shl(Reg.DX, Reg.CL);
      asm.Or(Reg.AX, Reg.DX);
      asm.Add(Reg.AX, 2);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AH, 0x43);                      // allocate pages -> DX = handle
      asm.Int(0x67);
      asm.Test(Reg.AH, Reg.AH);
      asm.Jz(ok);
      asm.Jmp(asm.Lbl("rt_err_arr"));
      asm.MarkLabel(ok);
      asm.Mov(Reg.AX, Reg.DX);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    this.EmsFree = asm.MarkLabel("rt_emsfree");
    {
      var done = asm.DefineLabel();
      asm.Test(Reg.DX, Reg.DX);
      asm.Jz(done);
      asm.Push(Reg.AX);
      asm.Mov(Reg.AH, 0x45);                      // release handle
      asm.Int(0x67);
      asm.Pop(Reg.AX);
      asm.MarkLabel(done);
      asm.Ret();
    }

    this.EmsFre = asm.MarkLabel("rt_emsfre");
    {
      var noEms = asm.DefineLabel();
      var output = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Mov(Reg.AH, 0x42);                      // get unallocated page count -> BX
      asm.Int(0x67);
      asm.Test(Reg.AH, Reg.AH);
      asm.Jnz(noEms);
      // DX:AX = pages * 16384
      asm.Mov(Reg.AX, Reg.BX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Mov(Reg.CX, 14);
      asm.MarkLabel("rt_emsfre_shift");
      asm.Shl(Reg.AX, 1);
      asm.Rcl(Reg.DX, 1);
      asm.Loop(asm.Lbl("rt_emsfre_shift"));
      asm.Jmp(output);
      asm.MarkLabel(noEms);
      asm.Xor(Reg.AX, Reg.AX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.MarkLabel(output);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // EmsMap2: DX = handle, BX = logical page; maps BX -> physical 0, BX+1 -> physical 1
    this.EmsMap2 = asm.MarkLabel("rt_emsmap2");
    {
      var ok1 = asm.DefineLabel();
      var ok2 = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Mov(Reg.AX, 0x4400);                    // map physical page 0
      asm.Int(0x67);
      asm.Test(Reg.AH, Reg.AH);
      asm.Jz(ok1);
      asm.Jmp(asm.Lbl("rt_err_arr"));
      asm.MarkLabel(ok1);
      asm.Pop(Reg.BX);
      asm.Push(Reg.BX);
      asm.Inc(Reg.BX);
      asm.Mov(Reg.AX, 0x4401);                    // map physical page 1
      asm.Int(0x67);
      asm.Test(Reg.AH, Reg.AH);
      asm.Jz(ok2);
      asm.Jmp(asm.Lbl("rt_err_arr"));
      asm.MarkLabel(ok2);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.EmitEmsZero(asm);
  }

  public Label EmsZero { get; private set; } = null!;

  /// <summary>EmsZero: DX = handle, CX:BX = byte count - zero-fills the allocation page by page.</summary>
  private void EmitEmsZero(Assembler asm) {
    this.EmsZero = asm.MarkLabel("rt_emszero");
    var loop = asm.DefineLabel();
    var small = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Call(this.EmsFrame);                      // AX = frame segment
    asm.Mov(Reg.ES, Reg.AX);
    asm.Xor(Reg.SI, Reg.SI);                      // SI = logical page
    asm.MarkLabel(loop);
    asm.Mov(Reg.AX, Reg.CX);
    asm.Or(Reg.AX, Reg.BX);
    asm.Jz(done);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Reg.SI);
    asm.Call(this.EmsMap2);
    asm.Pop(Reg.BX);
    // chunk = min(remaining, 0x4000)
    asm.Test(Reg.CX, Reg.CX);
    asm.Jnz(asm.Lbl("rt_ez_big"));
    asm.Cmp(Reg.BX, 0x4000);
    asm.Jbe(small);
    asm.MarkLabel("rt_ez_big");
    asm.Mov(Reg.AX, 0x4000);
    asm.Jmp(asm.Lbl("rt_ez_chunk"));
    asm.MarkLabel(small);
    asm.Mov(Reg.AX, Reg.BX);
    asm.MarkLabel("rt_ez_chunk");
    asm.Sub(Reg.BX, Reg.AX);
    asm.Sbb(Reg.CX, (Imm)0);
    asm.Push(Reg.CX);
    asm.Xor(Reg.DI, Reg.DI);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Inc(Reg.CX);                              // odd chunks round up one word
    asm.Shr(Reg.CX, 1);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Rep();
    asm.Stosw();
    asm.Pop(Reg.CX);
    asm.Inc(Reg.SI);
    asm.Jmp(loop);
    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitEmsData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_emsseg");
    asm.Dw(0);
  }
}
