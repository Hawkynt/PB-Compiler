using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// PB internal variables (pbvScrnCols, pbvScrnRows, ...) backed by runtime data
/// cells, plus the EXIT FAR unwind state and the DIR$ DTA buffers.
/// </summary>
public sealed partial class DosRuntime {

  /// <summary>One internal variable: runtime data label, cell size in bytes, initial value.</summary>
  public readonly record struct InternalVariable(string Label, int Size, int Initial);

  /// <summary>
  /// Catalog of PB internal variables resolvable by name from BASIC code and
  /// inline assembly. Screen cells are refreshed from the BIOS data area at
  /// startup; pbvDefSeg aliases the runtime DEF SEG cell.
  /// </summary>
  public static readonly IReadOnlyDictionary<string, InternalVariable> InternalVariables =
    new Dictionary<string, InternalVariable>(StringComparer.OrdinalIgnoreCase) {
      ["pbvScrnCols"] = new("rt_pbv_scrncols", 1, 80),
      ["pbvScrnRows"] = new("rt_pbv_scrnrows", 1, 25),
      ["pbvScrnMode"] = new("rt_pbv_scrnmode", 1, 3),
      ["pbvScrnCard"] = new("rt_pbv_scrncard", 1, 0),   // bit0 clear = color adapter
      ["pbvScrnAPage"] = new("rt_pbv_scrnapage", 1, 0),
      ["pbvScrnBuff"] = new("rt_pbv_scrnbuff", 2, 0xB800),
      ["pbvCursor1"] = new("rt_pbv_cursor1", 2, 6),     // cursor start scan line
      ["pbvCursor2"] = new("rt_pbv_cursor2", 2, 7),     // cursor end scan line
      ["pbvDefSeg"] = new("rt_defseg", 2, 0),           // alias of the DEF SEG cell
      ["pbvHost"] = new("rt_pbv_host", 2, 0),           // 0 = plain DOS
      ["pbvBinBase"] = new("rt_pbv_binbase", 2, 0),
      ["pbvSwitch"] = new("rt_pbv_switch", 2, 0),
      ["pbvFixDigits"] = new("rt_pbv_fixdigits", 1, 2), // FIX fraction digits
      ["pbvRestore"] = new("rt_pbv_restore", 2, 0),
      ["pbvVTxtX1"] = new("rt_pbv_vtxtx1", 1, 1),
      ["pbvVTxtY1"] = new("rt_pbv_vtxty1", 1, 1),
      ["pbvVTxtX2"] = new("rt_pbv_vtxtx2", 1, 80),
      ["pbvVTxtY2"] = new("rt_pbv_vtxty2", 1, 25),
    };

  /// <summary>Runtime data label of an internal variable, or null when the name is not one.</summary>
  public static string? InternalVariableLabel(string name)
    => InternalVariables.TryGetValue(name, out var iv) ? iv.Label : null;

  /// <summary>Refreshes the screen-state internal variables from the BIOS data area (entry stub).</summary>
  private void EmitInternalsInit(Assembler asm) {
    asm.Push(Reg.ES);
    asm.Mov(Reg.AX, 0x40);
    asm.Mov(Reg.ES, Reg.AX);
    asm.Mov(Reg.AL, Mem.Byte(0x49).Es());           // current video mode
    asm.Mov(Mem.Byte(asm.Lbl("rt_pbv_scrnmode")), Reg.AL);
    asm.Mov(Reg.AL, Mem.Byte(0x4A).Es());           // columns (low byte of the word)
    asm.Mov(Mem.Byte(asm.Lbl("rt_pbv_scrncols")), Reg.AL);
    asm.Mov(Reg.AL, Mem.Byte(0x84).Es());           // rows - 1 (EGA+; 0 on very old BIOSes)
    asm.Test(Reg.AL, Reg.AL);
    var rowsKnown = asm.DefineLabel();
    asm.Jz(rowsKnown);
    asm.Inc(Reg.AL);
    asm.Mov(Mem.Byte(asm.Lbl("rt_pbv_scrnrows")), Reg.AL);
    asm.MarkLabel(rowsKnown);
    asm.Mov(Reg.AL, Mem.Byte(0x62).Es());           // active display page
    asm.Mov(Mem.Byte(asm.Lbl("rt_pbv_scrnapage")), Reg.AL);
    asm.Pop(Reg.ES);
  }

