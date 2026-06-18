using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Console, keyboard, timing and conversion helpers. Conventions:
///   InKey:   -> AX=string handle ("" / 1 char / CHR$(0)+scan for extended keys)
///   Rnd:     -> ST0 = next SINGLE in [0,1) (xorshift-style LCG on rt_rndseed)
///   Timer:   -> ST0 = BIOS tick count / 18.2065 (seconds since midnight)
///   Cv:      AX=string handle (consumed), CX=byte count -> value bytes
///            zero-padded at rt_scratch
///   Locate:  AX=row, CX=column (1-based; 0 = keep current)
///   Cls:     clears the text screen via BIOS scroll, cursor home
///   Sound:   AX=frequency (Hz), DX=duration in BIOS ticks (PIT-programmed)
///   Delay:   ST0=seconds (popped) - busy-waits on the BIOS tick counter
/// </summary>
public sealed partial class DosRuntime {

  public Label InKey { get; private set; } = null!;
  public Label Rnd { get; private set; } = null!;
  public Label Timer { get; private set; } = null!;
  public Label Cv { get; private set; } = null!;
  public Label Locate { get; private set; } = null!;
  public Label Cls { get; private set; } = null!;
  public Label Sound { get; private set; } = null!;
  public Label Delay { get; private set; } = null!;
  public Label ScreenMode { get; private set; } = null!;
  public Label Spc { get; private set; } = null!;
  public Label Tab { get; private set; } = null!;
  public Label UseFmt { get; private set; } = null!;
  public Label ReadData { get; private set; } = null!;

