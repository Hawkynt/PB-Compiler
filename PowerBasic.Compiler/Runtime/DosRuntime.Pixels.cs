using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {
  private static void EmitPackedModeBranch(Assembler asm, Label packed) {
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0F);
    asm.Int(0x10);
    asm.Cmp(Reg.AL, (Imm)0x13);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Je(packed);
  }

  private void EmitSetPixel(Assembler asm) {
    this.Pset = asm.MarkLabel("rt_pset"); // AX=x, BX=y, DL=color; retain the caller's coordinates.
    var packed = asm.DefineLabel();
    EmitPackedModeBranch(asm, packed);
    asm.Push(Reg.AX);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Push(Reg.DX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Mov(Reg.AX, Reg.DX);
    asm.Mov(Reg.DX, Reg.BX);
    asm.Xor(Reg.BX, Reg.BX); // Page zero; firmware handles the active mode's pixel encoding.
    asm.Mov(Reg.AH, (Imm)0x0C);
    asm.Int(0x10);
    asm.Pop(Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.AX);
    asm.Ret();
    asm.MarkLabel(packed);
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.DI, Reg.BX); // Retain the existing packed y*320+x implementation.
    asm.Mov(Reg.CL, (Imm)8);
    asm.Shl(Reg.DI, Reg.CL);
    asm.Mov(Reg.CL, (Imm)6);
    asm.Shl(Reg.BX, Reg.CL);
    asm.Add(Reg.DI, Reg.BX);
    asm.Add(Reg.DI, Reg.AX);
    asm.Mov(Reg.BX, 0xA000);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Mov(Mem.Byte(Reg.DI).Seg(Reg.ES), Reg.DL);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Ret();
  }

  private void EmitReadPixel(Assembler asm) {
    this.Point = asm.MarkLabel("rt_point"); // AX=x, BX=y -> DX:AX=color.
    var packed = asm.DefineLabel();
    EmitPackedModeBranch(asm, packed);
    asm.Push(Reg.BX);
    asm.Push(Reg.CX);
    asm.Mov(Reg.CX, Reg.AX);
    asm.Mov(Reg.DX, Reg.BX);
    asm.Xor(Reg.BX, Reg.BX);
    asm.Mov(Reg.AH, (Imm)0x0D);
    asm.Int(0x10);
    asm.Xor(Reg.AH, Reg.AH);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.BX);
    asm.Ret();
    asm.MarkLabel(packed);
    asm.Push(Reg.ES);
    asm.Push(Reg.DI);
    asm.Push(Reg.CX);
    asm.Push(Reg.BX);
    asm.Mov(Reg.DI, Reg.BX);
    asm.Mov(Reg.CL, (Imm)8);
    asm.Shl(Reg.DI, Reg.CL);
    asm.Mov(Reg.CL, (Imm)6);
    asm.Shl(Reg.BX, Reg.CL);
    asm.Add(Reg.DI, Reg.BX);
    asm.Add(Reg.DI, Reg.AX);
    asm.Mov(Reg.BX, 0xA000);
    asm.Mov(Reg.ES, Reg.BX);
    asm.Mov(Reg.AL, Mem.Byte(Reg.DI).Seg(Reg.ES));
    asm.Xor(Reg.AH, Reg.AH);
    asm.Xor(Reg.DX, Reg.DX);
    asm.Pop(Reg.BX);
    asm.Pop(Reg.CX);
    asm.Pop(Reg.DI);
    asm.Pop(Reg.ES);
    asm.Ret();
  }
}
