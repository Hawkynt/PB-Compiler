using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class ComWriterTests {

  [Test]
  public void Write_GivenInternalAbsoluteOffset_ThenRebasesItByPspLoadOffset() {
    var asm = new Assembler();
    var data = asm.DefineLabel();
    asm.Mov(Reg.AX, Imm.OffsetOf(data));
    asm.MarkLabel(data);
    asm.Nop();

    var relocatable = asm.ToRelocatable();
    var com = ComWriter.Write(relocatable);

    Assert.Multiple(() => {
      Assert.That(relocatable.Relocations.Select(r => r.Kind), Does.Contain(AsmRelocationKind.Absolute));
      Assert.That(com, Is.EqualTo(new byte[] { 0xB8, 0x03, 0x01, 0x90 }));
    });
  }

  [Test]
  public void Write_GivenRelativeBranch_ThenItsDisplacementIsNotRebased() {
    var asm = new Assembler();
    var target = asm.DefineLabel();
    asm.Jmp(target);
    asm.Nop();
    asm.MarkLabel(target);
    asm.Nop();

    var relocatable = asm.ToRelocatable();
    var com = ComWriter.Write(relocatable);

    Assert.That(com, Is.EqualTo(relocatable.Image), "PC-relative control flow is load-address independent");
  }

  [Test]
  public void Write_GivenSegmentRelocation_ThenRejectsComAsUnrepresentable() {
    var asm = new Assembler();
    asm.Mov(Reg.AX, Imm.Segment());

    var error = Assert.Throws<InvalidDataException>(() => ComWriter.Write(asm.ToRelocatable()));

    Assert.That(error!.Message, Does.Contain("segment relocation"));
  }

  [Test]
  public void Write_GivenVirtualBssPastFfff_ThenRejectsTheImage() {
    var image = new RelocatableImage([0x90], [], new Dictionary<string, int>());

    var error = Assert.Throws<InvalidDataException>(
      () => ComWriter.Write(image, ComWriter.MaximumImageBytes + 1));

    Assert.That(error!.Message, Does.Contain("virtual BSS"));
  }
}
