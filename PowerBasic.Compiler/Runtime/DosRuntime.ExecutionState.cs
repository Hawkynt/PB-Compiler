using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private void EmitRestoreUmbAllocationState(Assembler asm) {
    if (!this.EnableUmb)
      return;

    // High-first allocation is private to our heaps. A child COM cannot declare
    // its far-heap minimum to DOS, so it must not inherit this optimization.
    // Preserve AX as well: AL carries END's or the child's exit status here.
    var inactive = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Cmp(Mem.Word(asm.Lbl("rt_umb_active")), (Imm)0);
    asm.Je(inactive);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_umb_oldstrat")));
    asm.Mov(Reg.AX, 0x5801);
    asm.Int(0x21);
    asm.Mov(Reg.BX, Mem.Word(asm.Lbl("rt_umb_oldlink")));
    asm.Mov(Reg.AX, 0x5803);
    asm.Int(0x21);
    asm.MarkLabel(inactive);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
  }

  private void EmitSuspendUmbAllocationForExec(Assembler asm) {
    if (!this.EnableUmb)
      return;
    var unchanged = asm.DefineLabel();
    asm.Push(Reg.AX);
    asm.Cmp(Mem.Word(asm.Lbl("rt_umb_active")), (Imm)0);
    asm.Je(unchanged);
    asm.Mov(Reg.AX, 0x5800);
    asm.Int(0x21);
    asm.Cmp(Reg.AX, (Imm)0x0080);
    asm.Jne(unchanged);
    this.EmitRestoreUmbAllocationState(asm);
    asm.MarkLabel(unchanged);
    asm.Pop(Reg.AX);
  }

  private void EmitSaveUmbAllocationForExec(Assembler asm) {
    if (!this.EnableUmb)
      return;
    // Keep one stack shape even when DOS has no UMB support; the saved SP
    // includes both words, and the inactive resume path discards the padding.
    var inactive = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Cmp(Mem.Word(asm.Lbl("rt_umb_active")), (Imm)0);
    asm.Je(inactive);
    asm.Mov(Reg.AX, 0x5800);
    asm.Int(0x21);
    asm.Push(Reg.AX);
    asm.Mov(Reg.AX, 0x5802);
    asm.Int(0x21);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Push(Reg.AX);
    asm.Jmp(done);
    asm.MarkLabel(inactive);
    asm.Xor(Reg.AX, Reg.AX);
    asm.Push(Reg.AX);
    asm.Push(Reg.AX);
    asm.MarkLabel(done);
  }

  private void EmitResumeUmbAllocationAfterExec(Assembler asm) {
    if (!this.EnableUmb)
      return;
    var inactive = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Cmp(Mem.Word(asm.Lbl("rt_umb_active")), (Imm)0);
    asm.Je(inactive);
    asm.Pop(Reg.BX);
    asm.Mov(Reg.AX, 0x5803);
    asm.Int(0x21);
    asm.Pop(Reg.BX);
    asm.Mov(Reg.AX, 0x5801);
    asm.Int(0x21);
    asm.Jmp(done);
    asm.MarkLabel(inactive);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.BX);
    asm.MarkLabel(done);
  }
}
