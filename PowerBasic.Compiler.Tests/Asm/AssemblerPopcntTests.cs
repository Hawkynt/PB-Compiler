using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerPopcntTests {
  private static byte[] Assemble(Action<Assembler> emit) {
    var asm = new Assembler();
    emit(asm);
    return asm.ToArray();
  }

  [Test]
  public void Popcnt_GivenWordRegisters_ThenEmitsMandatoryF3Encoding() {
    Assert.That(Assemble(a => a.Popcnt(Reg.AX, Reg.CX)),
      Is.EqualTo(new byte[] { 0xF3, 0x0F, 0xB8, 0xC1 }));
  }

  [Test]
  public void Popcnt_GivenDwordRegisters_ThenEmitsOperandSizePrefixBeforeF3() {
    Assert.That(Assemble(a => a.Popcnt(Reg.EAX, Reg.ECX)),
      Is.EqualTo(new byte[] { 0x66, 0xF3, 0x0F, 0xB8, 0xC1 }));
  }

  [Test]
  public void Popcnt_GivenSegmentedDwordMemory_ThenKeepsSegmentPrefixFirst() {
    Assert.That(Assemble(a => a.Popcnt(Reg.EDX, Mem.Dword(Reg.BX).Es())),
      Is.EqualTo(new byte[] { 0x26, 0x66, 0xF3, 0x0F, 0xB8, 0x17 }));
  }
}
