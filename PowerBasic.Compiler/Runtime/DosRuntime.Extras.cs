using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// PB 3.x surface helpers added with the dialect/pointer wave. Conventions
/// (registers not listed as outputs are preserved):
///   PrintInt64:  ST0 = integral value (popped) - prints PB-style "[ |-]digits[ ]"
///   StrI64:      ST0 = integral value (popped) -> AX=STR$ text
///   AsciizLoad:  DX=segment, SI=offset, CX=capacity -> AX=string handle (chars before NUL)
///   AsciizStore: AX=handle (consumed), DX=segment, DI=offset, CX=capacity
///                (copies min(len, capacity-1) bytes, always NUL-terminates)
///   AsciizLen:   DX=segment, SI=offset, CX=capacity -> AX=length before NUL
///   AscSet:      AX=handle (not consumed), CX=position (1-based), DL=code
///                (in-place byte poke; out-of-range positions are ignored)
///   RndRange:    DX:AX=lower, CX:BX=upper -> DX:AX = LONG in [lower, upper]
///   Randomize:   - reseeds rt_rndseed from the BIOS tick counter (RANDOMIZE with no argument)
/// </summary>
public sealed partial class DosRuntime {

  public Label PrintInt64 { get; private set; } = null!;
  public Label StrI64 { get; private set; } = null!;

  public Label AsciizLoad { get; private set; } = null!;
  public Label AsciizStore { get; private set; } = null!;
  public Label AsciizLen { get; private set; } = null!;
  public Label AscSet { get; private set; } = null!;
  public Label RndRange { get; private set; } = null!;
  public Label Randomize { get; private set; } = null!;

  private void EmitExtraProcedures(Assembler asm) {
    this.EmitPrintInt64(asm);
    this.EmitStrI64(asm);
    this.EmitAsciizProcedures(asm);
    this.EmitAscSet(asm);
    this.EmitRndRange(asm);
    this.EmitRandomize(asm);
  }

