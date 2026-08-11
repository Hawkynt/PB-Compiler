using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// Print CAPTURE, given labels so the IR path can reach it.
///
/// <para>
/// The runtime's print routines all write through one decision: <c>rt_capmode</c>. Zero means "to
/// the current output handle"; anything else means "append to <c>rt_capbuf</c> and count the bytes
/// in <c>rt_caplen</c>". That one cell is what makes <c>STR$</c> the printed form of a number rather
/// than a second formatter, and it is what makes <c>USING$</c> the PRINT USING text rather than a
/// second field renderer - both are the ordinary output routines run with the output pointed at a
/// buffer, which is exactly the trade LPRINT makes in the other direction.
/// </para>
///
/// <para>
/// The direct emitter writes both halves INLINE around the USING body (CodeGenerator.Intrinsics.cs,
/// the <c>USING$</c> case) and keeps doing so - this adds somewhere to CALL, it does not change what
/// that emitter produces. The IR cannot write them inline for the reason
/// <see cref="EmitPrinterProcedures"/> exists: a store of a runtime cell's OFFSET into another
/// runtime cell is not something the IR can say, and the length has to be read back and turned into
/// a string handle in the same breath.
/// </para>
///
/// <para>
/// Capture does NOT nest, here or in the direct emitter: there is one buffer and one length, so a
/// <c>STR$</c> evaluated inside a captured region restarts the capture rather than stacking on it.
/// Both paths inherit that from the single pair of cells, so they inherit it identically.
/// </para>
/// </summary>
public sealed partial class DosRuntime {

  /// <summary>Begin capture: print output is appended to <c>rt_capbuf</c> from the start. No argument, no result.</summary>
  public Label CaptureBegin { get; private set; } = null!;

  /// <summary>
  /// End capture and answer what was written as a string handle in AX. The bytes are taken
  /// VERBATIM - unlike <c>rt_str_cap</c>, which drops the last one because STR$ does not want the
  /// space PB's numeric formatter reserves for a sign. A USING field has no such column.
  /// </summary>
  public Label CaptureEnd { get; private set; } = null!;

  private void EmitCaptureProcedures(Assembler asm) {
    this.CaptureBegin = asm.MarkLabel("rt_capon");
    {
      asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)1);
      asm.Mov(Mem.Word(asm.Lbl("rt_caplen")), (Imm)0);
      asm.Ret();
    }

    this.CaptureEnd = asm.MarkLabel("rt_capoff");
    {
      asm.Mov(Mem.Byte(asm.Lbl("rt_capmode")), (Imm)0);
      asm.Mov(Reg.CX, Mem.Word(asm.Lbl("rt_caplen")));
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_capbuf")));
      asm.Mov(Reg.DX, Reg.DS);                      // the buffer is a runtime cell, so never in the string heap
      asm.Call(this.StrMem);
      asm.Ret();
    }
  }
}
