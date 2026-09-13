using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>
  /// <c>rt_shl32</c> / <c>rt_shr32</c>: a 32-bit value in DX:AX shifted by the COUNT in CX.
  ///
  /// <para>
  /// The selector writes a shift by a compile-time count out as that many one-bit steps, which is
  /// what an 8086 pair shift is; a count it cannot read declined instead, and took the whole module
  /// body with it - 37 times over the SVGA corpus. The loop cannot be written straight-line, so it
  /// is a routine rather than a selection: the same shape as the direct emitter's own per-bit loop
  /// over the word chain.
  /// </para>
  /// <para>
  /// A count of 32 or more shifts every bit out and answers zero, which the loop reaches by running
  /// rather than by a test. That is deliberate - the 386's masked shift would answer something else,
  /// and this routine is the baseline one, so it agrees with the direct emitter's loop instead.
  /// </para>
  /// </summary>
  private void EmitWideShift(Assembler asm) {
    foreach (var left in (bool[])[true, false]) {
      var loop = asm.DefineLabel();
      var done = asm.DefineLabel();
      asm.MarkLabel(left ? "rt_shl32" : "rt_shr32");
      asm.Jcxz(done);
      asm.MarkLabel(loop);
      if (left) {
        asm.Shl(Reg.AX, 1);
        asm.Rcl(Reg.DX, 1);
      } else {
        asm.Shr(Reg.DX, 1);
        asm.Rcr(Reg.AX, 1);
      }
      asm.Loop(loop);
      asm.MarkLabel(done);
      asm.Ret();
    }
  }
}
