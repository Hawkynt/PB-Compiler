using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// Redundant-load and dead-store elimination over BP-relative frame cells:
/// <c>MOV [cell],R … MOV R,[cell]</c> leaves R already holding the value, so the reload is dead,
/// and a store the next store to the same cell fully overwrites is dead too. Deliberately narrow -
/// the cases the pass must DECLINE are as much the specification as the cases it takes.
/// </summary>
[TestFixture]
public sealed class LoadForwardingTests {

  /// <summary>MOV AX,[BP-8] - the frame reload under test (8B 46 F8).</summary>
  private static bool HasReload(byte[] image) {
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x8B && image[i + 1] == 0x46 && image[i + 2] == 0xF8)
        return true;
    return false;
  }

  /// <summary>Count register-to-word stores MOV [BP-8],r16 (89 /r, disp8 F8).</summary>
  private static int CountRegisterStores(byte[] image) {
    var count = 0;
    for (var i = 0; i + 2 < image.Length; ++i)
      if (image[i] == 0x89 && (image[i + 1] & 0xC7) == 0x46 && image[i + 2] == 0xF8)
        ++count;
    return count;
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
  public void DeadStore_GivenSameCellOverwrittenBeforeAnyRead_WhenAssembled_ThenOlderStoreRemoved() {
    var asm = Store();
    asm.Mov(Reg.DX, Reg.BX);                    // unrelated register work does not observe the cell
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);      // complete overwrite
    var image = asm.ToArray();
    Assert.That(CountRegisterStores(image), Is.EqualTo(1), "only the final MOV [BP-8],CX survives");
  }

  [Test]
  public void DeadStore_GivenOnlyReaderWasForwarded_WhenAssembled_ThenOlderStoreBecomesDead() {
    // O0034 first rewrites MOV DX,[BP-8] to MOV DX,AX. O0065 can then see that nothing observes
    // the frame cell before the CX overwrite and remove the otherwise pointless first store.
    var asm = Store();
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, -8));
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(CountRegisterStores(image), Is.EqualTo(1));
      Assert.That(image[..2], Is.EqualTo(new byte[] { 0x89, 0xC2 }), "forwarded MOV DX,AX remains");
    });
  }

  [Test]
  public void DeadStore_GivenSurvivingAliasingRead_WhenAssembled_ThenOlderStoreKept() {
    var asm = Store();
    asm.Add(Reg.DX, Mem.Word(Reg.BP, -8));       // observes the old AX value in memory
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
  }

  [Test]
  public void DeadStore_GivenReadAfterSourceRegisterChanged_WhenAssembled_ThenOlderStoreKept() {
    // the load cannot forward because AX was overwritten; it remains a real memory observation.
    var asm = Store();
    asm.Mov(Reg.AX, Reg.BX);
    asm.Mov(Reg.DX, Mem.Word(Reg.BP, -8));
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
  }

  [Test]
  public void DeadStore_GivenPartialOverwrite_WhenAssembled_ThenOlderWordStoreKept() {
    // conservative boundary: a byte write is not accepted as a complete replacement of the word.
    var asm = Store();
    asm.Mov(Mem.Byte(Reg.BP, -8), (Imm)1);
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
  }

  [Test]
  public void DeadStore_GivenReadModifyWrite_WhenAssembled_ThenOlderStoreKept() {
    // ADD [BP-8],1 needs the value written by the first MOV; it is not a killing store.
    var asm = Store();
    asm.Add(Mem.Word(Reg.BP, -8), (Imm)1);
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
  }

  [Test]
  public void DeadStore_GivenLabelBeforeOverwrite_WhenAssembled_ThenOlderStoreKept() {
    var asm = Store();
    var entry = asm.DefineLabel();
    asm.MarkLabel(entry);                       // another path may enter the region here
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
  }

  [Test]
  public void DeadStore_GivenCallBeforeOverwrite_WhenAssembled_ThenOlderStoreKept() {
    var asm = Store();
    var target = asm.DefineLabel();
    asm.Call(target);                           // unrecorded barrier may observe/mutate memory
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.CX);
    asm.Ret();
    asm.MarkLabel(target);
    asm.Ret();
    Assert.That(CountRegisterStores(asm.ToArray()), Is.EqualTo(2));
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
  public void Forward_GivenDirectVariableCell_WhenAssembled_ThenReloadRemoved() {
    // O0083: an ordinary BASIC variable is a direct DS-relative label. With no unrecorded
    // instruction between the store and load, DS cannot have changed and AX still has the value.
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(cell), Reg.AX);             // A3 cell
    asm.Mov(Reg.AX, Mem.Word(cell));             // 8B 06 cell - dead
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0);
    var image = asm.ToArray();
    var loads = 0;
    for (var i = 0; i + 1 < image.Length; ++i)
      if (image[i] == 0x8B && image[i + 1] == 0x06)
        ++loads;
    Assert.That(loads, Is.Zero, "the direct-variable reload is removed");
  }

  [Test]
  public void Forward_GivenDirectVariableLoadedIntoDifferentRegister_WhenAssembled_ThenRegisterMove() {
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(cell), Reg.AX);
    asm.Mov(Reg.DX, Mem.Word(cell));
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0);
    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x89, 0xC2]), Is.GreaterThanOrEqualTo(0), "MOV DX,AX replaces the memory load");
  }

  [Test]
  public void Forward_GivenDirectVariableWithSegmentOverride_WhenAssembled_ThenReloadKept() {
    // an explicit segment changes the addressed cell; keep the operation rather than treating the
    // label identity alone as sufficient.
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(cell).Es(), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(cell).Es());
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0);
    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x26, 0x8B, 0x06]), Is.GreaterThanOrEqualTo(0), "ES-relative reload survives");
  }

  [Test]
  public void Forward_GivenSegmentRegisterChangeBetweenDirectAccesses_WhenAssembled_ThenReloadKept() {
    // MOV DS,... is intentionally unrecorded and therefore splits the proof chain.
    var asm = new Assembler { EnableLoadForwarding = true };
    var cell = asm.DefineLabel();
    asm.Mov(Mem.Word(cell), Reg.AX);
    asm.Mov(Reg.DS, Reg.BX);
    asm.Mov(Reg.AX, Mem.Word(cell));
    asm.Ret();
    asm.MarkLabel(cell);
    asm.Dw(0);
    var image = asm.ToArray();
    Assert.That(IndexOf(image, [0x8B, 0x06]), Is.GreaterThanOrEqualTo(0), "reload after changing DS survives");
  }

  [Test]
  public void Forward_GivenGateOff_WhenAssembled_ThenReloadKept() {
    var asm = new Assembler();
    asm.Mov(Mem.Word(Reg.BP, -8), Reg.AX);
    asm.Mov(Reg.AX, Mem.Word(Reg.BP, -8));
    Assert.That(HasReload(asm.ToArray()), Is.True);
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
