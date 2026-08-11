using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// The array DESCRIPTOR an ARRAY SORT / ARRAY SCAN parameter block points at, built from arguments
/// rather than written inline.
///
/// <para>
/// The direct emitter fills a descriptor itself (CodeGenerator.Vendor's EmitArrayDescriptorPush): for
/// a static array it writes DS, the array's data label, the element size and the bounds into a shadow
/// cell it lays out beside the array. Two of those four are things the IR cannot say. A segment
/// register is not a value the IR can name - the same reason VARSEG and the bare DEF SEG became
/// one-instruction routines - and the descriptor must live where DS reaches it, which a frame object
/// of a routed function does not promise. So the caller supplies the near address, the bounds and the
/// element size, and this routine supplies the segment and the storage.
/// </para>
/// <para>
/// Two blocks rather than one, because ARRAY SORT ... TAGARRAY needs a key descriptor and a tag
/// descriptor alive at the same time.
/// </para>
/// <code>
///   rt_arr_desc / rt_arr_tagdesc:
///     DX:SI = the far address of the array's first element
///     BX    = the lower bound of dimension 1
///     CX    = the element byte size
///     DI    = the element count of dimension 1
///     -> AX = the DS offset of the filled descriptor (what rt_arpb +0 holds)
/// </code>
/// </summary>
public sealed partial class DosRuntime {

  public Label ArrDesc { get; private set; } = null!;
  public Label ArrTagDesc { get; private set; } = null!;

  private void EmitArrayDescriptors(Assembler asm) {
    var fill = asm.DefineLabel();

    this.ArrDesc = asm.MarkLabel("rt_arr_desc");
    asm.Mov(Reg.AX, Imm.OffsetOf(asm.Lbl("rt_arr_desc_key")));
    asm.Jmp(fill);

    this.ArrTagDesc = asm.MarkLabel("rt_arr_tagdesc");
    asm.Mov(Reg.AX, Imm.OffsetOf(asm.Lbl("rt_arr_desc_tag")));

    // The two XCHGs are the whole register discipline: BX is both an argument and the only base
    // register free to address the descriptor with, and swapping it twice hands the answer back in AX
    // while leaving BX holding the lower bound it arrived with. It is a byte shorter than PUSH/POP -
    // and, less incidentally than it sounds, it avoids ending in `MOV AX,BX / POP BX / RET`, which is
    // the four-byte signature OptimizerTests scans the whole image for to identify rt_arr_alloc_nz.
    asm.MarkLabel(fill);
    asm.Xchg(Reg.AX, Reg.BX);                    // BX = the descriptor, AX = the lower bound
    asm.Mov(Mem.Word(Reg.BX), Reg.DX);           // +0  data segment
    asm.Mov(Mem.Word(Reg.BX, 2), Reg.SI);        // +2  data offset
    asm.Mov(Mem.Word(Reg.BX, 4), Reg.CX);        // +4  element byte size
    asm.Mov(Mem.Word(Reg.BX, 6), (Imm)1);        // +6  rank (this path builds one-dimensional descriptors only)
    asm.Mov(Mem.Word(Reg.BX, 8), Reg.AX);        // +8  lower bound
    asm.Mov(Mem.Word(Reg.BX, 10), Reg.DI);       // +10 extent
    asm.Xchg(Reg.AX, Reg.BX);                    // AX = the descriptor, BX = what the caller passed
    asm.Ret();
  }

  /// <summary>The two descriptor blocks, in the array data section so the trimmer keeps them with the routine.</summary>
  private static void EmitArrayDescriptorData(Assembler asm) {
    asm.Align(2);
    asm.MarkLabel("rt_arr_desc_key");
    asm.Db(new byte[12]);
    asm.MarkLabel("rt_arr_desc_tag");
    asm.Db(new byte[12]);
  }
}
