using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Asm;

[TestFixture]
public sealed class TextAssemblerImmediateRangeTests {
  [Test]
  public void TryParse_GivenMinimumSignedDwordDecimal_ThenEncodesItsBitPattern() {
    var assembler = new Assembler();
    var parser = new TextAssembler(assembler);

    var parsed = parser.TryParse("MOV EAX, -2147483648", null, out var error);

    Assert.Multiple(() => {
      Assert.That(parsed, Is.True, error);
      Assert.That(error, Is.Null);
      Assert.That(assembler.ToArray(), Is.EqualTo(new byte[] { 0x66, 0xB8, 0x00, 0x00, 0x00, 0x80 }));
    });
  }

  [Test]
  public void TryParse_GivenDecimalAboveDwordBitPatternRange_ThenRejectsIt() {
    var assembler = new Assembler();
    var parser = new TextAssembler(assembler);

    var parsed = parser.TryParse("MOV EAX, 4294967296", null, out var error);

    Assert.Multiple(() => {
      Assert.That(parsed, Is.False);
      Assert.That(error, Does.Contain("out of range"));
      Assert.That(assembler.Position, Is.Zero);
    });
  }
}
