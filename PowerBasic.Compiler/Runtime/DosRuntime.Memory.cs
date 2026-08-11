using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

/// <summary>Raw memory operations shared by UDTs, arrays, and fixed-width storage.</summary>
public sealed partial class DosRuntime {

  public Label MemCompare { get; private set; } = null!;
  public Label MemCopy { get; private set; } = null!;
  public Label MemSet { get; private set; } = null!;

  /// <summary>
  /// Compares CX bytes at DX:SI and BX:DI. The signed AX result is -1, 0, or 1 according to the
  /// first unequal unsigned byte; DS and ES are restored before returning.
  /// </summary>
  private void EmitMemoryProcedures(Assembler asm) {
    this.MemCompare = asm.MarkLabel("rt_memcmp");
    var done = asm.DefineLabel();
    var greater = asm.DefineLabel();

    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.DS, Reg.DX);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Jcxz(done);
    asm.Repe();
    asm.Cmpsb();
    asm.Je(done);
    asm.Mov(Reg.AX, -1);
    asm.Ja(greater);
    asm.Jmp(done);
    asm.MarkLabel(greater);
    asm.Mov(Reg.AX, 1);
    asm.MarkLabel(done);
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Ret();

    this.MemCopy = asm.MarkLabel("rt_memcpy");
    asm.Push(Reg.DS);
    asm.Push(Reg.ES);
    asm.Mov(Reg.DS, Reg.DX);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Rep();
    asm.Movsb();
    asm.Pop(Reg.ES);
    asm.Pop(Reg.DS);
    asm.Ret();

    this.MemSet = asm.MarkLabel("rt_memset");
    asm.Push(Reg.ES);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Rep();
    asm.Stosb();
    asm.Pop(Reg.ES);
    asm.Ret();
  }
}
