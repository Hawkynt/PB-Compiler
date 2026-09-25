using PowerBasic.Compiler.Asm;
using PowerBasic.Compiler.Emit;

namespace PowerBasic.Compiler.Tests.Emit;

[TestFixture]
public sealed class ComWriterTests {

  [Test]
  public void Write_GivenInternalAbsoluteOffset_ThenRebasesItByPspLoadOffset() {
    var asm = new Assembler();
    asm.Db(new byte[ComWriter.LoadOffset]);
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
    asm.Db(new byte[ComWriter.LoadOffset]);
    var target = asm.DefineLabel();
    asm.Jmp(target);
    asm.Nop();
    asm.MarkLabel(target);
    asm.Nop();

    var relocatable = asm.ToRelocatable();
    var com = ComWriter.Write(relocatable);

    Assert.That(com, Is.EqualTo(relocatable.Image[ComWriter.LoadOffset..]),
      "PC-relative control flow is load-address independent");
  }

  [Test]
  public void Write_GivenSegmentRelocation_ThenRejectsComAsUnrepresentable() {
    var asm = new Assembler();
    asm.Db(new byte[ComWriter.LoadOffset]);
    asm.Mov(Reg.AX, Imm.Segment());

    var error = Assert.Throws<InvalidDataException>(() => ComWriter.Write(asm.ToRelocatable()));

    Assert.That(error!.Message, Does.Contain("segment relocation"));
  }

  [Test]
  public void Write_GivenVirtualBssPastFfff_ThenRejectsTheImage() {
    var image = new RelocatableImage(new byte[ComWriter.LoadOffset + 1], [], new Dictionary<string, int>());

    var error = Assert.Throws<InvalidDataException>(
      () => ComWriter.Write(image, 0x10001));

    Assert.That(error!.Message, Does.Contain("virtual BSS"));
  }
}
