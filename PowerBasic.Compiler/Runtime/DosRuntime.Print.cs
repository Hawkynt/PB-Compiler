using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private void EmitPrintStr(Assembler asm) {
    this.PrintStr = asm.MarkLabel("rt_print_str");
    var done = asm.DefineLabel("rt_print_str_done");
    var capture = asm.DefineLabel("rt_print_str_cap");
    asm.Or(Reg.CX, Reg.CX);
    asm.Jz(done);
    asm.Cmp(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
    asm.Jne(capture);
    var dos = asm.DefineLabel("rt_print_str_dos");
    var graphicsReady = asm.DefineLabel("rt_print_str_graphics");
    var graphicsProbeDone = asm.DefineLabel();
    var graphicsFound = asm.DefineLabel();
    var graphicsProbeFail = asm.DefineLabel();
    var graphicsBios = asm.DefineLabel("rt_print_str_gbios");

    // Graphics-mode console output is special even without $OPTION VIDEO: QuickBASIC's current
    // foreground affects PRINT, while a redirected/file handle must still receive bytes through DOS.
    // A plain "character device" test is insufficient (NUL/AUX/PRN exist), so require the standard
    // input+output bits and exclude NUL before treating handle 1 as the console-like device.
    asm.Cmp(Mem.Word(asm.Lbl("rt_curout")), 1);
    asm.Jne(graphicsProbeDone);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Cmp(Reg.AL, (Imm)0x12);
    asm.Je(graphicsFound);
    asm.Cmp(Reg.AL, (Imm)0x13);
    asm.Jne(graphicsProbeFail);
    asm.MarkLabel(graphicsFound);
    asm.Mov(Mem.Byte(asm.Lbl("rt_gprint_mode")), Reg.AL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_gprint_columns")), Reg.AH);
    asm.Mov(Mem.Byte(asm.Lbl("rt_gprint_page")), Reg.BH);
    asm.Mov(Reg.AX, 0x4400);             // DOS IOCTL get device information for stdout.
    asm.Mov(Reg.BX, 1);
    asm.Int(0x21);
    asm.Jc(graphicsProbeFail);
    asm.Mov(Reg.AX, Reg.DX);
    asm.And(Reg.AX, (Imm)0x83);          // character device + standard input + standard output.
    asm.Cmp(Reg.AX, (Imm)0x83);
    asm.Jne(graphicsProbeFail);
    asm.Test(Reg.DX, (Imm)0x04);         // NUL has its own device-attribute bit.
    asm.Jnz(graphicsProbeFail);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Jmp(graphicsReady);
    asm.MarkLabel(graphicsProbeFail);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.MarkLabel(graphicsProbeDone);

    if (this.EnableFastVideo) {
      // Preserve the existing $OPTION VIDEO text-mode contract: Mode 3 goes straight to B800h.
      // This is deliberately separate from the graphics CON proof above.
      var scanFail = asm.DefineLabel();
      var scan = asm.DefineLabel();
      var blit = asm.DefineLabel();
      var textMode = asm.DefineLabel();
      var textRejected = asm.DefineLabel();
      asm.Cmp(Mem.Word(asm.Lbl("rt_curout")), 1);
      asm.Jne(dos);
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Mov(Reg.AH, (Imm)0x0F);
      asm.Int(0x10);
      asm.Cmp(Reg.AL, (Imm)3);
      asm.Je(textMode);
      asm.MarkLabel(textRejected);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(dos);
      asm.MarkLabel(textMode);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Cmp(Reg.CX, 80);
      asm.Ja(dos);
      asm.Push(Reg.SI);
      asm.Push(Reg.CX);
      asm.MarkLabel(scan);
      asm.Lodsb();
      asm.Cmp(Reg.AL, 0x20);
      asm.Jb(scanFail);
      asm.Loop(scan);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);

      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.DX);
      asm.Push(Reg.DI);
      asm.Push(Reg.ES);
      asm.Mov(Reg.AX, 0x0040);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Mov(Reg.DX, Mem.Word(0x50).Seg(Reg.ES));
      asm.Mov(Reg.AL, Reg.DL);
      asm.Add(Reg.AL, Reg.CL);
      asm.Cmp(Reg.AL, 80);
      asm.Ja(asm.Lbl("rt_fv_unfit"));
      asm.Mov(Reg.AL, Reg.DH);
      asm.Mov(Reg.AH, 80);
      asm.Mul(Reg.AH);
      asm.Mov(Reg.BL, Reg.DL);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Add(Reg.AX, Reg.BX);
      asm.Shl(Reg.AX, 1);
      asm.Mov(Reg.DI, Reg.AX);
      asm.Mov(Reg.AX, 0xB800);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Push(Reg.CX);
      asm.Push(Reg.SI);
      asm.MarkLabel(blit);
      asm.Lodsb();
      asm.Mov(Reg.AH, 0x07);
      asm.Stosw();
      asm.Loop(blit);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.CX);
      asm.Add(Reg.DL, Reg.CL);
      asm.Mov(Reg.AH, 0x02);
      asm.Mov(Reg.BH, (Imm)0);
      asm.Int(0x10);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(asm.Lbl("rt_fv_advance"));

      asm.MarkLabel("rt_fv_unfit");
      asm.Pop(Reg.ES);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(dos);

      asm.MarkLabel(scanFail);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);
      asm.Jmp(dos);
    }

    // Any ordinary/non-graphics path that reaches here remains the byte-exact DOS handle path.
    asm.Jmp(dos);
    asm.MarkLabel(graphicsReady);

    // Record the original run once. The BIOS fallback and the direct kernel both preserve SI/CX.
    asm.Mov(Mem.Word(asm.Lbl("rt_gprint_src")), Reg.SI);
    asm.Mov(Mem.Word(asm.Lbl("rt_gprint_count")), Reg.CX);

    if (this.EnableFastVideo) {
      var graphicsScan = asm.DefineLabel();
      var graphicsScanFail = asm.DefineLabel();
      var graphicsDirect = asm.DefineLabel("rt_print_str_gdirect");
      var graphicsDirectFail = asm.DefineLabel();
      var clearRows = asm.DefineLabel();
      var glyphLoop = asm.DefineLabel();
      var glyphRows = asm.DefineLabel();

      // The planar kernel is deliberately narrow: SCREEN 12, page zero, 80 columns, valid
      // four-bit foreground, printable 7-bit bytes, and no wrap/scroll. Everything else goes
      // through BIOS teletype after the positive console proof above.
      asm.Cmp(Mem.Byte(asm.Lbl("rt_gprint_mode")), (Imm)0x12);
      asm.Jne(graphicsBios);
      asm.Cmp(Mem.Byte(asm.Lbl("rt_gprint_columns")), (Imm)80);
      asm.Jne(graphicsBios);
      asm.Cmp(Mem.Byte(asm.Lbl("rt_gprint_page")), (Imm)0);
      asm.Jne(graphicsBios);
      asm.Cmp(Mem.Byte(asm.Lbl("rt_fgcolor")), (Imm)15);
      asm.Ja(graphicsBios);
      asm.Cmp(Reg.CX, (Imm)80);
      asm.Jae(graphicsBios);
      asm.Push(Reg.SI);
      asm.Push(Reg.CX);
      asm.MarkLabel(graphicsScan);
      asm.Lodsb();
      asm.Cmp(Reg.AL, (Imm)0x20);
      asm.Jb(graphicsScanFail);
      asm.Cmp(Reg.AL, (Imm)0x7E);
      asm.Ja(graphicsScanFail);
      asm.Loop(graphicsScan);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);

      // Query the actual text cursor and current graphics-mode font. BH=1 returns the INT 43h
      // graphics font pointer, while CX is its current character height and DL its last text row.
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.BP);
      asm.Push(Reg.ES);
      asm.Xor(Reg.BH, Reg.BH);
      asm.Mov(Reg.AH, (Imm)0x03);
      asm.Int(0x10);
      asm.Mov(Mem.Word(asm.Lbl("rt_gprint_cursor")), Reg.DX);

      asm.Xor(Reg.AX, Reg.AX);
      asm.Mov(Reg.AL, Reg.DL);
      asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_count")));
      asm.Cmp(Reg.AX, (Imm)80);
      asm.Jae(graphicsDirectFail);       // final cell would wrap, which can scroll.

      asm.Mov(Reg.AX, 0x1130);
      asm.Mov(Reg.BH, (Imm)1);
      asm.Int(0x10);
      asm.Or(Reg.CX, Reg.CX);
      asm.Jz(graphicsDirectFail);
      asm.Cmp(Reg.CX, (Imm)32);          // defensive VGA-font bound; normal modes use 8/14/16.
      asm.Ja(graphicsDirectFail);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_cursor")));
      asm.Cmp(Reg.AH, Reg.DL);
      asm.Ja(graphicsDirectFail);
      asm.Mov(Mem.Word(asm.Lbl("rt_gprint_height")), Reg.CX);
      asm.Mov(Mem.Word(asm.Lbl("rt_gprint_fontofs")), Reg.BP);
      asm.Mov(Reg.AX, Reg.ES);
      asm.Mov(Mem.Word(asm.Lbl("rt_gprint_fontseg")), Reg.AX);

      // A SCREEN 12 cell is exactly one VGA byte wide. Byte offset =
      // cursorRow * fontHeight * 80 + cursorColumn.
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_cursor")));
      asm.Mov(Reg.AL, Reg.AH);
      asm.Xor(Reg.AH, Reg.AH);
      asm.Mul(Reg.CX);
      asm.Mov(Reg.BX, 80);
      asm.Mul(Reg.BX);
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gprint_cursor")));
      asm.Xor(Reg.BX, Reg.BX);
      asm.Mov(Reg.BL, Reg.DL);
      asm.Add(Reg.AX, Reg.BX);
      asm.Mov(Mem.Word(asm.Lbl("rt_gprint_dest")), Reg.AX);

      asm.Pop(Reg.ES);
      asm.Pop(Reg.BP);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      EmitCertifiedPlanarBranch(asm, graphicsDirect);
      asm.Jmp(graphicsBios);

      asm.MarkLabel(graphicsDirectFail);
      asm.Pop(Reg.ES);
      asm.Pop(Reg.BP);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(graphicsBios);

      asm.MarkLabel(graphicsScanFail);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.SI);
      asm.Jmp(graphicsBios);

      asm.MarkLabel(graphicsDirect);
      asm.Push(Reg.AX);
      asm.Push(Reg.BX);
      asm.Push(Reg.CX);
      asm.Push(Reg.DX);
      asm.Push(Reg.SI);
      asm.Push(Reg.DI);
      asm.Push(Reg.BP);
      asm.Push(Reg.DS);
      asm.Push(Reg.ES);

      // Save selectors and every VGA register touched. Write Mode 3 is ideal for monochrome
      // glyph masks: CPU data becomes the bit mask while Set/Reset supplies all four color planes.
      asm.Mov(Reg.DX, 0x3CE);
      asm.In(Reg.AL, Reg.DX);
      asm.Push(Reg.AX);
      asm.Mov(Reg.DX, 0x3C4);
      asm.In(Reg.AL, Reg.DX);
      asm.Push(Reg.AX);
      SaveAndSet(asm, 0x3CE, 5, -2);     // Write Mode 3, preserve read mode.
      SaveAndSet(asm, 0x3CE, 3, 0);      // no rotate
      SaveAndSet(asm, 0x3CE, 8, 255);    // CPU byte is the whole glyph mask
      SaveAndSet(asm, 0x3CE, 0, 0);      // first pass: background attribute 0
      SaveAndSet(asm, 0x3C4, 2, 15);     // all four planes together

      asm.Mov(Reg.AX, 0xA000);
      asm.Mov(Reg.ES, Reg.AX);
      asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gprint_dest")));
      asm.Mov(Reg.BP, Mem.Word(asm.Lbl("rt_gprint_height")));
      asm.Cld();

      // Normal PRINT is opaque. Clear every glyph cell to attribute 0 first, one contiguous
      // run per scanline, rather than accidentally implementing transparent text.
      asm.MarkLabel(clearRows);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gprint_count")));
      asm.Mov(Reg.CX, Reg.BX);
      asm.Mov(Reg.AL, (Imm)0xFF);
      asm.Rep();
      asm.Stosb();
      asm.Add(Reg.DI, (Imm)80);
      asm.Sub(Reg.DI, Reg.BX);
      asm.Dec(Reg.BP);
      asm.Jnz(clearRows);

      // One controller write changes Set/Reset from background 0 to the current foreground.
      asm.Mov(Reg.DX, 0x3CE);
      asm.Mov(Reg.AL, (Imm)0);
      asm.Out(Reg.DX, Reg.AL);
      asm.Inc(Reg.DX);
      asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_fgcolor")));
      asm.Out(Reg.DX, Reg.AL);

      asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gprint_src")));
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_gprint_count")));
      asm.MarkLabel(glyphLoop);
      asm.Lodsb();
      asm.Xor(Reg.AH, Reg.AH);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gprint_height")));
      asm.Mul(Reg.BX);
      asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_fontofs")));
      asm.Mov(Reg.BX, Reg.AX);
      asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gprint_dest")));
      asm.Mov(Reg.BP, Mem.Word(asm.Lbl("rt_gprint_height")));
      asm.Push(Reg.DS);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_fontseg")));
      asm.Mov(Reg.DS, Reg.AX);
      asm.MarkLabel(glyphRows);
      asm.Mov(Reg.AL, Mem.Byte(Reg.BX));
      asm.Mov(Reg.AH, Mem.Byte(Reg.DI).Seg(Reg.ES)); // load zero background into all four latches
      asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.AL);
      asm.Inc(Reg.BX);
      asm.Add(Reg.DI, (Imm)80);
      asm.Dec(Reg.BP);
      asm.Jnz(glyphRows);
      asm.Pop(Reg.DS);
      asm.Inc(Mem.Word(asm.Lbl("rt_gprint_dest")));
      asm.Loop(glyphLoop);

      Restore(asm, 0x3C4, 2);
      Restore(asm, 0x3CE, 0);
      Restore(asm, 0x3CE, 8);
      Restore(asm, 0x3CE, 3);
      Restore(asm, 0x3CE, 5);
      asm.Pop(Reg.BX);                    // original sequencer selector
      asm.Mov(Reg.DX, 0x3C4);
      asm.Mov(Reg.AL, Reg.BL);
      asm.Out(Reg.DX, Reg.AL);
      asm.Pop(Reg.BX);                    // original graphics-controller selector
      asm.Mov(Reg.DX, 0x3CE);
      asm.Mov(Reg.AL, Reg.BL);
      asm.Out(Reg.DX, Reg.AL);

      // Keep BIOS cursor state exactly in sync with the direct framebuffer write.
      asm.Mov(Reg.DX, Mem.Word(asm.Lbl("rt_gprint_cursor")));
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gprint_count")));
      asm.Add(Reg.DL, Reg.AL);
      asm.Mov(Reg.AH, (Imm)0x02);
      asm.Mov(Reg.BH, Mem.Byte(asm.Lbl("rt_gprint_page")));
      asm.Int(0x10);

      asm.Pop(Reg.ES);
      asm.Pop(Reg.DS);
      asm.Pop(Reg.BP);
      asm.Pop(Reg.DI);
      asm.Pop(Reg.SI);
      asm.Pop(Reg.DX);
      asm.Pop(Reg.CX);
      asm.Pop(Reg.BX);
      asm.Pop(Reg.AX);
      asm.Jmp(asm.Lbl("rt_fv_advance"));
    }

    // Semantic fallback for a proven graphics console: BIOS teletype owns control characters,
    // wrapping and scrolling, with BL carrying the QuickBASIC foreground. Redirection never enters.
    asm.MarkLabel(graphicsBios);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.BL, Mem.Byte(asm.Lbl("rt_fgcolor")));
    asm.Mov(Reg.BH, Mem.Byte(asm.Lbl("rt_gprint_page")));
    var graphicsBiosLoop = asm.DefineLabel();
    asm.MarkLabel(graphicsBiosLoop);
    asm.Lodsb();
    asm.Mov(Reg.AH, (Imm)0x0E);
    asm.Int(0x10);
    asm.Dec(Mem.Word(asm.Lbl("rt_gprint_count")));
    asm.Jnz(graphicsBiosLoop);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Jmp(asm.Lbl("rt_fv_advance"));

    asm.MarkLabel(dos);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.DX, Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_curout")));
    asm.Mov(Reg.AH, 0x40);
    asm.Int(0x21);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.MarkLabel("rt_fv_advance");
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Add(Mem.Word(Reg.BX), Reg.CX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
    asm.Ret();

    // Capture mode (STR$): rt_capbuf lives in DS. Temporarily mirror DS into ES so the same
    // target-aware copy kernel used by strings/memory can handle the run, then restore caller ES.
    asm.MarkLabel(capture);
    asm.Push(Reg.AX);
    asm.Push(Reg.CX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_caplen")));
    asm.Add(Mem.Word(asm.Lbl("rt_caplen")), Reg.CX);
    asm.Lea(Reg.DI, Mem.At(Reg.DI, asm.Lbl("rt_capbuf")));
    asm.Push(Reg.DS);
    asm.Pop(Reg.ES);
    this.EmitRepMovsbWidened(asm);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitPrintNewLine(Assembler asm) {
    this.PrintNewLine = asm.MarkLabel("rt_print_nl");
    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.AX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_crlf")));
    asm.Mov(Reg.CX, 2);
    asm.Call(this.PrintStr);
    asm.Push(Reg.BX);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Mov(Mem.Word(Reg.BX), (Imm)0);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }

  private void EmitPrintZone(Assembler asm) {
    this.PrintZone = asm.MarkLabel("rt_print_zone");
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_colptr")));
    asm.Mov(Reg.AX, Mem.Word(Reg.BX));
    asm.Xor(Reg.DX, Reg.DX);
    asm.Mov(Reg.BX, 14);
    asm.Div(Reg.BX);
    asm.Mov(Reg.CX, 14);
    asm.Sub(Reg.CX, Reg.DX);
    asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_spaces")));
    asm.Call(this.PrintStr);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
  }

  private void EmitPrintInt16(Assembler asm) {
    this.PrintInt16 = asm.MarkLabel("rt_print_i16");
    asm.Push(Reg.DX);
    asm.Cwd();
    asm.Call(asm.Lbl("rt_print_i32"));
    asm.Pop(Reg.DX);
    asm.Ret();
  }

  private void EmitPrintInt32(Assembler asm) {
    this.PrintInt32 = asm.MarkLabel("rt_print_i32");
    var convert = asm.DefineLabel();
    var digitLoop = asm.DefineLabel();
    var positive = asm.DefineLabel();

    asm.Push(Reg.SI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Push(Reg.DI);
    asm.Push(Reg.AX);
    asm.Push(Reg.DX);

    asm.Mov(Reg.SI, Imm.OffsetOf(this._numBuffer, 31));
    asm.Mov(Mem.Byte(Reg.SI), ' ');
    asm.Xor(Reg.DI, Reg.DI);
    asm.Test(Reg.DX, Reg.DX);
    asm.Jns(positive);
    asm.Mov(Reg.DI, 1);
    asm.Not(Reg.DX);
    asm.Not(Reg.AX);
    asm.Add(Reg.AX, 1);
    asm.Adc(Reg.DX, (Imm)0);
    asm.MarkLabel(positive);

    asm.MarkLabel(convert);
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
    var noSign = asm.DefineLabel();
    asm.Dec(Reg.SI);
    asm.Test(Reg.DI, Reg.DI);
    asm.Jz(noSign);
    asm.Mov(Mem.Byte(Reg.SI), '-');
    asm.Jmp(asm.Lbl("rt_print_i32_out"));
    asm.MarkLabel(noSign);
    asm.Mov(Mem.Byte(Reg.SI), ' ');

    asm.MarkLabel("rt_print_i32_out");
    asm.Mov(Reg.CX, Imm.OffsetOf(this._numBuffer, 32));
    asm.Sub(Reg.CX, Reg.SI);
    asm.Call(this.PrintStr);

    asm.Pop(Reg.DX);
    asm.Pop(Reg.AX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.SI);
    asm.Ret();
  }
}