  /// <summary>ST0 (integral, popped) -> "[ |-]digits[ ]" on the current output (QUAD print).</summary>
  private void EmitPrintInt64(Assembler asm) {
    this.PrintInt64 = asm.MarkLabel("rt_print_i64");
    var positive = asm.DefineLabel();
    var digitLoop = asm.DefineLabel();
    var noSign = asm.DefineLabel();
    var output = asm.DefineLabel();

    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Fistp(Mem.Qword(this._scratch));

    // SI walks backwards from the end of the number buffer; trailing space first
    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 31));
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    // DI = sign flag; negate the 4-word value when negative
    asm.Xor(Reg.DI, Reg.DI);
    asm.Mov(Reg.AX, Mem.Word(this._scratch, 6));
    asm.Test(Reg.AX, Reg.AX);
    asm.Jns(positive);
    asm.Mov(Reg.DI, 1);
    asm.Not(Mem.Word(this._scratch, 0));
    asm.Not(Mem.Word(this._scratch, 2));
    asm.Not(Mem.Word(this._scratch, 4));
    asm.Not(Mem.Word(this._scratch, 6));
    asm.Add(Mem.Word(this._scratch, 0), (Imm)1);
    asm.Adc(Mem.Word(this._scratch, 2), (Imm)0);
    asm.Adc(Mem.Word(this._scratch, 4), (Imm)0);
    asm.Adc(Mem.Word(this._scratch, 6), (Imm)0);
    asm.MarkLabel(positive);

    asm.Mov(Reg.CX, 10);
    asm.MarkLabel(digitLoop);
    // divide the 4-word value by 10 (high word first), remainder = next digit
    asm.Xor(Reg.DX, Reg.DX);
    foreach (var offset in new[] { 6, 4, 2, 0 }) {
      asm.Mov(Reg.AX, Mem.Word(this._scratch, offset));
      asm.Div(Reg.CX);
      asm.Mov(Mem.Word(this._scratch, offset), Reg.AX);
    }
    asm.Add(Reg.DX, '0');
    asm.Dec(Reg.SI);
    asm.Mov(Mem.Byte(Reg.SI), Reg.DL);
    asm.Mov(Reg.AX, Mem.Word(this._scratch, 0));
    asm.Or(Reg.AX, Mem.Word(this._scratch, 2));
    asm.Or(Reg.AX, Mem.Word(this._scratch, 4));
    asm.Or(Reg.AX, Mem.Word(this._scratch, 6));
    asm.Jnz(digitLoop);

    asm.Dec(Reg.SI);
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(output);
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    asm.MarkLabel(output);
    asm.Mov(Reg.CX, Imm.OffsetOf(this._numBuffer, 32));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);

    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>STR$ of a QUAD: capture-mode wrapper around <see cref="PrintInt64"/>.</summary>
  private void EmitStrI64(Assembler asm) {
    this.StrI64 = asm.MarkLabel("rt_str_i64");
    asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
    asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
    asm.Call(this.PrintInt64);
    asm.Jmp(asm.Lbl("rt_str_cap")); // strips the trailing space, returns the handle in AX
  }

  private void EmitAsciizProcedures(Assembler asm) {
    // AsciizLen: DX=segment, SI=offset, CX=capacity -> AX=chars before NUL
    this.AsciizLen = asm.MarkLabel("rt_az_len");
    {
      var scan = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.Push(Reg.BX);
      asm.Push(Reg.SI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.ES, Reg.DX);
      asm.Xor(Reg.BX, Reg.BX);
      asm.MarkLabel(scan);
      asm.Cmp(Reg.BX, Reg.CX);
      asm.Jae(done);
      asm.Cmp(Mem.Byte(Reg.SI).Es(), (Imm)0);
      asm.Je(done);
      asm.Inc(Reg.SI);
      asm.Inc(Reg.BX);
      asm.Jmp(scan);
      asm.MarkLabel(done);
      asm.Mov(Reg.AX, Reg.BX);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.BX);
      asm.Ret();
    }

    // AsciizLoad: DX=segment, SI=offset, CX=capacity -> AX=string handle
    this.AsciizLoad = asm.MarkLabel("rt_az_load");
    {
      asm.Push(Reg.CX);
      asm.Call(this.AsciizLen);
      asm.Mov(Reg.CX, Reg.AX);
      asm.Call(this.StrMem); // DX=segment, SI=offset, CX=length -> AX
      asm.Pop(Reg.CX);
      asm.Ret();
    }

    // AsciizStore: AX=handle (consumed), DX=segment, DI=offset, CX=capacity
    this.AsciizStore = asm.MarkLabel("rt_az_store");
    {
      var lengthOk = asm.DefineLabel();
      var copyDone = asm.DefineLabel();
      var empty = asm.DefineLabel();
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);

      // source data offset/length from the descriptor table
      asm.Xor(Reg.SI, Reg.SI);
      asm.Xor(Reg.BX, Reg.BX);
      asm.Test(Reg.AX, Reg.AX);
      asm.Jz(empty);
      asm.Mov(Reg.BX, Reg.AX);
      asm.Shl(Reg.BX, 2);
      asm.Mov(Reg.SI, this.Descriptor(Reg.BX));      // heap offset
      asm.Mov(Reg.BX, this.Descriptor(Reg.BX, 2));   // length
      asm.MarkLabel(empty);

      asm.Dec(Reg.CX);                               // capacity-1 = max chars
      asm.Cmp(Reg.BX, Reg.CX);
      asm.Jbe(lengthOk);
      asm.Mov(Reg.BX, Reg.CX);
      asm.MarkLabel(lengthOk);

      // copy BX bytes heap -> DX:DI, then NUL-terminate
      asm.Mov(Reg.CX, Reg.BX);
      asm.Mov(Reg.ES, Reg.DX);
      asm.Push(Reg.DS);
      asm.Mov(Reg.DS, Mem.Word(asm.Lbl("rt_strseg")));
      asm.Jcxz(copyDone);
      asm.Rep();
      asm.Movsb();
      asm.MarkLabel(copyDone);
      asm.Pop(Reg.DS);
      asm.Mov(Mem.Byte(Reg.DI).Es(), (Imm)0);

      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Call(this.StrFree); // consumes the handle
      asm.Ret();
    }
  }

  /// <summary>ASC(s$, n) = code - pokes one byte of a dynamic string in place.</summary>
  private void EmitAscSet(Assembler asm) {
    this.AscSet = asm.MarkLabel("rt_ascset");
    var done = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.SI);
    asm.Push(Reg.ES);
    asm.Test(Reg.AX, Reg.AX);
    asm.Jz(done);
    asm.Jcxz(done);
    asm.Mov(Reg.BX, Reg.AX);
    asm.Shl(Reg.BX, 2);
    asm.Mov(Reg.SI, this.Descriptor(Reg.BX));        // heap offset
    asm.Mov(Reg.AX, this.Descriptor(Reg.BX, 2));     // length
    asm.Cmp(Reg.CX, Reg.AX);
    asm.Ja(done);                                    // position past the end - ignored
    asm.Add(Reg.SI, Reg.CX);
    asm.Dec(Reg.SI);
    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_strseg")));
    asm.Mov(Mem.Byte(Reg.SI).Es(), Reg.DL);
    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  /// <summary>RND(a, z): DX:AX=lower, CX:BX=upper -> DX:AX = lower + trunc(rnd * (upper-lower+1)).</summary>
  private void EmitRndRange(Assembler asm) {
    this.RndRange = asm.MarkLabel("rt_rndrange");
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);

    // span = upper - lower + 1 -> CX:BX
    asm.Sub(Reg.BX, Reg.AX);
    asm.Sbb(Reg.CX, Reg.DX);
    asm.Add(Reg.BX, (Imm)1);
    asm.Adc(Reg.CX, (Imm)0);

    asm.Push(Reg.DX);
    asm.Push(Reg.AX);
    asm.Mov(Mem.Word(this._scratch), Reg.BX);
    asm.Mov(Mem.Word(this._scratch, 2), Reg.CX);
    asm.Fild(Mem.Dword(this._scratch));
    asm.Call(this.Rnd);                              // ST0=rnd [0,1), ST1=span
    asm.Fmulp();
    asm.Call(this.Trunc);
    asm.Fistp(Mem.Dword(this._scratch));
    asm.Pop(Reg.AX);
    asm.Pop(Reg.DX);
    asm.Add(Reg.AX, Mem.Word(this._scratch));
    asm.Adc(Reg.DX, Mem.Word(this._scratch, 2));

    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
  }

  /// <summary>
  /// <c>RANDOMIZE</c> with no argument: the BIOS tick counter becomes the new seed, low word first,
  /// which is the LONG the counter already is.
  ///
  /// <para>
  /// It is a routine rather than four inline instructions because both back ends need it and there is
  /// exactly one seed. The direct emitter used to write the sequence inline and the routed path had no
  /// way to spell an <c>INT 1Ah</c> at all; giving each its own copy would be two statements of the
  /// same fact, and the seeded form - a plain store of a LONG into <c>rt_rndseed</c> - is deliberately
  /// NOT routed through here, because that one really is the same instruction on both paths.
  /// </para>
  /// </summary>
  private void EmitRandomize(Assembler asm) {
    this.Randomize = asm.MarkLabel("rt_randomize");
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Int(0x1A);                                   // CX:DX = ticks since midnight
    asm.Mov(Mem.Word(asm.Lbl("rt_rndseed")), Reg.DX);
    asm.Mov(Mem.Word(asm.Lbl("rt_rndseed"), 2), Reg.CX);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }
}
