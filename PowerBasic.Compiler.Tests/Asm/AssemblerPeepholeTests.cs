using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerPeepholeTests {

  private static byte[] Assemble(bool peephole, Action<Assembler> emit) {
    var asm = new Assembler { EnablePeephole = peephole };
    emit(asm);
    return asm.ToArray();
  }

  [Test]
  public void Peephole_GivenImmediateStagedThenRegisterDies_WhenEnabled_ThenLoadsTargetDirectly() {
    // mov ax, 0x1234 ; mov bx, ax ; pop ax   ->   mov bx, 0x1234 ; pop ax
    var emit = (Action<Assembler>)(a => { a.Mov(Reg.AX, 0x1234); a.Mov(Reg.BX, Reg.AX); a.Pop(Reg.AX); });
    Assert.That(Assemble(true, emit), Is.EqualTo(new byte[] { 0xBB, 0x34, 0x12, 0x58 }));
    Assert.That(Assemble(false, emit), Is.EqualTo(new byte[] { 0xB8, 0x34, 0x12, 0x89, 0xC3, 0x58 }),
      "without the peephole the faithful staged stream is preserved");
  }

  [Test]
  public void Peephole_GivenRegisterStagedThenRegisterDies_WhenEnabled_ThenCopiesSourceDirectly() {
    // mov ax, dx ; mov bx, ax ; pop ax   ->   mov bx, dx ; pop ax
    var bytes = Assemble(true, a => { a.Mov(Reg.AX, Reg.DX); a.Mov(Reg.BX, Reg.AX); a.Pop(Reg.AX); });
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x89, 0xD3, 0x58 }), "mov bx, dx then pop ax");
  }

  [Test]
  public void Peephole_GivenMemoryStagedThenRegisterDies_WhenEnabled_ThenLoadsTargetFromMemory() {
    // mov ax, [0x10] ; mov bx, ax ; pop ax   ->   mov bx, [0x10] ; pop ax
    var bytes = Assemble(true, a => { a.Mov(Reg.AX, Mem.Word(0x10)); a.Mov(Reg.BX, Reg.AX); a.Pop(Reg.AX); });
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x8B, 0x1E, 0x10, 0x00, 0x58 }), "mov bx, [0x10] then pop ax");
  }

  [Test]
  public void Peephole_GivenIntermediateStillLive_WhenEnabled_ThenKeepsTheStagingCopy() {
    // the next instruction does not overwrite AX, so AX is still live - the copy must remain
    var emit = (Action<Assembler>)(a => { a.Mov(Reg.AX, 0x1234); a.Mov(Reg.BX, Reg.AX); a.Inc(Reg.CX); });
    Assert.That(Assemble(true, emit), Is.EqualTo(Assemble(false, emit)), "a live intermediate is never coalesced");
  }

  [Test]
  public void Peephole_GivenLabelOnTheStagingCopy_WhenEnabled_ThenKeepsItReachable() {
    // a branch targets the 'mov bx, ax' - folding it away would strand the jump, so it must stay
    static void Emit(Assembler a) {
      var here = a.DefineLabel();
      a.Mov(Reg.AX, 0x1234);
      a.MarkLabel(here);
      a.Mov(Reg.BX, Reg.AX);
      a.Pop(Reg.AX);
      a.Jmp(here);
    }
    Assert.That(Assemble(true, Emit), Is.EqualTo(Assemble(false, Emit)), "a label on the copy blocks coalescing");
  }
}
