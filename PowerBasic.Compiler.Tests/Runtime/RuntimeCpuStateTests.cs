using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Tests.Runtime;

[TestFixture]
public sealed class RuntimeCpuStateTests {
  [Test]
  public void SseSetup_EnablesCr0Cr4WithoutTouchingXcr0() {
    var asm = new Assembler();
    asm.EnableExtendedVectorState(avx: false, avx512: false);
    var bytes = asm.ToArray();

    Assert.Multiple(() => {
      Assert.That(Contains(bytes, [0x0F, 0x20, 0xC0]), Is.True, "MOV EAX,CR0");
      Assert.That(Contains(bytes, [0x0F, 0x22, 0xC0]), Is.True, "MOV CR0,EAX");
      Assert.That(Contains(bytes, [0x0F, 0x20, 0xE0]), Is.True, "MOV EAX,CR4");
      Assert.That(Contains(bytes, [0x0F, 0x22, 0xE0]), Is.True, "MOV CR4,EAX");
      Assert.That(Contains(bytes, [0x0F, 0x01, 0xD0]), Is.False, "SSE alone has no XGETBV requirement");
    });
  }

  [Test]
  public void AvxSetup_ProgramsXcr0() {
    var asm = new Assembler();
    asm.EnableExtendedVectorState(avx: true, avx512: false);
    var bytes = asm.ToArray();

    Assert.Multiple(() => {
      Assert.That(Contains(bytes, [0x0F, 0x01, 0xD0]), Is.True, "XGETBV");
      Assert.That(Contains(bytes, [0x0F, 0x01, 0xD1]), Is.True, "XSETBV");
    });
  }

  private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i <= haystack.Length - needle.Length; ++i)
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        return true;
    return false;
  }
}
