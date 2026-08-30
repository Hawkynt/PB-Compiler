using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// O0081 result-flag reuse: a zero test is redundant when the closest flag writer already left
/// ZF/SF/PF describing the same unchanged register value and the following branch consumes only
/// those flags.
/// </summary>
[TestFixture]
public sealed class FlagReuseTests {

  [Test]
  public void Reuse_GivenSubtractStoreCompareZero_WhenBranchReadsZeroFlag_ThenCompareRemoved() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();
    asm.Sub(Reg.AX, (Imm)1);
    asm.Mov(Mem.Word(Reg.BP, -2), Reg.AX);       // does not disturb flags
    asm.Cmp(Reg.AX, (Imm)0);                    // redundant
    asm.Jnz(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x83, 0xF8, 0x00]), Is.EqualTo(-1), "CMP AX,0 is removed");
  }

  [Test]
  public void Reuse_GivenDecrementThenOrSelf_WhenBranchReadsSignFlag_ThenOrRemoved() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();
    asm.Dec(Reg.AX);
    asm.Or(Reg.AX, Reg.AX);                      // redundant zero/sign test
    asm.Js(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x09, 0xC0]), Is.EqualTo(-1), "OR AX,AX is removed");
  }

  [Test]
  public void Reuse_GivenShiftThenTestSelf_WhenBranchReadsParityFlag_ThenTestRemoved() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();
    asm.Shl(Reg.AX, 1);
    asm.Test(Reg.AX, Reg.AX);                    // same ZF/SF/PF as SHL result
    asm.Jp(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x85, 0xC0]), Is.EqualTo(-1), "TEST AX,AX is removed");
  }

  [Test]
  public void Reuse_GivenCarryBranch_WhenPreviousArithmeticHasDifferentCarryMeaning_ThenCompareKept() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();
    asm.Sub(Reg.AX, (Imm)1);
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jb(done);                                 // CMP AX,0 defines CF=0; SUB's CF is unrelated
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x83, 0xF8, 0x00]), Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void Reuse_GivenRegisterRewriteBetweenProducerAndTest_WhenAssembled_ThenCompareKept() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();
    asm.Sub(Reg.AX, (Imm)1);
    asm.Mov(Reg.AX, Reg.CX);                      // AX no longer matches SUB's flags
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jz(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x83, 0xF8, 0x00]), Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void Reuse_GivenReachableEntryLabelAtTest_WhenAssembled_ThenCompareKept() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var test = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Sub(Reg.AX, (Imm)1);
    asm.MarkLabel(test);                          // another path can enter without running SUB
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jz(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.MarkLabel(done);
    asm.Ret();
    asm.Jmp(test);                                // make test a real control-flow target

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x83, 0xF8, 0x00]), Is.GreaterThanOrEqualTo(0));
  }

  [Test]
  public void Reuse_GivenUnrecordedBarrier_WhenAssembled_ThenCompareKept() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var callee = asm.DefineLabel();
    var done = asm.DefineLabel();
    asm.Sub(Reg.AX, (Imm)1);
    asm.Call(callee);                             // call can replace both AX and flags
    asm.Cmp(Reg.AX, (Imm)0);
    asm.Jz(done);
    asm.Mov(Reg.BX, (Imm)7);
    asm.Jmp(done);
    asm.MarkLabel(callee);
    asm.Ret();
    asm.MarkLabel(done);

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x83, 0xF8, 0x00]), Is.GreaterThanOrEqualTo(0));
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
