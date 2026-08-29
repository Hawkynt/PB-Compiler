using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerBitManipulationTests {
  private static byte[] Assemble(Action<Assembler> emit) {
    var assembler = new Assembler();
    emit(assembler);
    return assembler.ToArray();
  }

  [Test]
  public void Popcnt_GivenWordRegisters_ThenUsesDefault16BitOperandSize()
    => Assert.That(Assemble(a => a.Popcnt(Reg.AX, Reg.CX)),
      Is.EqualTo(new byte[] { 0xF3, 0x0F, 0xB8, 0xC1 }));

  [Test]
  public void Popcnt_GivenDwordRegisters_ThenEmitsOperandSizeOverrideBeforeMandatoryPrefix()
    => Assert.That(Assemble(a => a.Popcnt(Reg.EAX, Reg.ECX)),
      Is.EqualTo(new byte[] { 0x66, 0xF3, 0x0F, 0xB8, 0xC1 }));

  [Test]
  public void Popcnt_GivenSegmentedDwordMemory_ThenPrefixesSegmentBeforeOperandAndMandatoryPrefixes()
    => Assert.That(Assemble(a => a.Popcnt(Reg.EDX, Mem.Dword(Reg.BX).Es())),
      Is.EqualTo(new byte[] { 0x26, 0x66, 0xF3, 0x0F, 0xB8, 0x17 }));
}
