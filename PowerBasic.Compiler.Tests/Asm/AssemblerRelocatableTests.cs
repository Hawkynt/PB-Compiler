using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class AssemblerRelocatableTests {

  #region external labels

  [Test]
  public void External_GivenName_WhenCreated_ThenFlaggedAndNotBindable() {
    var asm = new Assembler();
    var label = asm.External("rt_print_str");

    Assert.That(label.IsExternal, Is.True);
    Assert.That(label.IsBound, Is.False);
    Assert.Throws<InvalidOperationException>(() => asm.MarkLabel(label));
  }

  [Test]
  public void External_GivenAlreadyBoundName_WhenFlagged_ThenThrows() {
    var asm = new Assembler();
    asm.MarkLabel("here");
    Assert.Throws<InvalidOperationException>(() => asm.External("here"));
  }

  [Test]
  public void ToArray_GivenCallToExternal_WhenResolved_ThenStillThrows() {
    var asm = new Assembler();
    asm.Call(asm.External("rt_exit"));
    Assert.Throws<InvalidOperationException>(() => asm.ToArray());
  }

  #endregion

  #region ToRelocatable

  [Test]
  public void ToRelocatable_GivenOnlyInternalReferences_WhenResolved_ThenImageMatchesToArray() {
    byte[] Emit(Func<Assembler, byte[]> finish) {
      var asm = new Assembler();
      var top = asm.DefineLabel();
      asm.MarkLabel(top);
      asm.Mov(Reg.AX, 1);
      asm.Jmp(top);
      return finish(asm);
    }

    Assert.That(Emit(a => a.ToRelocatable().Image), Is.EqualTo(Emit(a => a.ToArray())));
  }

  [Test]
  public void ToRelocatable_GivenCallToExternal_WhenResolved_ThenExternalRelativeSiteReported() {
    var asm = new Assembler();
    asm.Nop();
    asm.Call(asm.External("rt_print_str"));

    var relocatable = asm.ToRelocatable();

    // NOP (1) + E8 opcode (1) -> displacement site at 2, operand left zero
    Assert.That(relocatable.Relocations, Is.EqualTo(new[] { new AsmRelocation(2, AsmRelocationKind.ExternalRelative, "rt_print_str") }));
    Assert.That(relocatable.Image[2] | relocatable.Image[3] << 8, Is.Zero);
  }

  [Test]
  public void ToRelocatable_GivenUnboundNamedLabel_WhenResolved_ThenTreatedAsExternal() {
    var asm = new Assembler();
    asm.Call(asm.Lbl("rt_readdata")); // codegen style: named, never bound in a unit

    var relocatable = asm.ToRelocatable();

    Assert.That(relocatable.Relocations.Single().Kind, Is.EqualTo(AsmRelocationKind.ExternalRelative));
    Assert.That(relocatable.Relocations.Single().Symbol, Is.EqualTo("rt_readdata"));
  }

  [Test]
  public void ToRelocatable_GivenAbsoluteExternalWithAddend_WhenResolved_ThenAddendStoredInSite() {
    var asm = new Assembler();
    asm.Mov(Reg.AX, Imm.OffsetOf(asm.External("rt_datapool"), 6));

    var relocatable = asm.ToRelocatable();

    var site = relocatable.Relocations.Single(r => r.Kind == AsmRelocationKind.ExternalAbsolute).Site;
    Assert.That(relocatable.Image[site] | relocatable.Image[site + 1] << 8, Is.EqualTo(6));
  }

  [Test]
  public void ToRelocatable_GivenMemoryOperandOnExternal_WhenResolved_ThenExternalAbsoluteSiteReported() {
    var asm = new Assembler();
    asm.Mov(Mem.Word(asm.External("rt_onerr")), Reg.AX);

    var relocations = asm.ToRelocatable().Relocations;

    Assert.That(relocations.Single().Kind, Is.EqualTo(AsmRelocationKind.ExternalAbsolute));
    Assert.That(relocations.Single().Symbol, Is.EqualTo("rt_onerr"));
  }

  [Test]
  public void ToRelocatable_GivenInternalAbsoluteReference_WhenResolved_ThenAbsoluteSiteReported() {
    var asm = new Assembler();
    var data = asm.DefineLabel("v_x");
    asm.Mov(Reg.AX, Mem.Word(data));
    asm.Ret();
    asm.MarkLabel(data);
    asm.Dw(0x1234);

    var relocatable = asm.ToRelocatable();

    var absolute = relocatable.Relocations.Single(r => r.Kind == AsmRelocationKind.Absolute);
    Assert.That(relocatable.Image[absolute.Site] | relocatable.Image[absolute.Site + 1] << 8, Is.EqualTo(data.Position));
  }

  [Test]
  public void ToRelocatable_GivenSegmentWord_WhenResolved_ThenSegmentSiteReported() {
    var asm = new Assembler();
    asm.Mov(Reg.AX, Imm.Segment());

    Assert.That(asm.ToRelocatable().Relocations.Single().Kind, Is.EqualTo(AsmRelocationKind.Segment));
  }

  [Test]
  public void ToRelocatable_GivenUnboundUnnamedLabel_WhenResolved_ThenThrows() {
    var asm = new Assembler();
    asm.Jmp(asm.DefineLabel());
    Assert.Throws<InvalidOperationException>(() => asm.ToRelocatable());
  }

  [Test]
  public void ToRelocatable_GivenShortJumpToExternal_WhenResolved_ThenThrows() {
    var asm = new Assembler();
    asm.JmpShort(asm.External("rt_exit"));
    Assert.Throws<InvalidOperationException>(() => asm.ToRelocatable());
  }

  [Test]
  public void ToRelocatable_GivenBoundNamedLabels_WhenResolved_ThenSymbolTableReturned() {
    var asm = new Assembler();
    asm.Nop();
    asm.MarkLabel("rt_exit");
    asm.Ret();

    var bound = asm.ToRelocatable().BoundLabels;

    Assert.That(bound["RT_EXIT"], Is.EqualTo(1), "lookup must be case-insensitive");
    Assert.That(bound, Has.Count.EqualTo(1), "unbound/external names must not appear");
  }

  #endregion
}
