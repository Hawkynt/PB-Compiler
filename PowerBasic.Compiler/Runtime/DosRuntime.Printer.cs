using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// LPRINT's two halves, given labels so the IR path can reach them.
///
/// <para>
/// LPRINT is PRINT with the output pointed at the printer, and pointing it there is four words:
/// the active output handle becomes DOS's PRN (handle 4, which DOS opens for every program before
/// it starts, so there is nothing to open and nothing to close), and the active print column
/// becomes the PRINTER's column. The two columns are counted apart on purpose - a comma zone or a
/// TAB on the printer must not move the screen's cursor, which is why BASIC has both POS and LPOS -
/// and every item, separator, zone and USING clause then works unchanged, because all of them
/// already write through <c>rt_curout</c> and count through <c>rt_colptr</c>.
/// </para>
///
/// <para>
/// The direct emitter writes these four MOVs INLINE at the head and tail of the statement
/// (CodeGenerator.Io.cs, EmitPrint) and keeps doing so - this adds somewhere to CALL, it does not
/// change what that emitter produces. The IR cannot write them inline: a store to a runtime cell
/// whose value is the OFFSET of another runtime cell is not something the IR can say, and the C and
/// LLVM back ends need a name to bind their own answer to. That is the same trade
/// <see cref="EmitLowLevelProcedures"/> makes for CSRLIN, CONSIN and the bare DEF SEG, and these
/// routines live in that section for the same reason.
/// </para>
///
/// <para>Neither routine takes an argument, answers anything, or touches a register.</para>
/// </summary>
public sealed partial class DosRuntime {

  /// <summary>LPRINT: route console output at the printer (handle 4) and at the printer's own column.</summary>
  public Label LPrintOn { get; private set; } = null!;

  /// <summary>...and back to the screen when the statement ends.</summary>
  public Label LPrintOff { get; private set; } = null!;

  private void EmitPrinterProcedures(Assembler asm) {
    this.LPrintOn = asm.MarkLabel("rt_lpon");
    {
      asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 4);                          // DOS PRN, open before we start
      asm.Mov(Mem.Word(asm.Lbl("rt_colptr")), Imm.OffsetOf(asm.Lbl("rt_lcol")));
      asm.Ret();
    }

    this.LPrintOff = asm.MarkLabel("rt_lpoff");
    {
      asm.Mov(Mem.Word(asm.Lbl("rt_curout")), 1);                          // stdout
      asm.Mov(Mem.Word(asm.Lbl("rt_colptr")), Imm.OffsetOf(asm.Lbl("rt_col")));
      asm.Ret();
    }
  }
}