  private void EmitInternalsData(Assembler asm) {
    asm.Align(2);
    foreach (var iv in InternalVariables.Values) {
      if (iv.Label == "rt_defseg")
        continue; // emitted with the low-level data
      asm.MarkLabel(iv.Label);
      if (iv.Size == 1)
        asm.Db((byte)iv.Initial);
      else
        asm.Dw((ushort)iv.Initial);
    }

    // EXIT FAR unwind state: stack mark + target offset
    asm.Align(2);
    asm.MarkLabel("rt_efar_sp");
    asm.Dw(0);
    asm.MarkLabel("rt_efar_bp");
    asm.Dw(0);
    asm.MarkLabel("rt_efar_tgt");
    asm.Dw(0);

    // DIR$ state: private DTA and the ASCIIZ search spec
    asm.MarkLabel("rt_dta");
    asm.Db(new byte[44]);
    asm.MarkLabel("rt_dirspec");
    asm.Db(new byte[80]);

    // ARRAY SORT/SCAN parameter block. The runtime reads it by displacement off rt_arpb, which is how
    // the routines below and the direct emitter both spell it; every field ALSO carries a name of its
    // own, because the IR path has no displacement to spell - it addresses a runtime cell by naming
    // it, and a block it had to index would cost one base register per field it fills, which is more
    // registers than the 8086 has. The bytes are unchanged: twenty zeros either way.
    asm.MarkLabel("rt_arpb");            // +0  descriptor pointer
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_start");      // +2  start index
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_count");      // +4  element count
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_collate");    // +6  COLLATE table handle (0 = none)
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_from");       // +8  FROM character position (1-based)
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_to");         // +10 TO character position
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_flags");      // +12 bit0 descend, bit1 range-left-only, high byte relop
    asm.Dw(0);
    asm.MarkLabel("rt_arpb_match");      // +14 match handle
    asm.Dw(0);
    asm.Dw(0);                           // +16 data base offset, filled by the setup prologue
    asm.Dw(0);                           // +18 data segment, likewise

    // numeric ARRAY SORT/SCAN parameter block (parallel to rt_arpb for non-string
    // arrays): kind/size of the element, descend flag, scan relop + match value,
    // and the optional TAGARRAY descriptor. Two 10-byte staging cells let the
    // comparison load any element width (incl. 80-bit EXT) through the x87.
    asm.MarkLabel("rt_num_kind");    // 0 integer, 2 float
    asm.Db(0);
    asm.MarkLabel("rt_num_size");    // element byte size to copy (1/2/4/8/10)
    asm.Db(0);
    asm.MarkLabel("rt_num_load");    // x87 load width: int 2/4/8, float 4/8/10
    asm.Db(0);
    asm.MarkLabel("rt_num_desc");    // 0 ascend, 1 descend
    asm.Db(0);
    asm.MarkLabel("rt_num_relop");   // scan: 0 = 1 <> 2 < 3 <= 4 > 5 >=
    asm.Db(0);
    asm.MarkLabel("rt_num_match");   // scan match value (raw element bytes)
    asm.Db(new byte[10]);
    asm.MarkLabel("rt_num_tagdesc"); // TAGARRAY descriptor ptr (0 = none)
    asm.Dw(0);
    asm.MarkLabel("rt_num_tagsize"); // TAGARRAY element byte size
    asm.Dw(0);
    asm.MarkLabel("rt_num_tagoff");  // TAGARRAY data base offset
    asm.Dw(0);
    asm.MarkLabel("rt_num_tagseg");  // TAGARRAY data segment
    asm.Dw(0);
    asm.MarkLabel("rt_num_a");       // x87 staging for element a (zero-padded)
    asm.Db(new byte[10]);
    asm.MarkLabel("rt_num_b");       // x87 staging for element b (zero-padded)
    asm.Db(new byte[10]);
    asm.MarkLabel("rt_num_i");       // sort outer loop index (survives helper calls)
    asm.Dw(0);
    asm.MarkLabel("rt_num_j");       // sort inner loop index
    asm.Dw(0);
    asm.MarkLabel("rt_num_n");       // element count
    asm.Dw(0);

    // string compare scratch (rt_strcmprange)
    asm.MarkLabel("rt_cmp_loff");
    asm.Dw(0);
    asm.MarkLabel("rt_cmp_llen");
    asm.Dw(0);
    asm.MarkLabel("rt_cmp_roff");
    asm.Dw(0);
    asm.MarkLabel("rt_cmp_rlen");
    asm.Dw(0);
    asm.MarkLabel("rt_cmp_col");
    asm.Dw(0);

    // EXTRACT$ scratch
    asm.MarkLabel("rt_ext0");
    asm.Dw(0);
    asm.MarkLabel("rt_ext1");
    asm.Dw(0);
    asm.MarkLabel("rt_ext2");
    asm.Dw(0);

    // program segment prefix (COMMAND$, ENVIRON$)
    asm.MarkLabel("rt_pspseg");
    asm.Dw(0);

    // ERL: last executed numeric line label
    asm.MarkLabel("rt_erl");
    asm.Dw(0);

    // $STRING n: usable bytes per string (1006..32750, default 32750)
    asm.MarkLabel("rt_strmaxlen");
    asm.Dw(32750);

    // REPLACE scratch: subject, find, repl, result, pos, findlen, hit
    asm.MarkLabel("rt_rp");
    asm.Db(new byte[14]);

    // SHELL/EXECUTE: EXEC parameter block, command tail, SS:SP save, null FCB
    asm.MarkLabel("rt_execpb");
    asm.Db(new byte[14]);
    asm.MarkLabel("rt_shellbuf");
    asm.Db(new byte[132]);
    asm.MarkLabel("rt_sssave");
    asm.Dw(0);
    asm.MarkLabel("rt_spsave");
    asm.Dw(0);
    asm.MarkLabel("rt_fcb");
    asm.Db(new byte[16]);
    asm.MarkLabel("rt_comspec");
    asm.Db("COMSPEC");

    // dynamic USING$ scan state: width, decimals, group, field start/end, fmt off/len
    asm.Align(2);
    asm.MarkLabel("rt_ud");
    asm.Db(new byte[14]);
  }
}
