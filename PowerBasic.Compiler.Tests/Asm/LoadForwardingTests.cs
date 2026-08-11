using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// Redundant-load elimination: <c>MOV [BP-d],R … MOV R,[BP-d]</c> leaves R already holding the
/// value, so the reload is dead. Deliberately narrow - the cases it must DECLINE are as much the
/// specification as the case it takes.
/// </summary>
[TestFixture]
public sealed class LoadForwardingTests {

  /// <summary>MOV AX,[BP-8] - the reload under test (8B 46 F8).</summary>
  private static bool HasReload(byte[] image) {
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x8B && image[i + 1] == 0x46 && image[i + 2] == 0xF8)
        return true;
    return false;
  }

  private static Assembler Store() {
    var asm = new Assembler { EnableLoadForwarding = true };
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.AX);      // MOV [BP-8],AX
    return asm;
  }

  [Test]
  public void Forward_GivenReloadOfJustStoredCell_WhenAssembled_ThenReloadRemoved() {
    var asm = Store();
    asm.Mov(Reg.CX, Mem.Word(Reg.BP, -4));      // touches neither AX nor the cell
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));      // dead: AX still holds it
    Assert.That(HasReload(asm.ToArray()), Is.False);
  }

  [Test]
  public void Forward_GivenConditionalJumpBetween_WhenAssembled_ThenReloadRemoved() {
    // the reload sits in the branch's fall-through path, so reaching it IS reaching it from the
    // store - and with no label in between nothing can enter the range from anywhere else
    var asm = Store();
    var over = asm.DefineLabel();
    asm.Cmp(Reg.AX, Mem.Word(Reg.BP, -4));
    asm.J(Condition.LessOrEqual, over);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    asm.MarkLabel(over);
    asm.Ret();
    Assert.That(HasReload(asm.ToArray()), Is.False);
  }

  [Test]
  public void Forward_GivenDifferentRegister_WhenAssembled_ThenRegisterMove() {
    var asm = Store();
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, -8));      // AX still holds it: MOV DX,AX beats a memory read
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(HasReload(image), Is.False);
      Assert.That(image[^2..], Is.EqualTo(new byte[] { 0x89, 0xC2 }), "MOV DX,AX");
    });
  }

  [Test]
  public void Forward_GivenStoredImmediate_WhenAssembled_ThenLoadBecomesImmediate() {
    var asm = new Assembler { EnableLoadForwarding = true };
    asm.Mov(Mem.Word(Reg.BP, -8), (Imm)7);      // MOV WORD PTR [BP-8],7
    asm.Mov(Reg.DI, Mem.Word(Reg.BP, -8));      // the cell's value is known: MOV DI,7
    var image = asm.ToArray();
    Assert.That(image[^3..], Is.EqualTo(new byte[] { 0xBF, 0x07, 0x00 }), "MOV DI,7");
  }

  [Test]
  public void Forward_GivenStoredLabelOffset_WhenAssembled_ThenReloadKept() {
    // MOV WORD PTR [BP-8],OFFSET cell is emitted with a ZERO PLACEHOLDER and the address written in
    // when the label resolves, so the immediate read out of the buffer here is not the value the
    // instruction will carry. Forwarding it turned the reload into MOV DI,0 - a cell holding an
    // address read as if it held nothing, which is how DATAREAD.BAS printed a garbage string at -O1
    // and the right one at -O0.
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(Reg.BP, -8), Imm.OffsetOf(cell));
    asm.Mov(Reg.DI, Mem.Word(Reg.BP, -8));
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0x1234);
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[5..8], Is.EqualTo(new byte[] { 0x8B, 0x7E, 0xF8 }), "MOV DI,[BP-8] survives");
      Assert.That(image[3..5], Is.Not.EqualTo(new byte[] { 0x00, 0x00 }), "and the store carries the resolved address");
    });
  }

  [Test]
  public void Forward_GivenInterveningWriteToRegister_WhenAssembled_ThenReloadKept() {
    var asm = Store();
    asm.Mov(Reg.AX, Reg.CX);                    // AX no longer holds the stored value
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    Assert.That(HasReload(asm.ToArray()), Is.True);
  }

  [Test]
  public void Forward_GivenInterveningStoreToSameCell_WhenAssembled_ThenForwardsTheLaterValue() {
    // the cell no longer holds AX - it holds CX, and that is what the load must produce
    var asm = Store();
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    var image = asm.ToArray();
    Assert.That(image[^2..], Is.EqualTo(new byte[] { 0x89, 0xC8 }), "MOV AX,CX - not AX,AX");
  }

  [Test]
  public void Forward_GivenLabelBetween_WhenAssembled_ThenReloadKept() {
    // something may branch to the label and reach the load without ever running the store
    var asm = Store();
    var entry = asm.DefineLabel();
    asm.Jmp(entry);
    asm.MarkLabel(entry);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    Assert.That(HasReload(asm.ToArray()), Is.True);
  }

  [Test]
  public void Forward_GivenCallBetween_WhenAssembled_ThenReloadKept() {
    // an unrecorded instruction is a barrier: a callee clobbers registers and memory
    var asm = Store();
    var target = asm.DefineLabel();
    asm.Call(target);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    asm.MarkLabel(target);
    asm.Ret();
    Assert.That(HasReload(asm.ToArray()), Is.True);
  }

  [Test]
  public void Forward_GivenDataSegmentCell_WhenAssembled_ThenReloadKept() {
    // only BP-relative (SS) cells qualify: a [label] cell can be re-pointed by a segment load
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(cell), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(cell));
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0);
    var image = asm.ToArray();
    var loads = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8B && image[i + 1] == 0x06)
        ++loads;
    Assert.That(loads, Is.EqualTo(1), "the DS-relative reload is kept");
  }

  [Test]
  public void Forward_GivenGateOff_WhenAssembled_ThenReloadKept() {
    var asm = new Assembler();
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    Assert.That(HasReload(asm.ToArray()), Is.True);
  }
}
