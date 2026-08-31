using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class DeadFrameStoreControlFlowTests {

  [Test]
  public void DeadStore_GivenConditionalBranchCanSkipOverwrite_WhenAssembled_ThenOlderStoreIsKept() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var done = asm.DefineLabel();

    asm.Mov(Mem.Word(Reg.BP, -8), Reg.AX);      // value needed on the taken branch
    asm.Cmp(Reg.DX, Reg.SI);
    asm.J(Condition.Equal, done);                // taken path skips the CX overwrite
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);      // fall-through-only replacement
    asm.MarkLabel(done);
    asm.Mov(Reg.AX, Reg.BX);                    // prevent the later load forwarding from AX
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, -8));      // observes AX or CX depending on the branch
    asm.Ret();

    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x89, 0x46, 0xF8]), Is.GreaterThanOrEqualTo(0),
      "MOV [BP-8],AX must survive because the taken path bypasses the overwrite");
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match)
        return i;
    }
    return -1;
  }
}
