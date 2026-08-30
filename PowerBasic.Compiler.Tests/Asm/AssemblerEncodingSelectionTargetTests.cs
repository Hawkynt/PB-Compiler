using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerEncodingSelectionTargetTests {

  [Test]
  public void EncodingSelection_GivenDefaultCpuAndDeadCarry_WhenScheduled_ThenAddOneBecomesInc() {
    var asm = new Assembler { EnableSchedule = true };
    asm.Add(Reg.AX, (Imm)1);
    asm.Mov(Reg.BX, Reg.CX);
    asm.Cmp(Reg.DX, Reg.SI);                    // complete flag kill before any read

    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(IndexOf(image, [0x40]), Is.GreaterThanOrEqualTo(0), "INC AX");
      Assert.That(IndexOf(image, [0x83, 0xC0, 0x01]), Is.EqualTo(-1), "ADD AX,1 removed");
    });
  }

  [Test]
  public void EncodingSelection_Given386OrLaterCpu_WhenScheduled_ThenAddOneStaysAdd() {
    // The assembler's established ISA-floor bit distinguishes the source-visible default 8086
    // target from every selectable 386/486/586 target. Under SPEED the later tier keeps ADD/SUB;
    // INC/DEC's byte saving is not assumed to beat later execution/dependency costs.
    var asm = new Assembler { EnableSchedule = true, Allow386Jcc = true };
    asm.Add(Reg.AX, (Imm)1);
    asm.Mov(Reg.BX, Reg.CX);
    asm.Cmp(Reg.DX, Reg.SI);

    Assert.That(IndexOf(asm.ToArray(), [0x83, 0xC0, 0x01]), Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void EncodingSelection_Given386OrLaterCpuAndDeadFlags_WhenScheduled_ThenMovZeroStillUsesXor() {
    var asm = new Assembler { EnableSchedule = true, Allow386Jcc = true };
    asm.Mov(Reg.AX, (Imm)0);
    asm.Mov(Reg.BX, Reg.CX);
    asm.Cmp(Reg.DX, Reg.SI);

    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(IndexOf(image, [0x31, 0xC0]), Is.GreaterThanOrEqualTo(0), "XOR AX,AX");
      Assert.That(IndexOf(image, [0xB8, 0x00, 0x00]), Is.EqualTo(-1));
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
