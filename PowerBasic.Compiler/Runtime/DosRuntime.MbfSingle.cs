using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Runtime;

public sealed partial class DosRuntime {

  /// <summary>ST(0) to 24 significant bits, ties away from zero, without IEEE single's exponent range.</summary>
  private void EmitMbfSingleNarrow(Assembler asm) {
    var rounded = asm.DefineLabel();
    var reload = asm.DefineLabel();
    asm.MarkLabel("rt_mbfsng");
    asm.Fstp(Mem.Tbyte(this._scratch));
    asm.Mov(Reg.AX, Mem.Word(this._scratch, 8));
    asm.And(Reg.AX, (Imm)0x7FFF);
    asm.Cmp(Reg.AX, (Imm)0x7FFF);
    asm.Je(reload);                                      // preserve non-finite inputs for the store's error check
    asm.Test(Mem.Byte(this._scratch, 4), (Imm)0x80);
    asm.Jz(rounded);
    // As in the MBF64 store, round the retained significand and carry into its exponent. Here
    // conversion discards 40 bits and deliberately rounds an exact halfway case away from zero.
    asm.Inc(Mem.Word(this._scratch, 5));
    asm.Jnz(rounded);
    asm.Inc(Mem.Byte(this._scratch, 7));
    asm.Jnz(rounded);
    asm.Mov(Mem.Byte(this._scratch, 7), (Imm)0x80);
    asm.Inc(Mem.Word(this._scratch, 8));
    asm.MarkLabel(rounded);
    asm.Mov(Mem.Word(this._scratch), (Imm)0);
    asm.Mov(Mem.Word(this._scratch, 2), (Imm)0);
    asm.Mov(Mem.Byte(this._scratch, 4), (Imm)0);
    asm.MarkLabel(reload);
    asm.Fld(Mem.Tbyte(this._scratch));
    asm.Ret();
  }
}
