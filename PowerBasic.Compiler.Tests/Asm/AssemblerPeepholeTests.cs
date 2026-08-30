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
  public void Peephole_GivenCompareWordRegisterWithZero_WhenEnabled_ThenBecomesTestRegReg() {
    // cmp bx, 0 (83 /7, 3 bytes)  ->  test bx, bx (85 /r, 2 bytes) - flag-identical, one byte shorter
    Assert.That(Assemble(true, a => a.Cmp(Reg.BX, (Imm)0)), Is.EqualTo(new byte[] { 0x85, 0xDB }));
    Assert.That(Assemble(false, a => a.Cmp(Reg.BX, (Imm)0)), Is.EqualTo(new byte[] { 0x83, 0xFB, 0x00 }),
      "without the peephole the faithful CMP is preserved");
  }

  [Test]
  public void Peephole_GivenCompareAccumulatorWithZero_WhenEnabled_ThenBecomesTestAxAx() {
    // cmp ax, 0  ->  test ax, ax
    Assert.That(Assemble(true, a => a.Cmp(Reg.AX, (Imm)0)), Is.EqualTo(new byte[] { 0x85, 0xC0 }));
  }

  [Test]
  public void Peephole_GivenCompareByteRegisterWithZero_WhenEnabled_ThenBecomesTestByteRegReg() {
    // cmp al, 0 (3C 00, 2 bytes) -> test al, al (84 /r, 2 bytes) - same length, still flag-identical
    Assert.That(Assemble(true, a => a.Cmp(Reg.AL, (Imm)0)), Is.EqualTo(new byte[] { 0x84, 0xC0 }));
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

  [Test]
  public void Peephole_GivenSchedulerEnabled_WhenCopyCoalesces_ThenRetargetedDependencyIsPreserved() {
    // The scheduler prefers memory operations. If the coalesced producer still claimed to write AX,
    // the store reading DX would look independent and move before MOV DX,1234h. The repaired write
    // set must pin producer -> consumer while still allowing ordinary scheduling around them.
    var asm = new Assembler { EnableSchedule = true };
    asm.Mov(Reg.AX, 0x1234);                   // coalesces to MOV DX,1234h
    asm.Mov(Reg.DX, Reg.AX);                   // removed
    asm.Mov(Reg.AX, 7);                        // kills the old intermediate
    asm.Mov(Mem.Word(Reg.BP, -2), Reg.DX);     // memory-priority consumer of the NEW destination
    asm.Add(Reg.BX, Reg.CX);

    var image = asm.ToArray();
    var producer = IndexOf(image, [0xBA, 0x34, 0x12]);
    var consumer = IndexOf(image, [0x89, 0x56, 0xFE]);
    Assert.Multiple(() => {
      Assert.That(IndexOf(image, [0x89, 0xC2]), Is.EqualTo(-1), "MOV DX,AX staging copy is gone under SPEED-style scheduling");
      Assert.That(producer, Is.GreaterThanOrEqualTo(0).And.LessThan(consumer), "the retargeted DX def dominates its scheduled use");
    });
  }

  [Test]
  public void Peephole_GivenSchedulerEnabled_WhenCompareZeroShrinks_ThenSchedulerSeesNewLength() {
    // CMP BX,0 shrinks from three bytes to TEST BX,BX. Its scheduler record must shrink too;
    // otherwise the following load appears to overlap the old three-byte record and the scheduling
    // window breaks instead of hoisting the independent memory operation.
    var asm = new Assembler { EnableSchedule = true };
    asm.Cmp(Reg.BX, (Imm)0);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, 2));
    asm.Add(Reg.CX, Reg.DX);

    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[..3], Is.EqualTo(new byte[] { 0x8B, 0x46, 0x02 }), "the independent load still schedules first");
      Assert.That(IndexOf(image, [0x85, 0xDB]), Is.GreaterThanOrEqualTo(0), "CMP BX,0 became TEST BX,BX");
      Assert.That(IndexOf(image, [0x83, 0xFB, 0x00]), Is.EqualTo(-1));
    });
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i) {
      var hit = true;
      for (var k = 0; k < needle.Length; ++k)
        if (haystack[i + k] != needle[k]) { hit = false; break; }
      if (hit)
        return i;
    }
    return -1;
  }
}
