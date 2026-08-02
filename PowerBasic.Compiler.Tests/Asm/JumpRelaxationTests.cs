using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

/// <summary>
/// S1 $OPTIMIZE SIZE: short-jump relaxation. A near JMP (E9 rel16, 3 bytes) or near Jcc
/// (0F 8x rel16, 4 bytes) whose displacement fits a signed byte is rewritten to the short
/// form (EB / 7x rel8, 2 bytes); every label/fixup slides via the peephole's cut machinery.
/// Iterated to fixpoint - each shrink can bring further jumps into short range.
/// </summary>
[TestFixture]
public sealed class JumpRelaxationTests {

  [Test]
  public void Relax_GivenNearJmpInRange_WhenAssembled_ThenShortForm() {
    var asm = new Assembler { EnableJumpRelaxation = true };
    var target = asm.DefineLabel();
    asm.Jmp(target);         // E9 xx xx -> EB xx
    asm.Nop();
    asm.MarkLabel(target);
    asm.Ret();
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[0], Is.EqualTo(0xEB), "near JMP relaxes to the short form");
      Assert.That((sbyte)image[1], Is.EqualTo(1), "displacement re-resolved for the 2-byte encoding");
      Assert.That(image.Length, Is.EqualTo(4), "one byte saved");
    });
  }

  [Test]
  public void Relax_GivenNearJccInRange_WhenAssembled_ThenShortForm() {
    var asm = new Assembler { EnableJumpRelaxation = true };
    var target = asm.DefineLabel();
    asm.Jz(target);          // 0F 84 xx xx -> 74 xx
    asm.Nop();
    asm.MarkLabel(target);
    asm.Ret();
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[0], Is.EqualTo(0x74), "near JZ relaxes to the short form");
      Assert.That((sbyte)image[1], Is.EqualTo(1));
      Assert.That(image.Length, Is.EqualTo(4), "two bytes saved");
    });
  }

  [Test]
  public void Relax_GivenBackwardJump_WhenAssembled_ThenShortFormWithNegativeDisp() {
    var asm = new Assembler { EnableJumpRelaxation = true };
    var top = asm.DefineLabel();
    asm.MarkLabel(top);
    asm.Nop();
    asm.Jmp(top);
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[1], Is.EqualTo(0xEB));
      Assert.That((sbyte)image[2], Is.EqualTo(-3), "backward displacement from the shrunk encoding");
    });
  }

  [Test]
  public void Relax_GivenFarTarget_WhenAssembled_ThenNearFormKept() {
    var asm = new Assembler { EnableJumpRelaxation = true };
    var target = asm.DefineLabel();
    asm.Jmp(target);
    for (var i = 0; i < 200; ++i)
      asm.Nop();
    asm.MarkLabel(target);
    asm.Ret();
    var image = asm.ToArray();
    Assert.That(image[0], Is.EqualTo(0xE9), "a 200-byte hop stays near");
  }

  [Test]
  public void Relax_GivenChainNearThreshold_WhenAssembled_ThenFixpointShrinksBoth() {
    // the first jump's target is ~129 bytes away and comes into short range only after
    // the second jump (inside the gap) has itself been shrunk - requires iteration
    var asm = new Assembler { EnableJumpRelaxation = true };
    var far = asm.DefineLabel();
    var near = asm.DefineLabel();
    asm.Jmp(far);            // distance 129 near-encoded, 128 too far... after inner shrink -> 128 fits? boundary probe
    for (var i = 0; i < 60; ++i)
      asm.Nop();
    asm.Jmp(near);           // short-range inner jump
    asm.Nop();
    asm.MarkLabel(near);
    for (var i = 0; i < 64; ++i)
      asm.Nop();
    asm.MarkLabel(far);
    asm.Ret();
    var image = asm.ToArray();
    Assert.That(image[0], Is.EqualTo(0xEB), "outer jump fits once the inner one shrank");
  }

  [Test]
  public void Relax_GivenFixupImmediatelyAfterCut_WhenAssembled_ThenItSurvivesAndResolves() {
    // the cut takes the two surplus bytes of the Jcc; the NEXT instruction's fixup sits just
    // past it and slides down onto a position inside the cut window. A removal pass run after
    // the slide deletes it - the reference (here a data label's offset) then resolves to 0.
    var asm = new Assembler { EnableJumpRelaxation = true };
    var over = asm.DefineLabel();
    var data = asm.DefineLabel();
    asm.Jz(over);                              // 0F 84 xx xx -> 74 xx (two bytes cut)
    asm.Mov(Reg.SI, Imm.OffsetOf(data));       // its Abs16 fixup lands right after the cut
    asm.MarkLabel(over);
    asm.Ret();
    asm.MarkLabel(data);
    asm.Db(0x42);
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(image[0], Is.EqualTo(0x74), "the branch relaxed");
      var offset = image[3] | (image[4] << 8);
      Assert.That(offset, Is.Not.Zero, "the following instruction's fixup must not be swallowed by the cut");
      Assert.That(image[offset], Is.EqualTo(0x42), "and it still points at the data label");
    });
  }

  [Test]
  public void Relax_GivenAnonymousLabelPastACut_WhenAssembled_ThenItStillPointsAtItsInstruction() {
    // DefineLabel() hands out labels that neither _namedLabels nor any fixup registers. A cut
    // still has to slide them: tail-merge delimits its fold regions with exactly such labels,
    // and a stale boundary makes it compare - and potentially fold - the wrong byte range.
    var asm = new Assembler { EnableJumpRelaxation = true };
    var over = asm.DefineLabel();
    var mark = asm.DefineLabel();
    asm.Jz(over);                              // 0F 84 xx xx -> 74 xx: a two-byte cut before `mark`
    asm.MarkLabel(over);
    asm.MarkLabel(mark);                       // referenced by nothing - only its Position matters
    asm.Db(0x42);
    var image = asm.ToArray();
    Assert.Multiple(() => {
      Assert.That(mark.Position, Is.EqualTo(2), "the anonymous label slid with the cut");
      Assert.That(image[mark.Position], Is.EqualTo(0x42), "and still marks its own instruction");
    });
  }

  [Test]
  public void Relax_GivenGateOff_WhenAssembled_ThenNearFormsKept() {
    var asm = new Assembler();
    var target = asm.DefineLabel();
    asm.Jmp(target);
    asm.MarkLabel(target);
    asm.Ret();
    var image = asm.ToArray();
    Assert.That(image[0], Is.EqualTo(0xE9), "without the gate the faithful stream is kept");
  }
}
