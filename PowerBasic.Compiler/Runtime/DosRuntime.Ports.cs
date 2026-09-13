using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>
  /// <c>rt_outp</c> / <c>rt_inp</c>: write a byte to an I/O port, and read one back. <c>OUT port, value</c>, which the direct emitter
  /// writes inline as <c>OUT DX, AL</c>.
  ///
  /// <para>
  /// It exists as a CALL rather than inline because the IR has to name the operation abstractly -
  /// the same declaration reaches <c>--emit-c</c> and <c>--emit-llvm</c>, where a port write is
  /// whatever that target says it is and usually nothing at all. Naming it is what let <c>OUT</c>
  /// lower; it was 46 routing declines, all of them in graphics code setting a VGA register.
  /// </para>
  /// <para>
  /// The ABI hands the port over in DX and the value in AX, which is where <c>OUT DX, AL</c> wants
  /// them - so the routine is the one instruction plus its return, and the register choice is what
  /// makes it that rather than a frame and four moves.
  /// </para>
  /// </summary>
  private void EmitPortOut(Assembler asm) {
    asm.MarkLabel("rt_outp");
    asm.Out(Reg.DX, Reg.AL);                       // DX = port, AL = value: the ABI hands them over
    asm.Ret();

    // INP(port) - the read half, and the same bargain. AH is cleared because the port gives a BYTE
    // and PowerBASIC's INP answers an INTEGER: leaving the high half as it lies would make the value
    // depend on whatever ran before it, which is the shape of the BYTE-result bug this compiler has
    // already been bitten by once.
    asm.MarkLabel("rt_inp");
    asm.Mov(Reg.DX, Reg.AX);                       // AX = port
    asm.Xor(Reg.AH, Reg.AH);
    asm.In(Reg.AL, Reg.DX);
    asm.Ret();
  }
}