  private void EmitMiscProcedures(Assembler asm) {
    this.InKey = asm.MarkLabel("rt_inkey");
    {
      var none = asm.DefineLabel();
      var extended = asm.DefineLabel();
      var make = asm.DefineLabel();
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.AH, (Imm)1);
      asm.Int(0x16);
      asm.Jz(none);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x16);
      asm.Test(Reg.AL, Reg.AL);
      asm.Jz(extended);
      asm.Mov(Mem.Byte(this._scratch), Reg.AL);
      asm.Mov(Reg.CX, 1);
      asm.Jmp(make);
      asm.MarkLabel(extended);
      asm.Mov(Mem.Byte(this._scratch), (Imm)0);
      asm.Mov(Mem.Byte(this._scratch, 1), Reg.AH);
      asm.Mov(Reg.CX, 2);
      asm.MarkLabel(make);
      asm.Mov(Reg.SI, Imm.OffsetOf(this._scratch));
      asm.Mov(Reg.DX, Reg.DS);
      asm.Call(this.StrMem);
      asm.Jmp(asm.Lbl("rt_inkey_done"));
      asm.MarkLabel(none);
      asm.Xor(Reg.AX, Reg.AX);
      asm.MarkLabel("rt_inkey_done");
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Ret();
    }

    this.Rnd = asm.MarkLabel("rt_rnd");
    {
      // seed = seed * 1103515245 + 12345; mantissa = high 15 bits / 32768
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_rndseed")));
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_rndseed"), 2));
      asm.Mov(Reg.BX, unchecked((int)(1103515245 & 0xFFFF)));
      asm.Mov(Reg.CX, unchecked((int)((1103515245 >> 16) & 0xFFFF)));
      asm.Call(this.LongMul);
      asm.Add(Reg.AX, 12345);
      asm.Adc(Reg.DX, (Imm)0);
      asm.Mov(Mem.Word(asm.Lbl("rt_rndseed")), Reg.AX);
      asm.Mov(Mem.Word(asm.Lbl("rt_rndseed"), 2), Reg.DX);
      asm.And(Reg.DX, 0x7FFF);
      asm.Mov(Mem.Word(this._scratch), Reg.DX);
      asm.Fild(Mem.Word(this._scratch));
      asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_32768")));
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Timer = asm.MarkLabel("rt_timer");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x1A);                       // CX:DX = ticks since midnight
      asm.Mov(Mem.Word(this._scratch), Reg.DX);
      asm.Mov(Mem.Word(this._scratch, 2), Reg.CX);
      asm.Fild(Mem.Dword(this._scratch));
      asm.Fdiv(Mem.Qword(asm.Lbl("rt_const_tickrate")));
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Cv = asm.MarkLabel("rt_cv");
    {
      // AX=handle (consumed), CX=count -> bytes at rt_scratch (zero padded)
      var copy = asm.DefineLabel();
      var pad = asm.DefineLabel();
      var fill = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.DI, Imm.OffsetOf(this._scratch));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 1);
      asm.Shl(Reg.BX, 1);
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));        // data offset
      asm.Mov(Reg.DX, this.Descriptor(Reg.BX, 2));     // length
      asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
      asm.MarkLabel(copy);
      asm.Jcxz(done);
      asm.Test(Reg.DX, Reg.DX);
      asm.Jz(pad);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Es());
      asm.Mov(Mem.Byte(Reg.DI), Reg.AL);
      asm.Inc(Reg.SI);
      asm.Inc(Reg.DI);
      asm.Dec(Reg.DX);
      asm.Dec(Reg.CX);
      asm.Jmp(copy);
      asm.MarkLabel(pad);
      asm.Jcxz(done);
      asm.MarkLabel(fill);
      asm.Mov(Mem.Byte(Reg.DI), (Imm)0);
      asm.Inc(Reg.DI);
      asm.Loop(fill);
      asm.MarkLabel(done);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Call(this.StrFree);
      asm.Ret();
    }

    this.Locate = asm.MarkLabel("rt_locate");
    {
      var keepRow = asm.DefineLabel();
      var keepCol = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.AX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.CX);
      asm.Mov(Reg.AH, (Imm)3);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Int(0x10);                       // DH=row, DL=column (current)
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(keepRow);
      asm.Dec(Reg.AX);
      asm.Mov(Reg.DH, Reg.AL);
      asm.MarkLabel(keepRow);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st1")));
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(keepCol);
      asm.Dec(Reg.AX);
      asm.Mov(Reg.DL, Reg.AL);
      asm.Mov(Mem.Word(asm.Lbl("rt_col")), Reg.AX);  // POS(0) follows the LOCATE column (0-based)
      asm.MarkLabel(keepCol);
      asm.Mov(Reg.AH, (Imm)2);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Int(0x10);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Cls = asm.MarkLabel("rt_cls");
    {
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Mov(Reg.AX, 0x0600);             // scroll the whole window blank
      asm.Mov(Reg.BH, (Imm)7);
      asm.Xor(Reg.CX, Reg.CX);
      asm.Mov(Reg.DX, 0x184F);
      asm.Int(0x10);
      asm.Mov(Reg.AH, (Imm)2);             // cursor home
      asm.Xor(Reg.BH, Reg.BH);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Int(0x10);
      asm.Mov(Mem.Word(asm.Lbl("rt_col")), (Imm)0);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Sound = asm.MarkLabel("rt_sound");
    {
      // AX=frequency, DX=duration in ticks: PIT channel 2 + speaker gate
      var wait = asm.DefineLabel();
      var off = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.SI, Reg.DX);             // duration
      asm.Mov(Reg.BX, Reg.AX);             // frequency
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(off);
      asm.Mov(Reg.AL, (Imm)0xB6);
      asm.Out(0x43, Reg.AL);
      asm.Mov(Reg.DX, 0x0012);             // 1193180 / freq
      asm.Mov(Reg.AX, 0x3540);
      asm.Div(Reg.BX);
      asm.Out(0x42, Reg.AL);
      asm.Mov(Reg.AL, Reg.AH);
      asm.Out(0x42, Reg.AL);
      asm.In(Reg.AL, 0x61);
      asm.Or(Reg.AL, (Imm)3);
      asm.Out(0x61, Reg.AL);
      asm.MarkLabel(off);
      // busy wait SI ticks
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x1A);
      asm.Mov(Reg.CX, Reg.DX);             // start tick (low word is enough)
      asm.MarkLabel(wait);
      asm.Test(Reg.SI, Reg.SI);
      asm.Jz(asm.Lbl("rt_sound_off"));
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x1A);
      asm.Sub(Reg.DX, Reg.CX);
      asm.Cmp(Reg.DX, Reg.SI);
      asm.Jb(wait);
      asm.MarkLabel("rt_sound_off");
      asm.In(Reg.AL, 0x61);
      asm.And(Reg.AL, (Imm)0xFC);
      asm.Out(0x61, Reg.AL);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Delay = asm.MarkLabel("rt_delay");
    {
      // ST0 = seconds (popped); busy-wait on the BIOS tick counter
      var wait = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Fmul(Mem.Qword(asm.Lbl("rt_const_tickrate")));
      asm.Fistp(Mem.Word(this._scratch));
      asm.Mov(Reg.BX, Mem.Word(this._scratch));   // tick budget
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x1A);
      asm.Mov(Reg.CX, Reg.DX);                    // start
      asm.MarkLabel(wait);
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(done);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x1A);
      asm.Sub(Reg.DX, Reg.CX);
      asm.Cmp(Reg.DX, Reg.BX);
      asm.Jb(wait);
      asm.MarkLabel(done);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }
  }

  private void EmitMiscProcedures2(Assembler asm) {
    this.ScreenMode = asm.MarkLabel("rt_screenmode");
    {
      // AX = PB SCREEN number -> BIOS video mode (QB-compatible mapping)
      var set = asm.DefineLabel();
      void Map(int pb, int bios) {
        var next = asm.DefineLabel();
        asm.Cmp(Reg.AX, pb);
        asm.Jne(next);
        asm.Mov(Reg.AX, bios);
        asm.Jmp(set);
        asm.MarkLabel(next);
      }
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      Map(0, 0x03);
      Map(1, 0x04);
      Map(2, 0x06);
      Map(7, 0x0D);
      Map(8, 0x0E);
      Map(9, 0x10);
      Map(11, 0x11);
      Map(12, 0x12);
      Map(13, 0x13);
      asm.MarkLabel(set);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Int(0x10);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Spc = asm.MarkLabel("rt_spc");
    {
      // CX = space count (chunks of the 16-space constant)
      var loop = asm.DefineLabel();
      var done = asm.DefineLabel();
      var chunk = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.MarkLabel(loop);
      asm.Jcxz(done);
      asm.Mov(Reg.DX, Reg.CX);            // DX = remaining
      asm.Cmp(Reg.DX, 16);
      asm.Jbe(chunk);
      asm.Mov(Reg.DX, 16);
      asm.MarkLabel(chunk);
      asm.Sub(Reg.CX, Reg.DX);
      asm.Push(Reg.CX);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_spaces")));
      asm.Call(this.PrintStr);
      asm.Pop(Reg.CX);
      asm.Jmp(loop);
      asm.MarkLabel(done);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.Tab = asm.MarkLabel("rt_tab");
    {
      // CX = 1-based target column; spaces forward only
      var done = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.CX);
      asm.Dec(Reg.CX);
      asm.Push(Reg.BX);                              // TAB targets the ACTIVE column (screen or per-file)
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
      asm.Sub(Reg.CX, Mem.Word(Reg.BX));
      asm.Pop(Reg.BX);
      asm.Cmp(Reg.CX, (Imm)0);
      asm.Jle(done);
      asm.Call(this.Spc);
      asm.MarkLabel(done);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    this.UseFmt = asm.MarkLabel("rt_usefmt");
    {
      // DX:AX = scaled value, CH = field width (chars incl. point), CL = decimals.
      // Renders right-aligned fixed-point text and prints it.
      var positive = asm.DefineLabel();
      var digitLoop = asm.DefineLabel();
      var padZero = asm.DefineLabel();
      var zeroDone = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Mov(Mem.Word(asm.Lbl("rt_st0")), Reg.CX);   // CL=decimals, CH=width
      asm.Xor(Reg.DI, Reg.DI);                        // sign flag
      asm.Test(Reg.DX, Reg.DX);
      asm.Jns(positive);
      asm.Mov(Reg.DI, 1);
      asm.Not(Reg.DX);
      asm.Neg(Reg.AX);
      asm.Sbb(Reg.DX, -1);
      asm.MarkLabel(positive);
      asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 34));
      asm.Mov(Reg.CX, 10);
      asm.MarkLabel(digitLoop);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.AX, Reg.DX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Div(Reg.CX);
      asm.Xchg(Reg.AX, Reg.BX);
      asm.Div(Reg.CX);
      asm.Add(Reg.DX, '0');
      asm.Dec(Reg.SI);
      asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
      asm.Mov(Reg.DX, Reg.BX);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Or(Reg.BX, Reg.DX);
      asm.Jnz(digitLoop);
      // pad with zeros until count > decimals (so "0.05" renders fully)
      asm.MarkLabel(padZero);
      asm.Mov(Reg.AX, Imm.OffsetOf(this._numBuffer, 34));
      asm.Sub(Reg.AX, Reg.SI);                        // AX = digit count
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_st0")));
      asm.And(Reg.BX, 0x7F);                          // decimals (bit 7 = grouping flag)
      asm.Cmp(Reg.AX, Reg.BX);
      asm.Jg(zeroDone);
      asm.Dec(Reg.SI);
      asm.Mov(Mem.Byte(Reg.SI), '0');
      asm.Jmp(padZero);
      asm.MarkLabel(zeroDone);
      // AX = digit count, BX = decimals, SI = first digit, DI = sign
      asm.Call(asm.Lbl("rt_usefmt_out"));
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Ret();
    }

    asm.MarkLabel("rt_usefmt_out");
    {
      // print: left padding to width, '-', integer digits (with optional
      // thousands separators), '.', decimal digits.
      //   AX = digit count, BX = decimals, SI = first digit, DI = sign,
      //   rt_st0: CH = width, CL bit7 = grouping, CL bits0..6 = decimals
      var noSign = asm.DefineLabel();
      var noFrac = asm.DefineLabel();
      var noCommas = asm.DefineLabel();
      var plainInt = asm.DefineLabel();
      var intDone = asm.DefineLabel();

      asm.Mov(Mem.Word(asm.Lbl("rt_st1")), Reg.AX);   // total digits
      asm.Mov(Reg.DX, Reg.AX);
      asm.Sub(Reg.DX, Reg.BX);
      asm.Mov(Mem.Word(asm.Lbl("rt_st2")), Reg.DX);   // integer digits

      // comma count = grouping ? (intdigits - 1) / 3 : 0
      asm.Xor(Reg.CX, Reg.CX);
      asm.Test(Mem.Byte(asm.Lbl("rt_st0")), (Imm)0x80);
      asm.Jz(noCommas);
      asm.Mov(Reg.AX, Reg.DX);
      asm.Dec(Reg.AX);
      asm.Js(noCommas);
      asm.Push(Reg.BX);
      asm.Push(Reg.DX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Mov(Reg.BX, 3);
      asm.Div(Reg.BX);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.MarkLabel(noCommas);
      asm.Mov(Mem.Word(asm.Lbl("rt_st3")), Reg.CX);   // commas

      // printed length = digits + commas + sign + (decimals ? 1 : 0)
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_st1")));
      asm.Add(Reg.AX, Reg.CX);
      asm.Add(Reg.AX, Reg.DI);
      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(asm.Lbl("rt_uf_nopoint"));
      asm.Inc(Reg.AX);
      asm.MarkLabel("rt_uf_nopoint");

      // padding = width - printed length
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_st0")));
      asm.Mov(Reg.DL, Reg.DH);
      asm.Xor(Reg.DH, Reg.DH);
      asm.Sub(Reg.DX, Reg.AX);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Cmp(Reg.CX, (Imm)0);
      asm.Jle(asm.Lbl("rt_uf_sign"));
      asm.Call(this.Spc);
      asm.MarkLabel("rt_uf_sign");
      asm.Test(Reg.DI, Reg.DI);
      asm.Jz(noSign);
      asm.Push(Reg.SI);
      asm.Mov(Mem.Byte(this._scratch), '-');
      asm.Mov(Reg.SI, Imm.OffsetOf(this._scratch));
      asm.Mov(Reg.CX, 1);
      asm.Call(this.PrintStr);
      asm.Pop(Reg.SI);
      asm.MarkLabel(noSign);

      // integer digits
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_st2")));   // remaining integer digits
      asm.Cmp(Mem.Word(asm.Lbl("rt_st3")), (Imm)0);
      asm.Je(plainInt);
      // grouped: lead chunk = ((intdigits - 1) mod 3) + 1, then ",ddd" chunks
      asm.Push(Reg.BX);
      asm.Mov(Reg.AX, Reg.DX);
      asm.Dec(Reg.AX);
      asm.Push(Reg.DX);
      asm.Xor(Reg.DX, Reg.DX);
      asm.Mov(Reg.BX, 3);
      asm.Div(Reg.BX);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Inc(Reg.CX);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.MarkLabel("rt_uf_group");
      asm.Call(this.PrintStr);
      asm.Add(Reg.SI, Reg.CX);
      asm.Sub(Reg.DX, Reg.CX);
      asm.Jz(intDone);
      asm.Push(Reg.SI);
      asm.Mov(Mem.Byte(this._scratch), ',');
      asm.Mov(Reg.SI, Imm.OffsetOf(this._scratch));
      asm.Mov(Reg.CX, 1);
      asm.Call(this.PrintStr);
      asm.Pop(Reg.SI);
      asm.Mov(Reg.CX, 3);
      asm.Jmp(asm.Lbl("rt_uf_group"));

      asm.MarkLabel(plainInt);
      asm.Mov(Reg.CX, Reg.DX);
      asm.Call(this.PrintStr);
      asm.Add(Reg.SI, Reg.CX);
      asm.MarkLabel(intDone);

      asm.Test(Reg.BX, Reg.BX);
      asm.Jz(noFrac);
      asm.Push(Reg.SI);
      asm.Mov(Mem.Byte(this._scratch), '.');
      asm.Mov(Reg.SI, Imm.OffsetOf(this._scratch));
      asm.Mov(Reg.CX, 1);
      asm.Call(this.PrintStr);
      asm.Pop(Reg.SI);
      asm.Mov(Reg.CX, Reg.BX);
      asm.Call(this.PrintStr);
      asm.MarkLabel(noFrac);
      asm.Ret();
    }

    this.ReadData = asm.MarkLabel("rt_readdata");
    {
      var outOfData = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_dataptr")));
      asm.Cmp(Reg.BX, Imm.OffsetOf(asm.Lbl("rt_dataend")));
      asm.Jae(outOfData);
      asm.Mov(Reg.CX, Mem.Word(Reg.BX));
      asm.Lea(Reg.SI, Mem.At(Reg.BX, 2));
      asm.Add(Reg.BX, 2);
      asm.Add(Reg.BX, Reg.CX);
      asm.Mov(Mem.Word(asm.Lbl("rt_dataptr")), Reg.BX);
      asm.Mov(Reg.DX, Reg.DS);
      asm.Call(this.StrMem);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Ret();
      asm.MarkLabel(outOfData);
      asm.Mov(Reg.AX, 4);                  // PB error 4: out of data
      asm.Jmp(asm.Lbl("rt_raise"));
    }
  }

  private void EmitMiscData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_rndseed");
    asm.Dd(0x12345678u);
  }

  private void EmitMiscConstants(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_const_tickrate");
    asm.Dq(18.2065);
    asm.MarkLabel("rt_const_32768");
    asm.Dq(32768.0);
  }
}
