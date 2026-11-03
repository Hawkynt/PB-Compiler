using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  /// <summary>
  /// Mode 12h GET uses the QuickBASIC four-plane image representation:
  /// [width in bits:word][height:word], then, for each scanline, one
  /// left-aligned bitstream per plane. A single plane occupies ceil(width/8)
  /// bytes per scanline, including zeroed unused low bits in the last byte.
  /// This behavior applies with or without compiler optimizations.
  /// </summary>
  private void EmitPlanarGet(Assembler asm, Label entry) {
    var xOrdered = asm.DefineLabel();
    var yOrdered = asm.DefineLabel();
    var row = asm.DefineLabel();
    var planeLoop = asm.DefineLabel();
    var block = asm.DefineLabel();
    var bitLoop = asm.DefineLabel();
    var noBit = asm.DefineLabel();
    var endByte = asm.DefineLabel();
    var nextPlane = asm.DefineLabel();
    var nextRow = asm.DefineLabel();
    var done = asm.DefineLabel();
    var fastAligned = this.EnableTargetOptimizations ? asm.DefineLabel() : null;

    asm.MarkLabel(entry);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Cld();

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gx2")));
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Jle(xOrdered);
    asm.Xchg(Reg.AX, Reg.BX);
    asm.MarkLabel(xOrdered);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx1")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gbx2")), Reg.BX);

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gy2")));
    asm.Cmp(Reg.AX, Reg.BX);
    asm.Jle(yOrdered);
    asm.Xchg(Reg.AX, Reg.BX);
    asm.MarkLabel(yOrdered);
    asm.Mov(Mem.Word(asm.Lbl("rt_gby1")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gby2")), Reg.BX);

    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_gbufseg")));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gbufofs")));
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gbx2")));
    asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
    asm.Inc(Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gpw")), Reg.AX);
    asm.Stosw(); // In SCREEN 12 each plane has ONE bit per pixel.
    asm.Add(Reg.AX, (Imm)7);
    asm.Shr(Reg.AX, 3);
    asm.Mov(Mem.Word(asm.Lbl("rt_pg_stride")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gby2")));
    asm.Sub(Reg.AX, Mem.Word(asm.Lbl("rt_gby1")));
    asm.Inc(Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_gph")), Reg.AX);
    asm.Stosw();

    if (fastAligned is not null) {
      var slow = asm.DefineLabel();
      // Complete 8-pixel bytes can be transferred directly from each
      // hardware plane to the already packed QuickBASIC destination row.
      // No masked edge/bit-shift combination is required.
      asm.Cmp(Mem.Word(asm.Lbl("rt_gbx1")), (Imm)0);
      asm.Jl(slow);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gbx2")), (Imm)639);
      asm.Jg(slow);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gby1")), (Imm)0);
      asm.Jl(slow);
      asm.Cmp(Mem.Word(asm.Lbl("rt_gby2")), (Imm)479);
      asm.Jg(slow);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
      asm.And(Reg.AX, (Imm)7);
      asm.Jnz(slow);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gpw")));
      asm.And(Reg.AX, (Imm)7);
      asm.Jnz(slow);
      EmitCertifiedPlanarBranch(asm, fastAligned);
      asm.MarkLabel(slow);
    }

    asm.Mov(Reg.SI, Mem.Word(asm.Lbl("rt_gby1")));
    asm.MarkLabel(row);
    asm.Cmp(Reg.SI, Mem.Word(asm.Lbl("rt_gby2")));
    asm.Jg(done);
    asm.Mov(Mem.Word(asm.Lbl("rt_pg_plane")), (Imm)0);
    asm.MarkLabel(planeLoop);
    asm.Cmp(Mem.Word(asm.Lbl("rt_pg_plane")), (Imm)4);
    asm.Jge(nextRow);
    asm.Mov(Mem.Word(asm.Lbl("rt_pg_block")), (Imm)0);

    asm.MarkLabel(block);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pg_block")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_pg_stride")));
    asm.Jge(nextPlane);
    asm.Mov(Mem.Byte(asm.Lbl("rt_pg_acc")), (Imm)0);
    asm.Mov(Mem.Word(asm.Lbl("rt_pg_bit")), (Imm)0);

    asm.MarkLabel(bitLoop);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pg_block")));
    asm.Shl(Reg.AX, 3);
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_pg_bit")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gpw")));
    asm.Jge(endByte);
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gbx1")));
    asm.Mov(Reg.BX, Reg.SI);
    asm.Call(this.Point);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pg_plane")));
    asm.Mov(Reg.CL, Reg.BL);
    asm.Mov(Reg.DL, (Imm)1);
    asm.Shl(Reg.DL, Reg.CL);
    asm.Test(Reg.AL, Reg.DL);
    asm.Jz(noBit);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_pg_bit")));
    asm.Mov(Reg.AL, (Imm)0x80);
    asm.Shr(Reg.AL, Reg.CL);
    asm.Or(Mem.Byte(asm.Lbl("rt_pg_acc")), Reg.AL);
    asm.MarkLabel(noBit);
    asm.Inc(Mem.Word(asm.Lbl("rt_pg_bit")));
    asm.Cmp(Mem.Word(asm.Lbl("rt_pg_bit")), (Imm)8);
    asm.Jl(bitLoop);

    asm.MarkLabel(endByte);
    asm.Mov(Reg.AL, Mem.Byte(asm.Lbl("rt_pg_acc")));
    asm.Stosb();
    asm.Inc(Mem.Word(asm.Lbl("rt_pg_block")));
    asm.Jmp(block);

    asm.MarkLabel(nextPlane);
    asm.Inc(Mem.Word(asm.Lbl("rt_pg_plane")));
    asm.Jmp(planeLoop);
    asm.MarkLabel(nextRow);
    asm.Inc(Reg.SI);
    asm.Jmp(row);

    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    if (fastAligned is not null) {
      var fastRow = asm.DefineLabel();
      var fastPlane = asm.DefineLabel();
      var fastByte = asm.DefineLabel();

      asm.MarkLabel(fastAligned);
      asm.Push(Reg.BP);
      asm.Mov(Reg.BP, Reg.SP);
      asm.Sub(Reg.SP, (Imm)6); // SS locals: row base, rows remaining, byte width.
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gph")));
      asm.Mov(Mem.Word(Reg.BP, -4), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pg_stride")));
      asm.Mov(Mem.Word(Reg.BP, -6), Reg.AX);
      asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_gby1")));
      asm.Mov(Reg.BX, 80);
      asm.Mul(Reg.BX);
      asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_gbx1")));
      asm.Shr(Reg.BX, 3);
      asm.Add(Reg.AX, Reg.BX);
      asm.Mov(Mem.Word(Reg.BP, -2), Reg.AX);

      // In 8086 real mode, use DS=A000 for planar reads and ES=the BASIC
      // array for STOSB. Every runtime global is consumed before changing
      // DS; BP-based locals reside in SS and remain accessible throughout.
      asm.Push(Reg.DS);
      asm.Mov(Reg.DX, 0x3CE);
      asm.In(Reg.AL, Reg.DX);
      asm.Push(Reg.AX);
      SaveAndSet(asm, 0x3CE, 5, -5); // Read mode 0, preserve write mode.
      SaveAndSet(asm, 0x3CE, 4, 0);  // First read map.
      asm.Mov(Reg.AX, 0xA000);
      asm.Mov(Reg.DS, Reg.AX);
      asm.Mov(Reg.DX, 0x3CE);
      asm.Mov(Reg.AL, (Imm)4);
      asm.Out(Reg.DX, Reg.AL);
      asm.Inc(Reg.DX);

      asm.MarkLabel(fastRow);
      asm.Xor(Reg.BX, Reg.BX); // Plane index.
      asm.MarkLabel(fastPlane);
      asm.Mov(Reg.AL, Reg.BL);
      asm.Out(Reg.DX, Reg.AL);
      asm.Mov(Reg.SI, Mem.Word(Reg.BP, -2));
      asm.Mov(Reg.CX, Mem.Word(Reg.BP, -6));
      asm.MarkLabel(fastByte);
      asm.Mov(Reg.AL, Mem.Byte(Reg.SI));
      asm.Stosb();
      asm.Inc(Reg.SI);
      asm.Dec(Reg.CX);
      asm.Jnz(fastByte);
      asm.Inc(Reg.BX);
      asm.Cmp(Reg.BX, (Imm)4);
      asm.Jl(fastPlane);
      asm.Add(Mem.Word(Reg.BP, -2), (Imm)80);
      asm.Dec(Mem.Word(Reg.BP, -4));
      asm.Jnz(fastRow);

      Restore(asm, 0x3CE, 4);
      Restore(asm, 0x3CE, 5);
      asm.Pop(Reg.BX);
      asm.Mov(Reg.DX, 0x3CE);
      asm.Mov(Reg.AL, Reg.BL);
      asm.Out(Reg.DX, Reg.AL);
      asm.Pop(Reg.DS);
      asm.Mov(Reg.SP, Reg.BP);
      asm.Pop(Reg.BP);
      asm.Jmp(done);
    }

    foreach (var name in new[] { "rt_pg_stride", "rt_pg_plane", "rt_pg_block", "rt_pg_bit" }) {
      asm.MarkLabel(name);
      asm.Dw(0);
    }
    asm.MarkLabel("rt_pg_acc");
    asm.Db(0);
  }

  /// <summary>
  /// Mode 12h PUT reconstructs one four-bit palette value from the four
  /// per-row plane streams, then performs the original five logical verbs
  /// through the existing POINT/PSET primitives. Rendering is deliberately
  /// in the original row/pixel order; a later fast plane-major writer may
  /// replace this only when overlap and VGA hardware effects are proven.
  /// </summary>
  private void EmitPlanarPut(Assembler asm, Label entry) {
    var row = asm.DefineLabel();
    var column = asm.DefineLabel();
    var plane = asm.DefineLabel();
    var zeroBit = asm.DefineLabel();
    var colorReady = asm.DefineLabel();
    var doPreset = asm.DefineLabel();
    var doAnd = asm.DefineLabel();
    var doOr = asm.DefineLabel();
    var doXor = asm.DefineLabel();
    var combined = asm.DefineLabel();
    var done = asm.DefineLabel();
    var nextRow = asm.DefineLabel();

    asm.MarkLabel(entry);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Push(Reg.SI);
    asm.Push(Reg.DI);
    asm.Push(Reg.ES);
    asm.Cld();

    asm.Mov(Reg.ES, Mem.Word(asm.Lbl("rt_gbufseg")));
    asm.Mov(Reg.DI, Mem.Word(asm.Lbl("rt_gbufofs")));
    asm.Mov(Reg.AX, Mem.Word(Reg.DI).Seg(Reg.ES));
    asm.Mov(Mem.Word(asm.Lbl("rt_gpw")), Reg.AX);
    asm.Add(Reg.AX, (Imm)7);
    asm.Shr(Reg.AX, 3);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_stride")), Reg.AX);
    asm.Shl(Reg.AX, 2);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_rowbytes")), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.DI, 2).Seg(Reg.ES));
    asm.Mov(Mem.Word(asm.Lbl("rt_gph")), Reg.AX);
    asm.Add(Reg.DI, (Imm)4);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_base")), Reg.DI);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_row")), (Imm)0);

    asm.MarkLabel(row);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_row")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gph")));
    asm.Jge(done);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_col")), (Imm)0);

    asm.MarkLabel(column);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_col")));
    asm.Cmp(Reg.AX, Mem.Word(asm.Lbl("rt_gpw")));
    asm.Jge(nextRow);

    asm.Mov(Reg.CX, Reg.AX);
    asm.And(Reg.CX, (Imm)7);
    asm.Mov(Reg.AL, (Imm)0x80);
    asm.Shr(Reg.AL, Reg.CL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_pp_mask")), Reg.AL);

    // Offset of this eight-pixel group in the first plane of the row.
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_row")));
    asm.Mul(Mem.Word(asm.Lbl("rt_pp_rowbytes")));
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_pp_base")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pp_col")));
    asm.Shr(Reg.BX, 3);
    asm.Add(Reg.AX, Reg.BX);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_offset")), Reg.AX);
    asm.Mov(Mem.Byte(asm.Lbl("rt_pp_color")), (Imm)0);
    asm.Mov(Mem.Word(asm.Lbl("rt_pp_plane")), (Imm)0);

    asm.MarkLabel(plane);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_plane")));
    asm.Cmp(Reg.AX, (Imm)4);
    asm.Jge(colorReady);
    asm.Mul(Mem.Word(asm.Lbl("rt_pp_stride")));
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_pp_offset")));
    asm.Mov(Reg.SI, Reg.AX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.SI).Seg(Reg.ES));
    asm.And(Reg.AL, Mem.Byte(asm.Lbl("rt_pp_mask")));
    asm.Jz(zeroBit);
    asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_pp_plane")));
    asm.Mov(Reg.AL, (Imm)1);
    asm.Shl(Reg.AL, Reg.CL);
    asm.Or(Mem.Byte(asm.Lbl("rt_pp_color")), Reg.AL);
    asm.MarkLabel(zeroBit);
    asm.Inc(Mem.Word(asm.Lbl("rt_pp_plane")));
    asm.Jmp(plane);

    asm.MarkLabel(colorReady);
    asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)0);
    asm.Je(combined);
    asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)1);
    asm.Je(doPreset);

    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_col")));
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pp_row")));
    asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Call(this.Point);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_pp_color")));
    asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)2);
    asm.Je(doAnd);
    asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)3);
    asm.Je(doOr);

    asm.MarkLabel(doXor);
    asm.Xor(Reg.CL, Reg.AL);
    asm.Jmp(combined);
    asm.MarkLabel(doAnd);
    asm.And(Reg.CL, Reg.AL);
    asm.Jmp(combined);
    asm.MarkLabel(doOr);
    asm.Or(Reg.CL, Reg.AL);
    asm.Jmp(combined);
    asm.MarkLabel(doPreset);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_pp_color")));
    asm.Not(Reg.CL);
    asm.And(Reg.CL, (Imm)15);
    asm.Jmp(combined);

    asm.MarkLabel(combined);
    // For COPY, the encoded sprite pixel is the exact 4-bit value.
    var copied = asm.DefineLabel();
    asm.Cmp(Mem.Word(asm.Lbl("rt_gverb")), (Imm)0);
    asm.Jne(copied);
    asm.Mov(Reg.CL, Mem.Byte(asm.Lbl("rt_pp_color")));
    asm.MarkLabel(copied);
    asm.Mov(Reg.AX, Mem.Word(asm.Lbl("rt_pp_col")));
    asm.Add(Reg.AX, Mem.Word(asm.Lbl("rt_gx1")));
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_pp_row")));
    asm.Add(Reg.BX, Mem.Word(asm.Lbl("rt_gy1")));
    asm.Mov(Reg.DL, Reg.CL);
    asm.Call(this.Pset);
    asm.Inc(Mem.Word(asm.Lbl("rt_pp_col")));
    asm.Jmp(column);

    asm.MarkLabel(nextRow);
    asm.Inc(Mem.Word(asm.Lbl("rt_pp_row")));
    asm.Jmp(row);
    asm.MarkLabel(done);

    asm.Pop(Reg.ES);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.SI);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();

    foreach (var name in new[] {
      "rt_pp_stride", "rt_pp_rowbytes", "rt_pp_base", "rt_pp_row",
      "rt_pp_col", "rt_pp_plane", "rt_pp_offset",
    }) {
      asm.MarkLabel(name);
      asm.Dw(0);
    }
    asm.MarkLabel("rt_pp_mask");
    asm.Db(0);
    asm.MarkLabel("rt_pp_color");
    asm.Db(0);
  }
}
