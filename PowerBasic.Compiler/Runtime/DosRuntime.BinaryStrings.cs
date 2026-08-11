using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>Bit-exact numeric/string record conversions used by the MKx$/CVx family.</summary>
public sealed partial class DosRuntime {

  public Label Mki { get; private set; } = null!;
  public Label Mkbyt { get; private set; } = null!;
  public Label Mkl { get; private set; } = null!;
  public Label Mkdwd { get; private set; } = null!;
  public Label Mks { get; private set; } = null!;
  public Label Mkd { get; private set; } = null!;

  /// <summary>
  /// MK encoders stage their input at <c>rt_scratch</c>, then copy those bytes into a new owned
  /// string. Integer inputs arrive in AX or DX:AX; floating inputs arrive on ST(0) and are popped at
  /// their declared IEEE width, exactly like the direct emitter's inline sequence.
  /// </summary>
  private void EmitBinaryStringProcedures(Assembler asm) {
    void Finish(int length) {
      asm.Mov(Reg.SI, Imm.OffsetOf(asm.Lbl("rt_scratch")));
      asm.Mov(Reg.CX, length);
      asm.Mov(Reg.DX, Reg.DS);
      asm.Call(this.StrMem);
      asm.Ret();
    }

    this.Mkbyt = asm.MarkLabel("rt_mkbyt");
    asm.Mov(Mem.Byte(asm.Lbl("rt_scratch")), Reg.AL);
    Finish(1);

    this.Mki = asm.MarkLabel("rt_mki");
    asm.Mov(Mem.Word(asm.Lbl("rt_scratch")), Reg.AX);
    Finish(2);

    this.Mkl = asm.MarkLabel("rt_mkl");
    this.Mkdwd = asm.MarkLabel("rt_mkdwd");
    asm.Mov(Mem.Word(asm.Lbl("rt_scratch")), Reg.AX);
    asm.Mov(Mem.Word(asm.Lbl("rt_scratch"), 2), Reg.DX);
    Finish(4);

    this.Mks = asm.MarkLabel("rt_mks");
    asm.Fstp(Mem.Dword(asm.Lbl("rt_scratch")));
    Finish(4);

    this.Mkd = asm.MarkLabel("rt_mkd");
    asm.Fstp(Mem.Qword(asm.Lbl("rt_scratch")));
    Finish(8);
  }
}
